extern alias HexGame;
using System.Collections.Concurrent;
using Dingler.Data.Entities.GameData;
using Dingler.Data.Enums;
using Dingler.Data.Repositories;
using Dingler.Game.Arena;
using Dingler.Game.Games;
using Dingler.Game.Replays;
using Dingler.Server;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Mechanics;
using HexGame::Reckoning.Game;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Dingler.Game.Sieges.SiegeWire;

namespace Dingler.Game.Sieges;

/// <summary>
/// The games of a Siege attack. After each countdown the attacker gets "siege:gstart" and their client opens the
/// battle; it then asks for it the way an arena battle does (StartEncounter, JoinSession, ReadyToStartGame, see
/// ArenaBattleRequestHandlers), and the game is built like an arena battle: the attacker's deck against the client's own
/// AI playing the defense deck, under session flag IsSiege. A win moves on to the next fight after another countdown;
/// a loss ends the attack as defended; winning all 3 defeats the defense and pays the attacker its bounty.
/// </summary>
public sealed class SiegeBattleService
{
	/// <summary>A game announced to the attacker and not built yet. GameId is set when the client asks for it.</summary>
	private sealed class PendingGame
	{
		public required SiegeAttacks.Attack Attack { get; init; }
		public required int Seq { get; init; }
		public required SessionState State { get; init; }
		public ulong GameId { get; set; }
	}

	public sealed class ActiveGame
	{
		public required SiegeAttacks.Attack Attack { get; init; }
		public required int Seq { get; init; }
		public required UID Human { get; init; }
		public required UID Ai { get; init; }
		public required SessionState State { get; init; }
		public required ReplayRecorder Recorder { get; init; }
		public HexGameWrapper? Game { get; set; }
		public int Applied;
	}

	// The AI's cards are numbered from here, so its card ids never match the attacker's (both decks come numbered from 1).
	private const ulong AiCardIdOffset = 1_000_000;

	private readonly SiegeAttacks _attacks;
	private readonly SiegePayments _payments;
	private readonly IServiceScopeFactory _scopes;
	private readonly ILogger<SiegeBattleService>? _logger;
	private readonly ConcurrentDictionary<ulong, PendingGame> _pending = new();   // by attacker profile id
	private readonly ConcurrentDictionary<ulong, ActiveGame> _active = new();     // by attacker profile id

	// Kept short on purpose: a Continue click on the end-of-game banner before the countdown arrives opens the client's
	// attack results splash (VICTORY or DEFEAT from the last "siege:saction"), which is wrong between fights.
	private static readonly TimeSpan EndScreenPause = TimeSpan.FromSeconds(3);

	public SiegeBattleService(SiegeAttacks attacks, SiegePayments payments, IServiceScopeFactory scopes,
		ILogger<SiegeBattleService>? logger = null)
	{
		_attacks = attacks;
		_payments = payments;
		_scopes = scopes;
		_logger = logger;
	}

