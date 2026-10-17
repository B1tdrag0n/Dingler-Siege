extern alias HexGame;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Dingler.Game.Cards;
using Dingler.Game.GameObjects;
using Dingler.Game.GameObjects.TrackedGameZones;
using Dingler.Game.Replays;
using Dingler.Game.Services;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Mechanics;
using HexGame::Game.Shared.Mechanics.Abilities;
using HexGame::Game.Shared.Mechanics.Transactions;
using HexGame::Reckoning.Game;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Games;

public sealed partial class HexRulesEngine : AuthoritativeSessionBase, IDisposable
{ 
	private readonly Channel<bool> _workSignal = Channel.CreateUnbounded<bool>();
	private readonly SemaphoreSlim _semaphoreSlim = new(1);
	private readonly ILogger<HexRulesEngine>? _logger;
	private readonly CardVisibilityManager _cardVisibilityManager;
	private readonly GameOptionService _gameOptionService;
	private readonly CardStatManager _cardStatManager;
	public UID Winner { get; private set; }
	public UID Loser { get; private set; }
	public SessionStateEncounterData EncounterData => m_EncounterData;

	private bool _combatListingDirty;
	private bool _lastSentCombatListingWasEmpty;
	
	public event Action<List<UID>, List<UID>>? GameEnded;
	public event Action? FlushReady;
	public event Action<Player, SessionEventArgs>? DispatchToPlayer;
	
	public HexRulesEngine(string sesName, UID sesId, ulong randomNumberGeneratorSeedZ,
		ulong randomNumberGeneratorSeedW, ILogger<HexRulesEngine>? logger = null) : base(
		sesName, sesId, randomNumberGeneratorSeedZ, randomNumberGeneratorSeedW)
	{
		_cardVisibilityManager = new CardVisibilityManager();
		_cardStatManager = new CardStatManager();
		_gameOptionService = new GameOptionService(this, _cardStatManager);
		_logger = logger;
	}
	
	public bool IsGameEnded { get; private set; }

	/// <summary>Records the game for a replay when set (Siege fights). Set before the game starts.</summary>
	public ReplayRecorder? Recorder { get; init; }

	public HexRulesEngine(string sesName, UID sesId, ILogger<HexRulesEngine>? logger = null) :
		this(sesName, sesId, (ulong)DateTime.Now.Ticks, (ulong)Guid.NewGuid().GetHashCode(), logger)
	{
	}

	public override bool Update()
	{
		return Tick();
	}
	
	public void ProcessWork()
	{
		_workSignal.Writer.TryWrite(true);
	}
	
	public async Task<List<UID>> RunAsync(CancellationToken token)
	{

		try
		{
			await _semaphoreSlim.WaitAsync(token);

			while (await _workSignal.Reader.ReadAsync(token).ConfigureAwait(false))
			{
				try
				{
					bool continueProcessing;
					do
					{
						continueProcessing = Update();
						SendChangedCardUpdates();
						FlushReady?.Invoke();

						// Arena: the AI seat asked to void the battle (design 03 D15-A); end it here so a flush follows.
						if (_voidRequested && !IsGameEnded)
						{
							EndArenaBattleAsVoid();
							FlushReady?.Invoke();
							break;
						}
					} while (continueProcessing);

					if (IsGameEnded)
					{
						while (_workSignal.Reader.TryRead(out _)) ;
						break;
					}
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					return new List<UID>();
				}
				catch (Exception ex)
				{
					_logger?.LogError("Match {SessionId} engine pump failed: {Exception}", m_SessionId, ex);
					if (!IsGameEnded)
					{
						try
						{
							if (IsPvEArena() || IsSiegeBattle)
							{
								_voidRequested = true;
								VoidReason ??= "engine-exception: " + ex.Message;
								EndArenaBattleAsVoid();
							}
							else
								EndGame(new List<UID>(), m_Players.Select(p => p.m_PlayerId).ToList(), forceEnd: true);
						}
						catch
						{
							// ignored
						}
					}
					
					FlushReady?.Invoke();

					// The crash path ended the game: leave, so the match is cleaned up (it used to wait for a signal
					// that never came, keeping the finished game registered for hours).
					if (IsGameEnded)
					{
						while (_workSignal.Reader.TryRead(out _)) ;
						break;
					}
				}
			}
		}
		finally
		{
			_logger?.LogDebug("Releasing semaphore");
			_semaphoreSlim.Release();
			_logger?.LogDebug("Semaphore released");
		}

		_logger?.LogDebug("Game has ended. Returning");
		m_EncounterData.MatchPreviousWinners?.Add(Winner.GetInstanceId());
		return
		[
			Winner,
			Loser
		];
	}

