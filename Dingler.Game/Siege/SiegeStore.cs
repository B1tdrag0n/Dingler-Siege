extern alias HexGame;
using HexGame::Game.Shared.Domain;

namespace Dingler.Game.Siege;

/// <summary>A committed Siege defense. Immutable: the store swaps in a changed copy, so a defense handed out never changes.</summary>
public sealed record SiegeDefense(
	ulong Id,
	ulong PlayerId,
	string PlayerName,
	siege_bits.CurrencyType Currency,
	int Pot,
	int ChallengeLimit,
	byte[] FightInfo,
	siege_bits.Status Status,
	DateTime Created,
	DateTime StatusDate,
	DateTime? CloseDate = null,
	int Wins = 0,
	int Loss = 0,
	int ChallengeCount = 0,
	DateTime? LastChallengeDate = null)
{
	public bool IsOpen => Status is siege_bits.Status.ACTIVE or siege_bits.Status.INGAME;
}

/// <summary>One row of a defense's events log. Context carries extra detail, e.g. the new status for a STATE row.</summary>
public sealed record SiegeLogEntry(
	ulong Id,
	ulong SiegeId,
	siege_ledger_bits.SiegeAction Action,
	string? Context,
	ulong PlayerId,
	string PlayerName,
	DateTime Created);

/// <summary>
/// Every Siege defense and its events log, kept in memory for now: a server restart forgets them all (Milestone 3 moves
/// this into the database). A singleton shared by all sessions, hence the lock.
/// </summary>
public sealed class SiegeStore
{
	private readonly object _gate = new();
	private readonly Dictionary<ulong, SiegeDefense> _defenses = new();
	private readonly List<SiegeLogEntry> _log = new();
	private ulong _lastDefenseId;
	private ulong _lastLogId;

	/// <summary>Commits a new active defense, or returns false if the player already has one open.</summary>
	public bool TryCreate(ulong playerId, string playerName, siege_bits.CurrencyType currency, int pot, int challengeLimit,
		byte[] fightInfo, out SiegeDefense defense)
	{
		lock (_gate)
		{
			if (_defenses.Values.Any(d => d.PlayerId == playerId && d.IsOpen))
			{
				defense = null!;
				return false;
			}

			var now = Now();
			defense = new SiegeDefense(++_lastDefenseId, playerId, playerName, currency, pot, challengeLimit, fightInfo,
				siege_bits.Status.ACTIVE, now, now);
			_defenses[defense.Id] = defense;
			AddLog(defense, siege_ledger_bits.SiegeAction.STATE, nameof(siege_bits.Status.ACTIVE), now);
			return true;
		}
	}

	/// <summary>Withdraws an open defense. Returns null if there is no such defense or it is already closed.</summary>
	public SiegeDefense? Close(ulong siegeId)
	{
		lock (_gate)
		{
			if (!_defenses.TryGetValue(siegeId, out var defense) || !defense.IsOpen)
				return null;

			var now = Now();
			var closed = defense with { Status = siege_bits.Status.CLOSED, StatusDate = now, CloseDate = now };
			_defenses[siegeId] = closed;
			AddLog(closed, siege_ledger_bits.SiegeAction.CLOSED, null, now);
			return closed;
		}
	}

	public SiegeDefense? Get(ulong siegeId)
	{
		lock (_gate)
			return _defenses.GetValueOrDefault(siegeId);
	}

	/// <summary>The player's most recently committed defense, open or not.</summary>
	public SiegeDefense? Latest(ulong playerId)
	{
		lock (_gate)
			return _defenses.Values.Where(d => d.PlayerId == playerId).MaxBy(d => d.Id);
	}

	public IReadOnlyList<SiegeDefense> ForPlayer(ulong playerId)
	{
		lock (_gate)
			return _defenses.Values.Where(d => d.PlayerId == playerId).OrderByDescending(d => d.Id).ToList();
	}

	/// <summary>Every open defense on the server, the player's own included (the client marks those itself).</summary>
	public IReadOnlyList<SiegeDefense> Open()
	{
		lock (_gate)
			return _defenses.Values.Where(d => d.IsOpen).OrderByDescending(d => d.Id).ToList();
	}

	/// <summary>A defense's events log, newest first, which is the order the client keeps it in.</summary>
	public IReadOnlyList<SiegeLogEntry> Log(ulong siegeId)
	{
		lock (_gate)
			return _log.Where(e => e.SiegeId == siegeId).OrderByDescending(e => e.Id).ToList();
	}

	private void AddLog(SiegeDefense defense, siege_ledger_bits.SiegeAction action, string? context, DateTime when) =>
		_log.Add(new SiegeLogEntry(++_lastLogId, defense.Id, action, context, defense.PlayerId, defense.PlayerName, when));

	// The client shows these dates as they come, without a time zone, so they are server-local like the rest of Dingler.
	private static DateTime Now() => DateTime.Now;
}
