extern alias HexGame;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Dingler.Server;
using Dingler.Game.Cards;
using Dingler.Game.GameObjects;
using Dingler.Game.Replays;
using Dingler.Game.Tournaments;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Domain;
using HexGame::Game.Shared.Mechanics;
using HexGame::Game.Shared.Tournaments;
using HexGame::Reckoning.Game;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Games;

public sealed class GameManager : IDisposable
{
	private readonly ConcurrentDictionary<ulong, HexGameWrapper> _runningMatches = new();
	private readonly ConcurrentDictionary<string, HexGameWrapper> _gamePlayerIsIn = new();
	private readonly SessionManager _sessionManager;
	private readonly ILoggerFactory? _loggerFactory;
	private readonly ILogger<GameManager>? _logger;
	private ulong _currentMatchId;
	// Concurrent: arena games are created on request threads while matches end on engine threads.
	private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _gameCtsCollection;

	public GameManager(SessionManager sessionManager, ILoggerFactory? loggerFactory)
	{
		_sessionManager = sessionManager;
		_logger = loggerFactory?.CreateLogger<GameManager>();
		_loggerFactory = loggerFactory;
		_gameCtsCollection = new ConcurrentDictionary<ulong, CancellationTokenSource>();
	}

	public HexGameWrapper CreateGameSession(ulong tournamentId, TournamentPairing pairing,
		SessionStateEncounterData encounterData, CancellationToken tournamentToken)
	{
		var assignedId = Interlocked.Increment(ref _currentMatchId);
		var sessionUid = new UID(UID.Type.AuthoritativeSession, assignedId);

		var engine = new HexRulesEngine($"game-{assignedId}", sessionUid,
			_loggerFactory?.CreateLogger<HexRulesEngine>())
		{
			m_EncounterData = encounterData,
			ForcedFirstPlayer = encounterData.FirstPlayer
		};

		var gameCts = CancellationTokenSource.CreateLinkedTokenSource(tournamentToken);
		_gameCtsCollection[assignedId] = gameCts;
		
		var wrapper = new HexGameWrapper(engine, new CardVisibilityManager(), _sessionManager, gameCts.Token,
			_loggerFactory?.CreateLogger<HexGameWrapper>(), CleanupMatch);

		wrapper.TryAddPlayer(new TrackedPlayer(new PlayerState
		{
			PlayerId = pairing.Player1.PlayerUID,
			PlayerPosition = 0
		}, UID.Invalid));

		wrapper.TryAddPlayer(new TrackedPlayer(new PlayerState
		{
			PlayerId = pairing.Player2.PlayerUID,
			PlayerPosition = 1
		}, UID.Invalid));

		try
		{
			if (!engine.InitializeGame())
				throw new InvalidOperationException($"Match {assignedId} failed to initialize (deck load failed).");
		}
		catch (Exception ex)
		{
			_logger?.LogError(
				"Match {MatchId} creation failed during engine initialization: {Exception}", assignedId, ex);
			throw;
		}

		_runningMatches[assignedId] = wrapper;
		_gamePlayerIsIn[pairing.Player1.Name] = wrapper;
		_gamePlayerIsIn[pairing.Player2.Name] = wrapper;

		_logger?.LogInformation("Match {MatchId} created for tournament {TournamentId}: {Player1} vs {Player2}",
			assignedId, tournamentId, pairing.Player1.Name, pairing.Player2.Name);

		return wrapper;
	}

	public HexGameWrapper CreateGameSession(ulong tournamentId, TournamentPairing pairing,
		SessionStateEncounterData.SeriesType tournamentType, ESessionFlags tournamentFlags,
		CancellationToken tournamentToken)
	{
		var encounterData = BuildSessionStateEncounterData(tournamentId, pairing, tournamentType, tournamentFlags);
		return CreateGameSession(tournamentId, pairing, encounterData, tournamentToken);
	}

	private SessionStateEncounterData BuildSessionStateEncounterData(ulong tournamentId, TournamentPairing pairing,
		SessionStateEncounterData.SeriesType tournamentType, ESessionFlags tournamentFlags)
	{
		var player1Uid = pairing.Player1.PlayerUID;
		var player2Uid = pairing.Player2.PlayerUID;
		var firstPlayer = Random.Shared.Next(2) == 0 ? player1Uid : player2Uid;
		var encounterData = new SessionStateEncounterData()
		{
			SeriesFormat = tournamentType,
			SessionFlags = tournamentFlags,
			TournamentDecks = new List<TournamentDeckBitsWrapper>(),
			TournamentID = tournamentId,
			MatchPreviousWinners = new List<ulong>(),
			SeriesMaxGames = 3,
			FirstPlayer = firstPlayer
		};

		encounterData.TournamentDecks.Add(new TournamentDeckBitsWrapper()
		{
			PlayerName = pairing.Player1.Name,
			PlayerDeck = pairing.Decks[pairing.Player1.Name],
			PlayerUID = player1Uid
		});

		encounterData.TournamentDecks.Add(new TournamentDeckBitsWrapper()
		{
			PlayerName = pairing.Player2.Name,
			PlayerDeck = pairing.Decks[pairing.Player2.Name],
			PlayerUID = player2Uid
		});

		return encounterData;
	}

	/// <summary>A new game id, shared with tournament games.</summary>
	public ulong NextGameId() => Interlocked.Increment(ref _currentMatchId);

