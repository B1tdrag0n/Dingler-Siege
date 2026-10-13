extern alias HexGame;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Attributes;
using Dingler.Game.Arena;
using Dingler.Game.Games;
using Dingler.Game.Sieges;
using HexGame::Game.Client.Network.LoadBalancer;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Network.LoadBalancer;
using Microsoft.Extensions.Logging;

namespace Dingler.Game.Handlers.Request.Arena;

// Frost Ring Arena, build step 4: the battle start. After the Battle button the client sends, in order:
// FindSession (22019), StartEncounter (22017), JoinSession (22021), ReadyToStartGame (22031). Only arena battles use
// this path in Dingler (tournament games start through TournamentSessionStart / ReadyForGameSetup instead), and Siege
// fights: after "siege:gstart" the client asks for its game the same way, so a waiting Siege game is served first.

// 22019: for an arena battle it must fail; the client then asks StartEncounter for a flag-128 encounter. A Siege fight
// is found here instead: its client carries no encounter data, so after a failed FindSession it would send StartSession
// (which Dingler doesn't answer, and the client waits forever). Found, it goes straight on to JoinSession.
[Authenticated]
public sealed class FindSessionRequestHandler : IRequestHandler<FindSessionRequestArgs, FindSessionResponse>
{
	private readonly SiegeBattleService _siege;
	private readonly GameManager _games;
	private readonly ILogger<FindSessionRequestHandler>? _logger;

	public FindSessionRequestHandler(SiegeBattleService siege, GameManager games, ILogger<FindSessionRequestHandler>? logger = null)
	{
		_siege = siege;
		_games = games;
		_logger = logger;
	}

	public FindSessionResponse HandleRequest(SessionContext context, FindSessionRequestArgs request)
	{
		var human = ArenaBattleService.HumanId(context);
		try
		{
			if (_siege.TryReserve(context, _games, out var siegeState))
			{
				_logger?.LogInformation("Siege: {user}'s client found its fight ({session})", context.UserName, siegeState.SessionName);
				return new FindSessionResponse { Success = true, RoutingPlayerId = human, SessionState = siegeState };
			}
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Siege: FindSession failed for {user}", context.UserName);
		}
		return new FindSessionResponse { Success = false, RoutingPlayerId = human };
	}
}

// 22017: reserve a battle for the run's current fight and describe its session. Answer Success = true whenever
// possible: a failed StartEncounter leaves the client on its loading screen (refusals belong in JoinSession).
[Authenticated]
public sealed class StartEncounterRequestHandler : IRequestHandler<StartEncounterRequestArgs, StartEncounterResponse>
{
	private readonly ArenaBattleService _battles;
	private readonly SiegeBattleService _siege;
	private readonly GameManager _games;
	private readonly ILogger<StartEncounterRequestHandler>? _logger;

	public StartEncounterRequestHandler(ArenaBattleService battles, SiegeBattleService siege, GameManager games,
		ILogger<StartEncounterRequestHandler>? logger = null)
	{
		_battles = battles;
		_siege = siege;
		_games = games;
		_logger = logger;
	}

	public StartEncounterResponse HandleRequest(SessionContext context, StartEncounterRequestArgs request)
	{
		var human = ArenaBattleService.HumanId(context);
		try
		{
			if (_siege.TryReserve(context, _games, out var siegeState))
			{
				return new StartEncounterResponse
				{
					Success = true,
					RoutingPlayerId = human,
					SessionState = siegeState,
					SessionID = siegeState.SessionId,
					ServerID = UID.Invalid,
				};
			}

			var isArena = request.EncounterData is not null && (request.EncounterData.SessionFlags & ESessionFlags.IsPvEArena) != 0;
			if (isArena)
			{
				var pending = _battles.Reserve(context, _games, request.SessionName ?? string.Empty);
				return new StartEncounterResponse
				{
					Success = true,
					RoutingPlayerId = human,
					SessionState = pending.SessionState,
					SessionID = pending.SessionState.SessionId,
					ServerID = UID.Invalid,   // never read by the client
				};
			}
			_logger?.LogWarning("Arena: StartEncounter for {user} is not an arena encounter", context.UserName);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Arena: StartEncounter failed for {user}", context.UserName);
		}
		return new StartEncounterResponse { Success = false, RoutingPlayerId = human };
	}
}

