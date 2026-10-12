extern alias HexGame;
using System.Reflection;
using Dingler.Game.Arena.Ai;
using Dingler.Game.Cards;
using Dingler.Game.GameObjects;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Mechanics;
using HexGame::Game.Shared.Mechanics.Abilities;
using HexGame::Game.Shared.Mechanics.Transactions;
using HexGame::Reckoning.Game;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Games;

// Frost Ring Arena: the parts of the rules engine an arena battle (session flag 128) needs. The recipe was proven
// offline in the research repo (docs/05, 200 of 200 battles); the AI seat follows docs/02 §7 (400 of 400 games).
public sealed partial class HexRulesEngine
{
	// ---------- battle mods (GetArenaBattleMods, 10029) ----------

	/// <summary>The run's battle mods for this fight (buffs, Hogarth's challenge). Never null.</summary>
	public List<EncounterModBase> ArenaMods { get; set; } = new();

	// The client's version asks the Campaign server for the mods over the network; the server supplies them directly,
	// then applies the round-0 ones exactly as the client's reply handler does. The engine applies later rounds itself.
	public override void ApplyEncounterStartModifications()
	{
		if (!IsPvEArena())
		{
			base.ApplyEncounterStartModifications();
			return;
		}

		if (m_AppliedEncounterModifications)
			return;
		m_AppliedEncounterModifications = true;
		m_BattleModifications = ArenaMods ?? new List<EncounterModBase>();
		ApplyEncounterRoundTriggeredModifications(0);
	}

	// ---------- starting an arena battle ----------