	/// <summary>
	/// Frost Ring Arena: one battle between the logged-in player (a type-244 seat with their stored deck) and the
	/// client's own AI (a type-248 seat with the arena opponent's deck), under session flag 128. The game is built and
	/// started here, on the calling thread, before its engine pump runs; the pump starts at once and waits until the
	/// player says they're ready (ReadyToStartGame). Clocks per design 03 D17-A.
	/// Siege fights use it too (session flag IsSiege): the AI then plays a player's deck, given as aiDeck instead of
	/// aiDeckTemplateId, and there are no battle mods. A recorder, when given, records the game for a replay.
	/// </summary>
	public HexGameWrapper CreateArenaGame(ulong gameId, string sessionName, SessionStateEncounterData encounterData,
		UID humanId, deck_bits humanDeck, string userName, List<ETurnPhases>? selfStops, List<ETurnPhases>? opponentStops,
		UID aiId, ResourceId aiDeckTemplateId, List<EncounterModBase> battleMods,
		Action<HexRulesEngine, List<UID>, List<UID>> onGameEnded, deck_bits? aiDeck = null, ReplayRecorder? recorder = null)
	{
		var engine = new HexRulesEngine(sessionName, new UID(UID.Type.AuthoritativeSession, gameId),
			_loggerFactory?.CreateLogger<HexRulesEngine>())
		{
			m_EncounterData = encounterData,
			Recorder = recorder,
			ForcedFirstPlayer = UID.Invalid,   // the engine's coin flip decides who starts
			ArenaMods = battleMods,
		};

		// A watchdog: no arena battle lasts 3 hours.
		var gameCts = new CancellationTokenSource(TimeSpan.FromHours(3));
		_gameCtsCollection[gameId] = gameCts;

		var wrapper = new HexGameWrapper(engine, new CardVisibilityManager(), _sessionManager, gameCts.Token,
			_loggerFactory?.CreateLogger<HexGameWrapper>(), CleanupMatch);
		engine.GameEnded += (winners, losers) => onGameEnded(engine, winners, losers);

		var human = new TrackedPlayer(new PlayerState { PlayerId = humanId, PlayerPosition = 0 }, UID.Invalid);
		var ai = new TrackedPlayer(new PlayerState { PlayerId = aiId, PlayerPosition = 1 }, UID.Invalid)
		{
			m_DeckTemplateID = aiDeckTemplateId,
		};
		human.GameTimer.MatchClockLimit = TimeSpan.FromDays(1);
		human.GameTimer.InactivityLimit = TimeSpan.FromMinutes(45);
		ai.GameTimer.MatchClockLimit = TimeSpan.FromDays(1);
		ai.GameTimer.InactivityLimit = TimeSpan.FromDays(1);

		// The player's own priority stops, as their client sent them (JoinSession); the engine adds the mandatory ones.
		if (selfStops is not null && opponentStops is not null)
			human.SetTurnPhases(selfStops.Distinct().ToList(), opponentStops.Distinct().ToList());

		try
		{
			wrapper.TryAddPlayer(human);
			wrapper.TryAddPlayer(ai);
			engine.AttachAiSeat(new Dingler.Game.Arena.Ai.ArenaAiSeat(engine, ai, human, _loggerFactory?.CreateLogger("ArenaAi")));
			engine.StartArenaGame(human, humanDeck, userName, ai, aiDeck);
		}
		catch (Exception ex)
		{
			_logger?.LogError("Arena battle {MatchId} could not start: {Exception}", gameId, ex);
			_gameCtsCollection.TryRemove(gameId, out _);
			gameCts.Dispose();
			wrapper.Dispose();
			throw;
		}

		wrapper.ArenaDeckInstanceId = humanDeck.Id;
		_runningMatches[gameId] = wrapper;
		_gamePlayerIsIn[userName] = wrapper;
		_ = wrapper.RunGameAsync(UID.Invalid);

		_logger?.LogInformation("Arena battle {MatchId} started for {Player}", gameId, userName);
		return wrapper;
	}

	private void CleanupMatch(ulong matchId)
	{
		if (_runningMatches.TryRemove(matchId, out var match))
		{
			foreach (var kvp in _gamePlayerIsIn.Where(kvp => kvp.Value == match).ToList())
			{
				_gamePlayerIsIn.TryRemove(kvp.Key, out _);
			}

			_logger?.LogInformation("Match {MatchId} removed from registry",
				matchId);
		}

		_gameCtsCollection.TryRemove(matchId, out var cts);

		if (cts is not null)
		{
			if (!cts.IsCancellationRequested)
				cts.Cancel();
			
			cts.Dispose();
		}
		
		match?.Dispose();
	}

	public bool TryGetMatch(ulong id, [MaybeNullWhen(false)] out HexGameWrapper match)
	{
		return _runningMatches.TryGetValue(id, out match);
	}

	public bool TryGetGameForPlayer(string username, [MaybeNullWhen(false)] out HexGameWrapper session)
	{
		return _gamePlayerIsIn.TryGetValue(username, out session);
	}

	public IReadOnlyList<string> GetRegisteredPlayerUsernames()
	{
		return _gamePlayerIsIn.Keys.ToList();
	}

	public void Cancel(ulong id)
	{
		if (!_gameCtsCollection.TryGetValue(id, out var cts))
			return;
		
		cts.Cancel();
		cts.Dispose();
	}

	public void Dispose()
	{
		_loggerFactory?.Dispose();

		foreach (var kvp in _gameCtsCollection)
		{
			if (!kvp.Value.IsCancellationRequested)
			{
				kvp.Value.Cancel();
			}

			kvp.Value.Dispose();
		}
		
		_gameCtsCollection.Clear();
	}
}