// 22021: build and start the reserved battle. A refusal here makes the client show "unable to start" and go back.
[Authenticated]
public sealed class JoinSessionRequestHandler : IRequestHandler<JoinSessionRequestArgs, JoinSessionResponse>
{
	private readonly ArenaBattleService _battles;
	private readonly SiegeBattleService _siege;
	private readonly GameManager _games;
	private readonly ILogger<JoinSessionRequestHandler>? _logger;

	public JoinSessionRequestHandler(ArenaBattleService battles, SiegeBattleService siege, GameManager games,
		ILogger<JoinSessionRequestHandler>? logger = null)
	{
		_battles = battles;
		_siege = siege;
		_games = games;
		_logger = logger;
	}

	public JoinSessionResponse HandleRequest(SessionContext context, JoinSessionRequestArgs request)
	{
		var human = ArenaBattleService.HumanId(context);
		try
		{
			if (_siege.HasPending(context))
			{
				if (_siege.TryStart(context, _games, request.SelfTurnPhases, request.OpponentTurnPhases, out var game, out var why))
					return new JoinSessionResponse
					{
						Success = true,
						RoutingPlayerId = human,
						SessionState = game.State,
						SessionPlayers = new List<PlayerState>
						{
							new() { PlayerId = human, PlayerPosition = 0 },
							new() { PlayerId = game.Ai, PlayerPosition = 1 },
						},
					};
				_logger?.LogWarning("Siege: JoinSession refused for {user}: {reason}", context.UserName, why);
				return new JoinSessionResponse { Success = false, RoutingPlayerId = human };
			}

			if (_battles.TryStart(context, _games, request.SessionId.GetInstanceId(), request.DeckID,
				    request.SelfTurnPhases, request.OpponentTurnPhases, out var pending, out var reason))
			{
				_battles.TryGetActive(context, out var battle);
				return new JoinSessionResponse
				{
					Success = true,
					RoutingPlayerId = human,
					SessionState = pending.SessionState,
					SessionPlayers = new List<PlayerState>
					{
						new() { PlayerId = human, PlayerPosition = 0 },
						new() { PlayerId = battle.Ai, PlayerPosition = 1 },
					},
				};
			}
			_logger?.LogWarning("Arena: JoinSession refused for {user}: {reason}", context.UserName, reason);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Arena: JoinSession failed for {user}", context.UserName);
		}
		return new JoinSessionResponse { Success = false, RoutingPlayerId = human };
	}
}

// 22031: the client has added its own player and is ready. Tell it about the opponent (it has no other way to learn
// it), then let the game's events flow. The client expects no reply.
[Authenticated]
public sealed class ReadyToStartGameRequestHandler : IRequestHandler<ReadyToStartGameRequestArgs>
{
	private readonly ArenaBattleService _battles;
	private readonly SiegeBattleService _siege;
	private readonly ILogger<ReadyToStartGameRequestHandler>? _logger;

	public ReadyToStartGameRequestHandler(ArenaBattleService battles, SiegeBattleService siege,
		ILogger<ReadyToStartGameRequestHandler>? logger = null)
	{
		_battles = battles;
		_siege = siege;
		_logger = logger;
	}

	public void HandleRequest(SessionContext context, ReadyToStartGameRequestArgs request)
	{
		try
		{
			if (!request.IsReady)
				return;
			if (_siege.TryGetActive(context, out var siegeGame) && siegeGame.Game is not null && siegeGame.Applied == 0)
			{
				context.TrySendMessageToClient(new HexGame::Game.Shared.Network.GameSession.PlayerAddedEventArgs
				{
					RoutingPlayerId = siegeGame.Human,
					PlayerState = new PlayerState { PlayerId = siegeGame.Ai, PlayerPosition = 1 },
				});
				siegeGame.Game.PlayerIsReadyForEvents(siegeGame.Human);
				_logger?.LogInformation("Siege: {user} is ready; fight running", context.UserName);
				return;
			}
			if (!_battles.TryGetActive(context, out var battle) || battle.Game is null || battle.Applied != 0)
			{
				_logger?.LogWarning("Arena: {user} is ready but has no running battle", context.UserName);
				return;
			}

			context.TrySendMessageToClient(new HexGame::Game.Shared.Network.GameSession.PlayerAddedEventArgs
			{
				RoutingPlayerId = battle.Human,
				PlayerState = new PlayerState { PlayerId = battle.Ai, PlayerPosition = 1 },
			});
			battle.Game.PlayerIsReadyForEvents(battle.Human);
			_logger?.LogInformation("Arena: {user} is ready; battle running", context.UserName);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Arena: ReadyToStartGame failed for {user}", context.UserName);
		}
	}
}