	public override void DispatchSessionEvent(SessionEventArgs args)
	{
		foreach (var player in m_Players)
		{
			DispatchSessionEvent(player, args);
		}
	}
	
	public override void DispatchSessionEvent(Player player, SessionEventArgs args)
	{
		// Before the AI seat takes its events away, so a replay has both sides.
		if (Recorder is not null)
		{
			try
			{
				Recorder.Record(player.m_PlayerId, args);
			}
			catch (Exception ex)
			{
				_logger?.LogWarning(ex, "Replay: couldn't record {EventType} for player {PlayerId}",
					args.GetType().Name, player.m_PlayerId);
			}
		}

		if (IsAiSeat(player))
		{
			AiSeat!.Enqueue(args);
			return;
		}

		try
		{
			DispatchToPlayer?.Invoke(player, args);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Dropped {EventType} for player {PlayerId}",
				args.GetType().Name, player.m_PlayerId);
		}
	}
	public override bool SubmitTransaction(Transaction transaction)
	{
		m_TransactionQueue.Enqueue(transaction);
		_workSignal.Writer.TryWrite(true);
		return true;
	}

	public override bool IsWaitingOnTransaction()
	{
		return !m_TransactionQueue.IsEmpty;
	}

	protected override void UpdateThread()
	{
		
	}

	public override bool PlayerCanCheat(UID player)
	{
		return false;
	}

	public override bool WantChecksumFailData()
	{
		return false;
	}

	public override void TrackEvent(UID player, string ev_type, params string[] kvs)
	{
		
	}

	public override void PublishEvent(string key, object value)
	{
		
	}

	public override void ChangeCollectionCardTemplate(UID player, Card targetCard, ResourceId oldTemplate, ResourceId newTemplate)
	{
		
	}

	public override bool HasDeckAndChampInfo(UID player, 
		[MaybeNullWhen(false)] out champion_bits champ,
		[MaybeNullWhen(false)] out deck_bits deck,
		[MaybeNullWhen(false)] out string keepname,
		[MaybeNullWhen(false)] out Dictionary<string, int> dungeonIntTacData,
		[MaybeNullWhen(false)] out List<ResourceId> partyMembers)
	{
		champ = null;
		deck = null;
		keepname = null;
		dungeonIntTacData = null;
		partyMembers = null;
		return false;
	}

	protected override bool SendPlayerInformationUpdates(ETurnPhases phase)
	{
		var players = GetAllPlayers();
		foreach (var player in players)
		{
			DispatchSessionEvent(BuildPlayerUpdate(player));
		}

		var combats = CombatManager.GetAllCombatAttacks();

		if (combats.Count == 0)
		{
			if (_lastSentCombatListingWasEmpty && !_combatListingDirty)
				return true;

			if (_combatListingDirty)
			{
				foreach (var player in players)
				{
					DispatchSessionEvent(player, new CombatListingSessionEventArgs()
					{
						PlayerId = player.m_PlayerId,
						SessionId =  m_SessionId,
						Combats = new List<SessionEventArgs>()
					});
				}
				
				_combatListingDirty = false;
			}
			else
			{
				_combatListingDirty = true;
				_lastSentCombatListingWasEmpty = true;
			}

			return true;
		}

		foreach (var player in players)
		{
			DispatchSessionEvent(player, CombatListingFor(player, combats));
		}

		_lastSentCombatListingWasEmpty = false;
		_combatListingDirty = false;
		return true;
	}

	public override bool VoidCard(Card card, Player voidingPlayer, TAC abilityData)
	{
		if (!base.VoidCard(card, voidingPlayer, abilityData))
			return false;

		if (abilityData is not AbilityInstance ability)
			return true;

		var template = ability.m_AbilityTemplate;
		var cost = ability.AbilityXCostData;
		
		if (template is null || !template.tac.GetBool(IntAttrs.Scrounge) || cost is null)
			return true;

		var voidedCards = cost.CardsToVoid;
		
		if (voidedCards.Count == 0)
			return true;

		int recorded = ability.Get(ListAttrs.VoidedCards)?.Count ?? 0;
		if (recorded != voidedCards.Count)
			return true;

		DispatchSessionEvent(new ScroungeSessionEventArgs
		{
			SourceCard = ability.SourceCard?.m_SessionCardId ?? SessionCardId.Invalid,
			VoidedCards = new List<SessionCardId>(voidedCards)
		});

		return true;
	}

	private CombatListingSessionEventArgs CombatListingFor(Player player, List<Combat> combats)
	{
		var combatListing = new CombatListingSessionEventArgs
		{
			PlayerId = player.m_PlayerId,
			SessionId = m_SessionId,
			Combats = new List<SessionEventArgs>()
		};

		foreach (var combat in combats)
		{
			combatListing.Combats.Add(new CombatSessionEventArgs
			{
				PlayerId = player.m_PlayerId,
				SessionId = m_SessionId,
				Id = combat.CombatId,
				Attacker = combat.Attacker?.m_SessionCardId ?? SessionCardId.Invalid,
				Blockers = combat.Blockers.Select(b => b.m_SessionCardId).ToList()
			});
		}

		return combatListing;
	}

	public override bool SendPlayerOptions(Player player)
	{
		if (IsAiSeat(player))
			return AiSendPlayerOptions(player);

		var options = _gameOptionService.CreateOptionListForPlayer(player);
		DispatchSessionEvent(player, options);
		return true;
	}

	public override bool SendPlayerOptionsFor(Player player, Card sourceCard, AbilityTemplate abilityTemplate,
		AbilityInstance abilityInstance)
	{
		if (IsAiSeat(player))
			return AiSendPlayerOptionsFor(player, sourceCard, abilityTemplate, abilityInstance);

		if (!player.m_AcceptedStartingHand)
			return false;
		
		var options = _gameOptionService.CreateOptionListForPlayer(player, sourceCard, abilityTemplate, abilityInstance);
		DispatchSessionEvent(player, options);
		return true;
	}

	public override bool SendPlayerOptionsFor(Player player, List<Card> cards, List<AbilityTemplate> templates,
		List<AbilityInstance> abilityInstances)
	{
		if (IsAiSeat(player))
			return AiSendPlayerOptionsFor(player, cards, templates, abilityInstances);

		var options = _gameOptionService.CreateOptionListForPlayer(player, cards, templates, abilityInstances);
		DispatchSessionEvent(player, options);
		return true;
	}

	public override void RevealCards(Player targetPlayer, List<SessionCardId> sessionCardIds, TAC data, bool inactive)
	{
		if (sessionCardIds.Count == 0)
		{
			return;
		}
		
		List<int> collections = new List<int>();
		List<UID> owningPlayers = new List<UID>();
		List<int> positions = new List<int>();
		
		AbilityInstance? abilityInstance = data as AbilityInstance;
		foreach (SessionCardId sessionCardId in sessionCardIds)
		{
			Card card = ResourceCache.GetCard(sessionCardId);
			collections.Add((int)card.m_CurrentCardCollection);
			owningPlayers.Add(card.m_ControllingPlayer.m_PlayerId);
			positions.Add(0);
			if (abilityInstance == null)
			{
				continue;
			}
			bool flag = false;
			TACList tacList = abilityInstance.Get(ListAttrs.RevealedCards);
			
			if (tacList is not null)
			{
				foreach (TAC item in tacList)
				{
					if (item.Get(IntAttrs.Id) == (int)sessionCardId.InstanceId)
					{
						flag = true;
						break;
					}
				}
			}
			
			if (!flag)
			{
				TAC tAC = new TAC();
				tAC.Set(IntAttrs.Id, (int)sessionCardId.InstanceId);
				abilityInstance.Add(ListAttrs.RevealedCards, tAC);
			}
		}
		
		SendRevealedCards(targetPlayer, sessionCardIds);
		DispatchSessionEvent(targetPlayer, new CardsRevealedSessionEventArgs
		{
			SessionCardIds = SanatizeList(sessionCardIds),
			OwningPlayers = owningPlayers,
			Collections = collections,
			Positions = positions,
			SessionId = m_SessionId,
			PlayerId = targetPlayer.m_PlayerId,
			AbilityInstanceId = ((abilityInstance == null) ? (-1) : abilityInstance.m_AbilityInstanceId),
			Inactive = false
		});
	}

	public override bool SendRevealedCards(Player player, List<SessionCardId> cards)
	{
		if (IsAiSeat(player))
		{
			AiSeat!.NoteReveal(cards);
			return true;
		}

		foreach (var cardId in cards)
		{
			var card = ResourceCache.GetCard(cardId);
			var cardUpdate =
				CardUpdateFactory.CreateUpdateEventForPlayer(player, card, card.m_CurrentCardCollection,
					forceFaceUp: true);
			DispatchSessionEvent(player, cardUpdate);
		}

		return true;
	}
	
	public override bool EndGame(List<UID> winners, List<UID> losers, bool forceEnd = false)
	{
		if (IsGameEnded)
			return false;

		IsGameEnded = true;
		m_GameTerminated = true;
		Winner = winners.Count > 0 ? winners[0] : UID.Invalid;
		Loser = losers.Count > 0 ? losers[0] : UID.Invalid;
		try
		{
			return base.EndGame(winners, losers, forceEnd);
		}
		catch (Exception ex)
		{
			_logger?.LogError("Match {SessionId} base EndGame failed, using fallback: {Exception}", m_SessionId, ex);
			
			if (CurrentTurnPhase != ETurnPhases.EndGame)
				m_StateMachine.Fire(ETurnPhases.EndGame);
			
			m_IsGameStarted = false;
			m_GameIsReadyForCleanup = true;
			
			DispatchSessionEvent(new GameEndedSessionEventArgs
			{
				SessionId = m_SessionId,
				Winners = winners,
				Losers = losers
			});
			
			return true;
		}
		finally
		{
			GameEnded?.Invoke(winners, losers);
		}
	}
	
	private void OnCardLocationUpdated(Card card)
	{
		foreach (var player in GetAllPlayers())
		{
			if (IsAiSeat(player))
				continue;

			if (card.CanPlayerSeeCard(player))
			{
				_cardVisibilityManager.TrySetCardAsVisibleForPlayer(player, card);

				if (card.m_ControllingPlayer.Equals(player) && card is
				    {
					    m_PreviousCardCollection: ECardCollections.Hand,
					    m_CurrentCardCollection: ECardCollections.CastSpells or ECardCollections.PlayedResources
				    }
				    or
				    {
					    m_PreviousCardCollection: ECardCollections.CastSpells,
					    m_CurrentCardCollection: ECardCollections.Deck 
				    })
				{
					continue;
				}
			}
			else
			{
				_cardVisibilityManager.TrySetCardAsInvisibleForPlayer(player, card);
			}

			var cardUpdate = CardUpdateFactory.CreateUpdateEventForPlayer(player, card, card.m_CurrentCardCollection);
			DispatchSessionEvent(player, cardUpdate);
		}
	}

	public override void AddPlayer(Player player)
	{
		RegisterPlayerEvents(player);
		base.AddPlayer(player);
	}
	
	private void RegisterPlayerEvents(Player player)
	{
		if (player is not TrackedPlayer trackedPlayer)
			return;
		
		trackedPlayer.GameTimer.PlayerRanOutOfTime += OnPlayerRanOutOfTime;
		trackedPlayer.GameTimer.TimerStarted += OnTimerStarted;

		// Align the match clock with the session's chess-clock limit (the
		// client's HUD uses the same Session.ChessTimerLimit for its max time).
		if (trackedPlayer.m_Session is not null)
			trackedPlayer.GameTimer.MatchClockLimit = player.m_Session.ChessTimerLimit;

		if (trackedPlayer.m_Deck is TrackedDeck trackedDeck)
		{
			trackedDeck.CardAdded += OnCardLocationUpdated;
		}

		if (trackedPlayer.m_Hand is TrackedHand trackedHand)
		{
			trackedHand.CardAdded += OnCardLocationUpdated;
		}

		if (trackedPlayer.m_Void is TrackedVoid trackedVoid)
		{
			trackedVoid.CardAdded += OnCardLocationUpdated;
		}

		if (trackedPlayer.m_Discard is TrackedDiscard trackedDiscard)
		{
			trackedDiscard.CardAdded += OnCardLocationUpdated;
		}

		if (trackedPlayer.m_CastSpells is TrackedCastSpells trackedCastSpells)
		{
			trackedCastSpells.CardAdded += OnCardLocationUpdated;
		}

		if (trackedPlayer.m_Champions is TrackedChampions trackedChampions)
		{
			trackedChampions.CardAdded += OnCardLocationUpdated;
		}

		if (trackedPlayer.m_PlayedResources is TrackedPlayedResources trackedPlayedResources)
		{
			trackedPlayedResources.CardAdded += OnCardLocationUpdated;
		}

		if (trackedPlayer.m_Simulacrum is TrackedSimulacrum trackedSimulacrum)
		{
			trackedSimulacrum.CardAdded += OnCardLocationUpdated;
		}

		if (trackedPlayer.m_Underground is TrackedUnderground trackedUnderground)
		{
			trackedUnderground.CardAdded += OnCardLocationUpdated;
		}

		if (trackedPlayer.m_Warzone is TrackedWarzone trackedWarzone)
		{
			trackedWarzone.CardAdded += OnCardLocationUpdated;
		}

		if (trackedPlayer.m_Choosing is TrackedChoosing trackedChoosing)
		{
			trackedChoosing.CardAdded += OnCardLocationUpdated;
		}

		if (trackedPlayer.m_None is TrackedNone trackedNone)
		{
			trackedNone.CardAdded += OnCardLocationUpdated;
		}
	}

	private void UnregisterPlayerEvents(Player player)
	{
		if (player is not TrackedPlayer trackedPlayer)
			return;
		
		trackedPlayer.GameTimer.PlayerRanOutOfTime -= OnPlayerRanOutOfTime;
		trackedPlayer.GameTimer.TimerStarted -= OnTimerStarted;

		if (trackedPlayer.m_Deck is TrackedDeck trackedDeck)
		{
			trackedDeck.CardAdded -= OnCardLocationUpdated;
		}

		if (trackedPlayer.m_Hand is TrackedHand trackedHand)
		{
			trackedHand.CardAdded -= OnCardLocationUpdated;
		}

		if (trackedPlayer.m_Void is TrackedVoid trackedVoid)
		{
			trackedVoid.CardAdded -= OnCardLocationUpdated;
		}

		if (trackedPlayer.m_Discard is TrackedDiscard trackedDiscard)
		{
			trackedDiscard.CardAdded -= OnCardLocationUpdated;
		}

		if (trackedPlayer.m_CastSpells is TrackedCastSpells trackedCastSpells)
		{
			trackedCastSpells.CardAdded -= OnCardLocationUpdated;
		}

		if (trackedPlayer.m_Champions is TrackedChampions trackedChampions)
		{
			trackedChampions.CardAdded -= OnCardLocationUpdated;
		}

		if (trackedPlayer.m_PlayedResources is TrackedPlayedResources trackedPlayedResources)
		{
			trackedPlayedResources.CardAdded -= OnCardLocationUpdated;
		}

		if (trackedPlayer.m_Simulacrum is TrackedSimulacrum trackedSimulacrum)
		{
			trackedSimulacrum.CardAdded -= OnCardLocationUpdated;
		}

		if (trackedPlayer.m_Underground is TrackedUnderground trackedUnderground)
		{
			trackedUnderground.CardAdded -= OnCardLocationUpdated;
		}

		if (trackedPlayer.m_Warzone is TrackedWarzone trackedWarzone)
		{
			trackedWarzone.CardAdded -= OnCardLocationUpdated;
		}

		if (trackedPlayer.m_Choosing is TrackedChoosing trackedChoosing)
		{
			trackedChoosing.CardAdded -= OnCardLocationUpdated;
		}

		if (trackedPlayer.m_None is TrackedNone trackedNone)
		{
			trackedNone.CardAdded -= OnCardLocationUpdated;
		}
	}
	
	private void OnPlayerRanOutOfTime(UID playerId)
	{
		SubmitTransaction(NonsenseTransaction.Create(playerId));
	}
	
	private void OnTimerStarted(UID playerId)
	{
		var player = GetPlayer(playerId);
		var opponents = GetOpponentsOfPlayer(player);

		foreach (var opponent in opponents)
		{
			opponent.StopChessTimer();
		}

		var chessTimerEvent = new ActiveChessTimerPlayerSessionEventArgs
		{
			PlayerId = playerId
		};
		
		DispatchSessionEvent(player, chessTimerEvent);

		foreach (var opponent in opponents)
		{
			DispatchSessionEvent(opponent, chessTimerEvent);
		}
	}
	
	private PlayerUpdatedSessionEventArgs BuildPlayerUpdate(Player player)
	{
		var champion = player.m_ChampionCard;
		var championContext = champion?.GetCardContext();
		var thresholds = player.m_ResourceThresholds.OrderBy(t => t.Key).ToList();

		var remainingTime = ChessTimerLimit - player.GetChessTimerElapsedTime();
		if (remainingTime < TimeSpan.Zero)
			remainingTime = TimeSpan.Zero;

		return new PlayerUpdatedSessionEventArgs
		{
			PlayerId = player.m_PlayerId,
			Health = champion?.CurrentHealthValue ?? 0,
			Charges = player.m_ChargePoints,
			Resources = player.m_CurrentResourcePool,
			TurnNumber = m_TotalTurnsTaken,
			TotalResources = player.m_TotalResourcePool,
			RemainingTime = remainingTime,
			ThresholdValues = thresholds.Select(t => t.Value).ToList(),
			ChampionId = champion?.m_SessionCardId ?? SessionCardId.Invalid,
			Thresholds = thresholds.Select(t => t.Key).ToList(),
			MaxHandSize = player.CalculateMaximumHandSize(),
			CanSeeEnemyHand = championContext?.GetBool(IntAttrs.CanSeeOpponentsHand) ?? false,
			CanSeeEnemyUnderground = championContext?.GetBool(IntAttrs.CanSeeUndergroundTroops) ?? false,
			DeckSleeveId = player.m_DeckSleeveId,
			SpellPoints = player.m_SpellPoints,
			SessionId = m_SessionId
		};
	}

	public List<SessionEventArgs> BuildResyncEvents(Player player)
	{
		var events = new List<SessionEventArgs>();

		foreach (var other in m_Players)
			events.Add(BuildPlayerUpdate(other));

		foreach (var card in GetAllCards())
			events.Add(CardUpdateFactory.CreateUpdateEventForPlayer(player, card, card.m_CurrentCardCollection));

		return events;
	}

	public EPriorityContext GetPriorityContext()
	{
		var activePlayer = GetActivePlayer();
		var priorityPlayer = GetPriorityPlayer();
		if (activePlayer is null || priorityPlayer is null)
			return EPriorityContext.Unknown;

		if (!m_Chain.IsEmpty)
			return EPriorityContext.ResolveTopOfChain;

		if (!priorityPlayer.HasStop(activePlayer, CurrentTurnPhase))
			return EPriorityContext.Unknown;

		var isActive = activePlayer.m_PlayerId == priorityPlayer.m_PlayerId;
		return CurrentTurnPhase switch
		{
			ETurnPhases.Ready => isActive ? EPriorityContext.Ready : EPriorityContext.OpponentsReady,
			ETurnPhases.FirstMainPhase => !activePlayer.CanDelareAttack()
				? (isActive ? EPriorityContext.ProcedeToSecondMain : EPriorityContext.ProcedeToOpponentsSecondMain)
				: (isActive ? EPriorityContext.ProcedeToCombat : EPriorityContext.ProcedeToOpponentsCombat),
			ETurnPhases.SecondMainPhase => activePlayer.HandLargerThanMaximumHandSize()
				? EPriorityContext.Normal
				: (isActive ? EPriorityContext.ProceedToEndTurn : EPriorityContext.ProceedToOpponentsEndTurn),
			ETurnPhases.DeclareAttackPriorityWindow => isActive ? EPriorityContext.ProcedeToBlockers : EPriorityContext.ProcedeToMyBlockers,
			ETurnPhases.DeclareDefensePriorityWindow => EPriorityContext.ResolveCombat,
			ETurnPhases.EndPhase => isActive ? EPriorityContext.EndPhase : EPriorityContext.EndOpponentsPhase,
			_ => EPriorityContext.Normal
		};
	}

	private void SendChangedCardUpdates()
	{
		var players = GetAllPlayers();
		var visibleCardsByPlayer = new List<List<Card>>(players.Count);
		var visibleCards = new HashSet<Card>();

		foreach (var player in players)
		{
			var playerVisibleCards = _cardVisibilityManager.GetListOfCardsPlayerCanSee(player);
			visibleCardsByPlayer.Add(playerVisibleCards);
			visibleCards.UnionWith(playerVisibleCards);
		}

		_cardStatManager.TrackChangesInCards(visibleCards);

		for (var i = 0; i < players.Count; i++)
		{
			var player = players[i];
			if (IsAiSeat(player))
				continue;

			foreach (var card in _cardStatManager.FilterCardsWithUpdates(player, visibleCardsByPlayer[i]))
			{
				var cardUpdate =
					CardUpdateFactory.CreateUpdateEventForPlayer(player, card, card.m_CurrentCardCollection);
				DispatchSessionEvent(player, cardUpdate);
			}
		}
	}

	public void Dispose()
	{
		AiSeat?.Dispose();
		_semaphoreSlim.Dispose();

		foreach (var player in m_Players)
		{
			UnregisterPlayerEvents(player);

			if (player is TrackedPlayer trackedPlayer)
			{
				trackedPlayer.Dispose();
			}
		}
		
		
	}
}