	/// <summary>After the countdown, announce the attack's next fight to the attacker.</summary>
	public void ScheduleNextGame(SiegeAttacks.Attack attack, DateTime when)
	{
		var seq = attack.Seq;
		_logger?.LogInformation("Siege: fight {seq} of {user}'s attack on defense {id} starts at {when:HH:mm:ss}", seq + 1,
			attack.AttackerName, attack.SiegeId, when);
		_ = Task.Run(async () =>
		{
			var wait = when - Now();
			if (wait > TimeSpan.Zero)
				await Task.Delay(wait);
			try
			{
				AnnounceGame(attack, seq);
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "Siege: couldn't start fight {seq} of the attack on defense {id}", seq + 1, attack.SiegeId);
			}
		});
	}

	private void AnnounceGame(SiegeAttacks.Attack attack, int seq)
	{
		// The attack may have ended (forfeit) or moved on while we waited.
		if (!_attacks.TryGetBySiege(attack.SiegeId, out var current) || current != attack || attack.Seq != seq)
		{
			_logger?.LogDebug("Siege: fight {seq} of the attack on defense {id} is no longer due", seq + 1, attack.SiegeId);
			return;
		}

		var state = new SessionState
		{
			SessionId = UID.Invalid,   // the client clears it anyway; the game gets its id when the client asks for it
			SessionName = $"Siege {attack.SiegeId} fight {seq + 1}",
			MinimumPlayerCount = 2,
			MaximumPlayerCount = 2,
			EncounterData = new SessionStateEncounterData
			{
				SessionFlags = ESessionFlags.IsSiege,
				SeriesFormat = SessionStateEncounterData.SeriesType.SIEGE,
				SeriesMaxGames = FightsPerDefense,
				MatchPreviousWinners = new List<ulong>(),
				FirstPlayer = UID.Invalid,
			},
			JoinInsteadOfReconnect = false,
		};
		_pending[attack.AttackerId] = new PendingGame { Attack = attack, Seq = seq, State = state };

		var taunt = attack.Fights[seq].TryGetProperty("Message", out var m) ? m.GetString() : null;
		if (attack.Session.TrySendMessageToClient(GameStartEvent(state, attack.SiegeId, taunt, seq)))
			_logger?.LogInformation("Siege: fight {seq} of {user}'s attack on defense {id} announced", seq + 1,
				attack.AttackerName, attack.SiegeId);
		else
			_logger?.LogWarning("Siege: couldn't announce fight {seq} to {user}", seq + 1, attack.AttackerName);
	}

	/// <summary>StartEncounter: the announced game's session, with its game id. False if no Siege game is waiting.</summary>
	public bool TryReserve(SessionContext context, GameManager games, out SessionState state)
	{
		state = null!;
		if (!_pending.TryGetValue(context.ProfileId, out var pending))
			return false;

		Reserve(pending, games);
		state = pending.State;
		return true;
	}

	private static void Reserve(PendingGame pending, GameManager games)
	{
		if (pending.GameId != 0)
			return;
		pending.GameId = games.NextGameId();
		pending.State.SessionId = new UID(UID.Type.AuthoritativeSession, pending.GameId);
	}

	public bool HasPending(SessionContext context) => _pending.ContainsKey(context.ProfileId);

	/// <summary>JoinSession: build and start the announced game.</summary>
	public bool TryStart(SessionContext context, GameManager games, List<ETurnPhases>? selfStops,
		List<ETurnPhases>? opponentStops, out ActiveGame game, out string reason)
	{
		game = null!;
		if (!_pending.TryRemove(context.ProfileId, out var pending))
			return Fail("no Siege game is waiting", out reason);
		var attack = pending.Attack;
		if (!_attacks.TryGetBySiege(attack.SiegeId, out var current) || current != attack)
			return Fail("the attack is over", out reason);
		if (games.TryGetGameForPlayer(context.UserName!, out _))
			return Fail("the player's previous game is still running", out reason);

		Reserve(pending, games);
		var humanDeck = ToDeckBits(attack.Deck);
		var aiDeck = ToDeckBits(attack.Fights[pending.Seq].GetProperty("Deck"));
		Renumber(aiDeck, AiCardIdOffset);

		var active = new ActiveGame
		{
			Attack = attack,
			Seq = pending.Seq,
			Human = ArenaBattleService.HumanId(context),
			Ai = new UID(UID.Type.ServiceAI, pending.GameId),
			State = pending.State,
			Recorder = new ReplayRecorder(),
		};
		var profileId = context.ProfileId;

		// Published before the game exists, so the end-of-game callback always finds (and removes) it.
		_active[profileId] = active;
		try
		{
			active.Game = games.CreateArenaGame(pending.GameId, pending.State.SessionName, pending.State.EncounterData,
				active.Human, humanDeck, context.UserName!, selfStops, opponentStops, active.Ai, ResourceId.Invalid,
				new List<EncounterModBase>(), (engine, winners, _) => OnGameEnded(profileId, active, engine, winners), aiDeck,
				active.Recorder);
		}
		catch (Exception ex)
		{
			_active.TryRemove(profileId, out _);
			return Fail("the game could not start: " + ex.Message, out reason);
		}

		_logger?.LogInformation("Siege: fight {seq} of {user}'s attack on defense {id} started (game {game}, AI personality {personality})",
			active.Seq + 1, attack.AttackerName, attack.SiegeId, pending.GameId, aiDeck.Personality);
		game = active;
		reason = string.Empty;
		return true;
	}

	public bool TryGetActive(SessionContext context, out ActiveGame game) => _active.TryGetValue(context.ProfileId, out game!);

	/// <summary>
	/// Ends the attack without playing it out: the attacker forfeited or left. The defense counts it as defended and goes
	/// back to ACTIVE; the attacker's payment isn't returned. Null if the attack had already ended.
	/// </summary>
	public async Task<Siege?> ForfeitAsync(SiegeRepository sieges, SiegeAttacks.Attack attack)
	{
		if (!_attacks.Remove(attack))
			return null;
		Abandon(attack);

		var now = Now();
		return await sieges.EndAttackDefendedAsync(attack.SiegeId, new SiegeLedger
		{
			Action = SiegeAction.Defended,
			FightSeq = attack.Seq,
			PlayerProfileId = attack.AttackerId,
			PlayerName = attack.AttackerName,
			Forfeit = now,
			Created = now
		});
	}

	/// <summary>
	/// The attacker's connection closed. Their client can't find its way back into the attack, so it counts as a forfeit;
	/// otherwise the defense would stay locked (INGAME) with nobody able to finish or forfeit the attack.
	/// </summary>
	public void OnAttackerDisconnected(SiegeAttacks.Attack attack)
	{
		if (!_attacks.TryGetBySiege(attack.SiegeId, out var current) || current != attack)
			return;

		_ = Task.Run(async () =>
		{
			try
			{
				using var scope = _scopes.CreateScope();
				var ended = await ForfeitAsync(scope.ServiceProvider.GetRequiredService<SiegeRepository>(), attack);
				if (ended is not null)
					_logger?.LogInformation("Siege: {user} disconnected during fight {seq} of the attack on {defender}'s defense {id}; it counts as forfeited",
						attack.AttackerName, attack.Seq + 1, ended.PlayerName, ended.Id);
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "Siege: ending the attack on defense {id} after {user} disconnected failed",
					attack.SiegeId, attack.AttackerName);
			}
		});
	}

	// Forget the attack's waiting game, and concede a running one.
	private void Abandon(SiegeAttacks.Attack attack)
	{
		if (_pending.TryGetValue(attack.AttackerId, out var pending) && pending.Attack == attack)
			_pending.TryRemove(new KeyValuePair<ulong, PendingGame>(attack.AttackerId, pending));
		if (_active.TryGetValue(attack.AttackerId, out var active) && active.Attack == attack)
			active.Game?.ForfeitArena(active.Human);
	}

	// Engine thread, before the final flush sends the game's end to the player. The database work runs on its own.
	private void OnGameEnded(ulong profileId, ActiveGame game, HexRulesEngine engine, List<UID> winners)
	{
		if (Interlocked.Exchange(ref game.Applied, 1) == 1)
			return;
		_active.TryRemove(new KeyValuePair<ulong, ActiveGame>(profileId, game));
		var attack = game.Attack;
		var voided = engine.IsVoided || winners.Count == 0;
		var won = !voided && winners.Contains(game.Human);
		_logger?.LogInformation("Siege: {user} {outcome} fight {seq} against defense {id} in {turns} turns. AI: {ai}",
			attack.AttackerName, voided ? "had a void game in" : won ? "won" : "lost", game.Seq + 1, attack.SiegeId,
			engine.m_TotalTurnsTaken, engine.AiSeat?.Summary() ?? "no AI seat");

		// The game's end event has gone out by now, so the recording is complete. A void fight is played again; only
		// fights that counted are kept.
		var events = game.Recorder.TakeEvents();
		var replay = voided ? null : NewReplay(game, engine, winners, events);

		// The result first, so the replay can point at the fight's events log row, where the client shows its watch
		// button. Building and saving the replay doesn't hold up the next fight's countdown.
		_ = Task.Run(async () =>
		{
			ulong? logId = null;
			try
			{
				logId = await ApplyResultAsync(attack, game.Seq, voided, won, engine.VoidReason);
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "Siege: applying fight {seq} of the attack on defense {id} failed", game.Seq + 1, attack.SiegeId);
			}

			if (replay is not null)
				await SaveReplayAsync(game, replay, events, logId);
		});
	}

	private static GameReplay NewReplay(ActiveGame game, HexRulesEngine engine, List<UID> winners, List<ReplayEvent> events)
	{
		var attack = game.Attack;
		return new GameReplay
		{
			SessionUid = engine.m_SessionId.ToUInt64(),
			GameId = engine.m_SessionId.GetInstanceId(),
			SessionName = game.State.SessionName,
			SiegeId = attack.SiegeId,
			FightSeq = game.Seq,
			Player1Uid = game.Human.ToUInt64(),
			Player1Name = attack.AttackerName,
			Player2Uid = game.Ai.ToUInt64(),
			Player2Name = attack.DefenderName,
			WinnerUid = winners.Count > 0 ? winners[0].ToUInt64() : 0,
			Started = game.Recorder.Started,
			Ended = DateTime.UtcNow,
			EventCount = events.Count,
			Events = events,
		};
	}

	private async Task SaveReplayAsync(ActiveGame game, GameReplay replay, List<ReplayEvent> events, ulong? logId)
	{
		var attack = game.Attack;
		replay.SiegeLedgerId = logId;

		ReplayBuilder.Result? file = null;
		try
		{
			file = ReplayBuilder.Build(replay, events, (uint)game.State.EncounterData.SessionFlags,
				ToDeckTemplate(attack.Deck), ToDeckTemplate(attack.Fights[game.Seq].GetProperty("Deck")));
			replay.ReplayFile = file.File;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Replay: building the file for fight {seq} on defense {id} failed; its events are kept",
				game.Seq + 1, attack.SiegeId);
		}

		try
		{
			using var scope = _scopes.CreateScope();
			var saved = await scope.ServiceProvider.GetRequiredService<ReplayRepository>().AddAsync(replay);
			_logger?.LogInformation("Replay: recorded {count} events of fight {seq} on defense {id} (replay {replay}, log row {log}){truncated}",
				events.Count, game.Seq + 1, attack.SiegeId, saved.Id, logId?.ToString() ?? "none",
				game.Recorder.Truncated ? ", cut short at the event limit" : "");
			if (file is not null)
				_logger?.LogInformation("Replay: file for replay {replay} is {bytes} bytes: {written} events in {generations} groups{skipped}",
					saved.Id, file.File.Length, file.Events, file.Generations,
					file.Skipped > 0 ? $", {file.Skipped} events unreadable and left out" : "");
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Replay: saving fight {seq} on defense {id} failed", game.Seq + 1, attack.SiegeId);
		}
	}

	// Returns the fight's events log row (SACKED or DEFENDED), or null if the fight got none (void, or forfeited meanwhile).
	private async Task<ulong?> ApplyResultAsync(SiegeAttacks.Attack attack, int seq, bool voided, bool won, string? voidReason)
	{
		if (!_attacks.TryGetBySiege(attack.SiegeId, out var current) || current != attack || attack.Seq != seq)
			return null;   // forfeited meanwhile

		using var scope = _scopes.CreateScope();
		var sieges = scope.ServiceProvider.GetRequiredService<SiegeRepository>();

		if (voided)
		{
			// An engine crash or a stuck AI isn't the attacker's fault: the same fight again (as the arena does).
			_logger?.LogWarning("Siege: fight {seq} on defense {id} voided ({reason}); it will be replayed", seq + 1,
				attack.SiegeId, voidReason ?? "no winner");
			CountdownAfterGame(attack);
			return null;
		}

		if (!won)
		{
			if (!_attacks.Remove(attack))
				return null;
			var now = Now();
			var defendedRow = new SiegeLedger
			{
				Action = SiegeAction.Defended,
				FightSeq = seq,
				PlayerProfileId = attack.AttackerId,
				PlayerName = attack.AttackerName,
				Created = now
			};
			var defended = await sieges.EndAttackDefendedAsync(attack.SiegeId, defendedRow);
			_logger?.LogInformation("Siege: {defender}'s defense {id} held against {user} at fight {seq}",
				defended?.PlayerName, attack.SiegeId, attack.AttackerName, seq + 1);
			// The client's end-of-game Continue opens the attack's results from the last "siege:saction" it got.
			if (defended is null)
				return null;
			attack.Session.TrySendMessageToClient(ActionEvent(defendedRow));
			return defendedRow.Id;
		}

		// The fight's deck lost: a SACKED row, like the original server's. It isn't sent to the attacker: their client
		// would take it as the attack's result (see above).
		var sackedRow = new SiegeLedger
		{
			Action = SiegeAction.Sacked,
			FightSeq = seq,
			PlayerProfileId = attack.AttackerId,
			PlayerName = attack.AttackerName,
			Created = Now()
		};

		attack.Seq = seq + 1;
		if (attack.Seq < FightsPerDefense)
		{
			var logged = await sieges.LogFightLostAsync(attack.SiegeId, sackedRow);
			CountdownAfterGame(attack);
			return logged ? sackedRow.Id : null;
		}

		// All 3 won: the defense falls and its bounty goes to the attacker.
		if (!_attacks.Remove(attack))
			return null;
		var defeatedRow = new SiegeLedger
		{
			Action = SiegeAction.Defeated,
			FightSeq = seq,
			PlayerProfileId = attack.AttackerId,
			PlayerName = attack.AttackerName,
			Escrow = SiegeEscrow.Funded,
			Created = sackedRow.Created
		};
		var defeated = await sieges.EndAttackDefeatedAsync(attack.SiegeId, sackedRow, defeatedRow);
		if (defeated is null)
		{
			_logger?.LogWarning("Siege: {user} won defense {id}, but it was no longer under attack; no bounty paid",
				attack.AttackerName, attack.SiegeId);
			return null;
		}

		_payments.PayBounty(attack.AttackerId, defeated.Currency, defeated.Pot);
		// No countdown: the client's end-of-game Continue shows the victory from this event (Action DEFEATED).
		attack.Session.TrySendMessageToClient(ActionEvent(defeatedRow));
		_logger?.LogInformation("Siege: {user} defeated {defender}'s defense {id} and won {pot} {currency}",
			attack.AttackerName, defeated.PlayerName, defeated.Id, defeated.Pot, defeated.Currency);
		return sackedRow.Id;
	}

	// The next fight's countdown. The client only takes it while it is still in the game: there the event moves it to
	// a fresh assault screen; on the assault screen itself it can't be shown again (UISiege allows no Attack -> Attack
	// transition). So it goes out shortly after the game ends, leaving the end-of-game banner on screen for a moment.
	// If the attacker clicks Continue first, UIBattle.Quit opens the results splash instead; the server can't prevent that.
	private void CountdownAfterGame(SiegeAttacks.Attack attack)
	{
		_ = Task.Run(async () =>
		{
			await Task.Delay(EndScreenPause);
			try
			{
				await NextCountdownAsync(attack);
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "Siege: sending the next countdown on defense {id} failed", attack.SiegeId);
			}
		});
	}

	// Back to the assault screen with the countdown, then the next (or the same, after a void) fight.
	private async Task NextCountdownAsync(SiegeAttacks.Attack attack)
	{
		if (!_attacks.TryGetBySiege(attack.SiegeId, out var current) || current != attack)
			return;   // forfeited or disconnected meanwhile
		using var scope = _scopes.CreateScope();
		var defense = await scope.ServiceProvider.GetRequiredService<SiegeRepository>().GetAsync(attack.SiegeId);
		if (defense is null)
			return;
		var next = Now() + Intermission;
		attack.Session.TrySendMessageToClient(ChallengeEvent(defense, attack.Seq, next));
		ScheduleNextGame(attack, next);
	}

	private static void Renumber(deck_bits deck, ulong offset)
	{
		foreach (var card in deck.CardsInDeck.Concat(deck.CardsInSideboard))
			card.Id += offset;
		var gems = deck.ActiveGems.ToList();
		deck.ActiveGems.Clear();
		foreach (var (id, gem) in gems)
			deck.ActiveGems[id + offset] = gem;
	}

	private static bool Fail(string why, out string reason)
	{
		reason = why;
		return false;
	}
}