	private static readonly MethodInfo CheckStartGameMethod =
		typeof(AuthoritativeSessionBase).GetMethod("CheckStartGame", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
		?? throw new MissingMethodException("AuthoritativeSessionBase.CheckStartGame");

	/// <summary>With flag 128 the engine's own InitPvEArenaEncounter loads no decks, so the server loads both and starts
	/// the game itself. Runs on the calling thread, before the pump, like a tournament game's start.</summary>
	internal void StartArenaGame(TrackedPlayer human, deck_bits humanDeck, string userName, TrackedPlayer ai, deck_bits? aiDeck = null)
	{
		if (!InitializeGame())
			throw new InvalidOperationException("arena battle failed to initialize");
		LoadPlayerDeck(human, humanDeck, null, userName, null, null);   // the champion card gets the user name (Dingler's lookups need it)
		// Arena: from ai.m_DeckTemplateID, the arena opponent's deck. Siege: the defense deck, which also carries the AI
		// personality (the engine sends it to the AI as SetDeckPersonality when the game starts).
		LoadPlayerDeck(ai, aiDeck, null, string.Empty, null, null);
		if (CurrentTurnPhase == ETurnPhases.NotPlaying)
			CheckStartGameMethod.Invoke(this, null);
		if (CurrentTurnPhase == ETurnPhases.NotPlaying)
			throw new InvalidOperationException("arena battle did not start (a deck failed to load?)");
	}

	/// <summary>A Siege fight (session flag IsSiege): built and voided like an arena battle, without battle mods.</summary>
	internal bool IsSiegeBattle => (m_EncounterData?.SessionFlags & ESessionFlags.IsSiege) != 0;

	// ---------- the AI seat ----------

	internal ArenaAiSeat? AiSeat { get; private set; }

	internal bool IsAiSeat(Player? player) =>
		AiSeat is not null && player is not null && AiSeat.Player.m_PlayerId.Equals(player.m_PlayerId);

	/// <summary>Attach after both players are added and before the game starts, so the AI gets every event.</summary>
	internal void AttachAiSeat(ArenaAiSeat seat)
	{
		AiSeat = seat;
		FlushReady += seat.OnFlush;
	}

	internal object? CurrentGameAction => m_ActionStack?.CurrentAction;

	/// <summary>The AI's moves: already initialized by their factories (never Initialize again), patched, then queued.</summary>
	internal bool SubmitAiTransaction(Transaction transaction)
	{
		PatchChooseOneIndex(transaction);
		GuardDiscardLoop(transaction);
		return SubmitTransaction(transaction);
	}

	// ---------- void (design 03 D15-A: engine crash or AI stall = no strike, the same fight replays) ----------

	private volatile bool _voidRequested;
	public bool IsVoided { get; private set; }
	public string? VoidReason { get; private set; }
	internal bool IsVoidRequested => _voidRequested;

	/// <summary>Asks the pump to end the battle as void at its next step (thread-safe).</summary>
	internal void RequestVoid(string reason)
	{
		if (_voidRequested || IsGameEnded)
			return;
		VoidReason = reason;
		_voidRequested = true;
		_logger?.LogWarning("Arena battle {SessionId} voided: {Reason}", m_SessionId, reason);
		ProcessWork();
	}

	// The human client needs a normal game end (it reads Winners[0]) to leave the battle; the arena result handler
	// sees IsVoided and changes nothing in the run.
	private void EndArenaBattleAsVoid()
	{
		IsVoided = true;
		var human = m_Players.FirstOrDefault(p => !IsAiSeat(p));
		var ai = AiSeat?.Player;
		EndGame(human is null ? new List<UID>() : new List<UID> { human.m_PlayerId },
			ai is null ? new List<UID>() : new List<UID> { ai.m_PlayerId }, forceEnd: true);
	}

	// ---------- the AI's view of the board: rebuilt live before each delivery ----------

	/// <summary>Player, card and combat state as the AI seat sees it now. Dingler's incremental updates were written for
	/// human clients (stale resources, skipped zone moves), so the AI never uses them. Two fixes from docs/02 §7: hidden
	/// cards the AI is offered come face up (RevealNow), and every card carries its real zone and AbleToBlock.</summary>
	internal List<SessionEventArgs> BuildAiSync(ArenaAiSeat seat)
	{
		var events = new List<SessionEventArgs>();

		foreach (var p in GetAllPlayers())
		{
			var update = BuildPlayerUpdate(p);
			update.RemainingTime = TimeSpan.FromHours(1);   // constant, so the byte comparison only sees real changes
			seat.AddIfChanged("P" + p.m_PlayerId, update, events);
		}

		foreach (var card in GetAllCards().ToList())
		{
			try
			{
				var faceUp = seat.RevealNow.Contains(card.m_SessionCardId.ToString());
				var update = CardUpdateFactory.CreateUpdateEventForPlayer(seat.Player, card, card.m_CurrentCardCollection, faceUp);
				update.Collection = card.m_CurrentCardCollection;   // the factory hides played resources from human clients
				if (!update.Nulling && card.CanBlock() == ECombatantStatus.Ok)
					update.AICardStates |= EAICardStates.AbleToBlock;
				seat.AddIfChanged("C" + card.m_SessionCardId, update, events);
			}
			catch (Exception)
			{
				seat.SyncErrors++;
			}
		}
		seat.RevealNow.Clear();

		// A combat whose attacker died stays in the engine with no attacker (Session.PruneDeadCombats skips it); listed, it
		// reaches the AI as attacker Card.0, which its combat evaluator can't look up (AICardEvaluator.GetCombatValue,
		// AITactical.AIHandleAttackDefensePriorityWindow), so the AI threw and passed. Offline: 11 of 200 games -> 0.
		seat.AddIfChanged("L", CombatListingFor(seat.Player, CombatManager.GetAllCombatAttacks().Where(c => c.Attacker != null).ToList()), events);
		return events;
	}

	// ---------- the AI's legal moves (ported from the research harness, which ran 600 games) ----------
	// Dingler's GameOptionService was written for human clients and differs in ways the AI notices (empty target
	// lists for choosing-zone cards, no forced-block minimum, no PlayForFree), so the AI seat gets its own builder.

	private int _aiSuppressTurn = -1;

	private bool AiSendPlayerOptions(Player player)
	{
		var list = new PlayerOptionListSessionEventArgs { SessionId = m_SessionId, PlayerId = player.m_PlayerId, Options = new List<SessionEventArgs>() };
		foreach (var card in GetAllCards().ToList())
		{
			try
			{
				var option = AiBuildOptionsFor(player, card);
				if (option != null)
					list.Options.Add(option);
			}
			catch (Exception)
			{
				AiSeat!.OptionErrors++;
			}
		}
		AiRevealOffered(player, list);
		AiSeat!.Enqueue(list);
		return true;
	}

	private bool AiSendPlayerOptionsFor(Player player, Card sourceCard, AbilityTemplate abilityTemplate, AbilityInstance abilityInstance)
	{
		var option = new PlayerOptionSessionEventArgs
		{
			SessionId = m_SessionId, Card = sourceCard.m_SessionCardId, State = ECardUsage.Activate,
			Instances = new List<SessionEventArgs> { AiOptionInstance(sourceCard, player, abilityTemplate.AbilityTemplateId, abilityInstance) },
		};
		var list = new PlayerOptionListSessionEventArgs { SessionId = m_SessionId, PlayerId = player.m_PlayerId, Options = new List<SessionEventArgs> { option } };
		AiRevealOffered(player, list);
		AiSeat!.Enqueue(list);
		return true;
	}

	private bool AiSendPlayerOptionsFor(Player player, List<Card> cards, List<AbilityTemplate> templates, List<AbilityInstance> abilityInstances)
	{
		var byCard = new Dictionary<string, PlayerOptionSessionEventArgs>();
		var order = new List<PlayerOptionSessionEventArgs>();
		for (var i = 0; i < cards.Count && i < templates.Count; i++)
		{
			var key = cards[i].m_SessionCardId.ToString();
			if (!byCard.TryGetValue(key, out var option))
			{
				option = new PlayerOptionSessionEventArgs { SessionId = m_SessionId, Card = cards[i].m_SessionCardId, State = ECardUsage.Activate, Instances = new List<SessionEventArgs>() };
				byCard[key] = option;
				order.Add(option);
			}
			var instance = abilityInstances != null && i < abilityInstances.Count ? abilityInstances[i] : null;
			option.Instances.Add(AiOptionInstance(cards[i], player, templates[i].AbilityTemplateId, instance));
		}
		var list = new PlayerOptionListSessionEventArgs { SessionId = m_SessionId, PlayerId = player.m_PlayerId, Options = order.Cast<SessionEventArgs>().ToList() };
		AiRevealOffered(player, list);
		AiSeat!.Enqueue(list);
		return true;
	}

	private PlayerOptionSessionEventArgs? AiBuildOptionsFor(Player p, Card card)
	{
		var seat = AiSeat!;
		if (_aiSuppressTurn != m_TotalTurnsTaken)
		{
			_aiSuppressTurn = m_TotalTurnsTaken;
			seat.Mirror.SuppressedCards.Clear();
		}
		if (seat.Mirror.SuppressedCards.Contains(SessionCardIdText(card.m_SessionCardId)))
			return null;   // the livelock guard: this card's play was refused 3 times in a row this turn

		var option = new PlayerOptionSessionEventArgs { SessionId = m_SessionId, Card = card.m_SessionCardId, State = ECardUsage.None, Instances = new List<SessionEventArgs>() };

		// play
		var forFree = card.GetCardContext().GetBool(IntAttrs.OwnerCanPlayForFree)
		              || (p.m_ChampionCard != null && p.m_ChampionCard.GetCardContext().GetBool(IntAttrs.CanPlayCardsForFree));
		if (CanPlayCard(card, p, forFree))
		{
			option.State |= ECardUsage.Play;
			if (forFree) option.State |= ECardUsage.PlayForFree;
			option.Instances.Add(AiOptionInstance(card, p, HexGame::Game.Shared.Resources.BuiltInResources.PlayCardAbilityTemplateId, null));
			foreach (var automatic in card.GetAutomaticAbilities())
				option.Instances.Add(AiOptionInstance(card, p, automatic.AbilityTemplateId, AiInstanceOf(card, automatic.AbilityTemplateId)));
		}

		// activate
		foreach (var id in card.CurrentAbilities)
		{
			var template = HexGame::Singleton<TemplateManager>.Instance.GetAbilityTemplate(id);
			if (template != null && template.IsManual && CanActivateAbility(card, p, id))
			{
				option.State |= ECardUsage.Activate;
				option.Instances.Add(AiOptionInstance(card, p, id, AiInstanceOf(card, id)));
			}
		}

		var active = GetActivePlayer() == p;
		var mine = card.m_ControllingPlayer == p && card.m_CurrentCardCollection == ECardCollections.Warzone;

		// attack
		if (CurrentTurnPhase == ETurnPhases.DeclareAttack && active && mine && card.CanAttack() == ECombatantStatus.Ok)
			option.State |= card.GetCardContext().GetBool(IntAttrs.MustAttack) ? ECardUsage.ForcedAttack : ECardUsage.Attack;

		// block
		if (CurrentTurnPhase == ETurnPhases.DeclareDefense && !active && mine)
		{
			var opponent = GetOpponentsOfPlayer(p)[0];
			var targets = opponent.GetAllAttackers().Where(attacker => card.CanBlock(attacker) == ECombatantStatus.Ok).Select(attacker => attacker.m_SessionCardId).ToList();
			if (targets.Count > 0)
			{
				var forced = opponent.GetAllAttackersThatMustBeBlocked().Any(attacker => card.CanBlock(attacker) == ECombatantStatus.Ok);
				option.State |= ECardUsage.Defend;
				option.Instances.Add(new OptionInstanceSessionEventArgs
				{
					SessionId = m_SessionId, Id = ResourceId.Blocking, TargetIds = new List<ResourceId>(),
					MinTargetCounts = new List<int> { forced ? 1 : 0 }, MaxTargetCounts = new List<int> { 1 },
					TargetInstances = new List<SessionEventArgs>
					{
						new TargetInstanceSessionEventArgs
						{
							SessionId = m_SessionId, TargetIndex = 0, TargetId = ResourceId.Blocking, Targets = targets,
							AdditionalTargets = new List<SessionCardId>(),
						},
					},
				});
			}
		}

		return option.State == ECardUsage.None ? null : option;
	}

	private OptionInstanceSessionEventArgs AiOptionInstance(Card card, Player p, ResourceId id, AbilityInstance? instance)
	{
		var optionInstance = new OptionInstanceSessionEventArgs
		{
			SessionId = m_SessionId, Id = id,
			TargetIds = GetTargetTemplateIdsForAbility(card, p, id) ?? new List<ResourceId>(),
			MinTargetCounts = GetMinimumTargetCountsForAbility(card, p, id, instance) ?? new List<int>(),
			MaxTargetCounts = GetMaximumTargetCountsForAbility(card, p, id, instance) ?? new List<int>(),
			TargetInstances = new List<SessionEventArgs>(),
		};
		var potential = GetPotentialTargetsForAbility(card, p, id, instance);
		if (potential != null)
			foreach (var byIndex in potential.OrderBy(k => k.Key))
				foreach (var byTemplate in byIndex.Value)
					optionInstance.TargetInstances.Add(new TargetInstanceSessionEventArgs
					{
						SessionId = m_SessionId, TargetIndex = byIndex.Key, TargetId = byTemplate.Key,
						Targets = byTemplate.Value != null ? byTemplate.Value.Distinct().ToList() : new List<SessionCardId>(),
						AdditionalTargets = new List<SessionCardId>(),
					});
		var costs = GetPotentialCostsForAbility(card, p, id);
		if (costs != null)
			foreach (var byType in costs)
				foreach (var cost in byType.Value)
					optionInstance.TargetInstances.Add(new CostInstanceSessionEventArgs
					{
						SessionId = m_SessionId, CostType = byType.Key, Min = cost.Min, Max = cost.Max,
						Targets = cost.Targets != null ? cost.Targets.ToList() : new List<SessionCardId>(),
						TargetTemplateId = cost.TargetTemplateId,
					});
		return optionInstance;
	}

	private AbilityInstance? AiInstanceOf(Card card, ResourceId abilityId)
	{
		if (card.m_CurrentCardCollection != ECardCollections.Warzone)
			return null;   // cards not in play have no instance
		try
		{
			return AbilityManager.TryGetAbilityInstance(AbilityManager.LookupAbilityInstanceId(card.m_SessionCardId, abilityId), out var instance) ? instance : null;
		}
		catch
		{
			return null;
		}
	}

	// The AI values every card it is offered as a target or cost and crashed on cards it had only seen face down
	// (docs/02 §7.3 cause A). Show the chooser, and only the chooser, those cards face up in the next sync.
	private void AiRevealOffered(Player player, PlayerOptionListSessionEventArgs list)
	{
		var ids = new HashSet<string>();
		foreach (var option in list.Options.OfType<PlayerOptionSessionEventArgs>())
			foreach (var instance in option.Instances.OfType<OptionInstanceSessionEventArgs>())
				foreach (var target in instance.TargetInstances)
				{
					if (target is TargetInstanceSessionEventArgs t && t.Targets != null) foreach (var id in t.Targets) ids.Add(id.ToString());
					if (target is CostInstanceSessionEventArgs c && c.Targets != null) foreach (var id in c.Targets) ids.Add(id.ToString());
				}
		if (ids.Count == 0)
			return;
		foreach (var card in GetAllCards())
			if (ids.Contains(card.m_SessionCardId.ToString()) && !card.CanPlayerSeeCard(player))
				AiSeat!.RevealNow.Add(card.m_SessionCardId.ToString());
	}

	// Same text the engine prints inside "SessionCardId(...)", so it matches the livelock guard's parse of a move.
	internal static string SessionCardIdText(SessionCardId id)
	{
		var text = id.ToString();
		var match = System.Text.RegularExpressions.Regex.Match(text, @"SessionCardId\(([^)]*)\)");
		return match.Success ? match.Groups[1].Value : text;
	}

	// ---------- fixes to the AI's moves (docs/02 §7.3) ----------

	private readonly Random _aiRng = new();

	// Choose-one cards: the AI may play them with no choice (Index -1) or a random one with no legal target; the engine
	// then asks for data the AI can't give (an AI bug) and waits forever. Pick a legal choice instead.
	private void PatchChooseOneIndex(Transaction t)
	{
		try
		{
			const BindingFlags priv = BindingFlags.Instance | BindingFlags.NonPublic;
			var listField = t.GetType().GetField("m_AbilityDataList", priv);
			var cardField = t.GetType().GetField("m_SessionCardId", priv);
			if (listField != null && cardField != null && listField.GetValue(t) is List<AbilityActivationData> list)
			{
				var card = FindCard((SessionCardId)cardField.GetValue(t)!);
				foreach (var data in list)
					if (data != null && data.AbilityTemplateId.Equals(HexGame::Game.Shared.Resources.BuiltInResources.PlayCardAbilityTemplateId))
						FixIndex(data, card, GetPlayer(t.m_PlayerId));
			}
			if (t is SetAbilityActivationDataTransaction && t.GetType().GetField("m_AbilityActivationData", priv)?.GetValue(t) is AbilityActivationData single
			    && single.AbilityTemplateId.Equals(HexGame::Game.Shared.Resources.BuiltInResources.PlayCardAbilityTemplateId))
				FixIndex(single, FindCard(single.SourceCardId), GetPlayer(t.m_PlayerId));
		}
		catch (Exception ex)
		{
			_logger?.LogWarning("Arena AI: choose-one patch failed: {Error}", ex.Message);
		}
	}

	private void FixIndex(AbilityActivationData data, Card? card, Player? player)
	{
		if (card == null || player == null)
			return;
		var indexed = card.GetIndexedAbilities();
		if (indexed == null || indexed.Count == 0)
			return;
		var current = data.Index >= 0 ? indexed.FirstOrDefault(a => a.m_AbilityIndex == data.Index) : null;
		if (current != null && AbilityHasValidTargets(card, player, current))
			return;
		var legal = indexed.Where(a => AbilityHasValidTargets(card, player, a)).ToList();
		var pick = legal.Count > 0 ? legal[_aiRng.Next(legal.Count)] : indexed[0];
		data.Index = pick.m_AbilityIndex;
		AiSeat!.IndexPatches++;
	}

	private Card? FindCard(SessionCardId id) => GetAllCards().FirstOrDefault(c => c.m_SessionCardId.Equals(id));

	// Passing in Discard with too many cards: the engine silently refuses it and offers priority again. First resend the
	// AI's whole view (it may have drifted), then after 3 refused passes discard one card for it.
	private readonly Dictionary<string, int> _aiRefusedDiscardPasses = new();

	private void GuardDiscardLoop(Transaction t)
	{
		if (t is not PassPriorityTransaction || CurrentTurnPhase != ETurnPhases.Discard)
			return;
		var player = GetPlayer(t.m_PlayerId);
		if (player == null || !player.HandLargerThanMaximumHandSize())
			return;
		var key = player.m_PlayerId + "@" + m_TotalTurnsTaken;
		var count = _aiRefusedDiscardPasses[key] = (_aiRefusedDiscardPasses.TryGetValue(key, out var c) ? c : 0) + 1;
		if (count == 1)
			AiSeat!.ResetSync();
		if (count < 3)
			return;
		var victim = GetAllCards().LastOrDefault(x => x.m_ControllingPlayer == player && x.m_CurrentCardCollection == ECardCollections.Hand);
		if (victim == null)
			return;
		m_TransactionQueue.Enqueue(DiscardTransaction.Create(player.m_PlayerId, victim.m_SessionCardId));
		_aiRefusedDiscardPasses[key] = 0;
		AiSeat!.AutoDiscards++;
	}
}
