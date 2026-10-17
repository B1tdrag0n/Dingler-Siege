extern alias HexGame;
using Dingler.Data.Entities.GameData;
using HexGame::Game.Shared;

namespace Dingler.Game.Replays;

/// <summary>
/// Collects a game's events while it is played, for both seats, so the game can be watched later. The engine hands it
/// every event it sends (HexRulesEngine.DispatchSessionEvent); the events stay in memory until the game ends and are
/// then saved in one write (ReplayRepository). A game cut short by a restart isn't kept, which is fine: it doesn't count.
/// </summary>
public sealed class ReplayRecorder
{
	// About 10 times a long game. Past it the recording stops rather than grow without bound.
	private const int MaxEvents = 200_000;

	private readonly List<ReplayEvent> _events = [];
	private readonly Lock _lock = new();
	private bool _stopped;

	public DateTime Started { get; } = DateTime.UtcNow;

	/// <summary>True once MaxEvents was reached and later events were dropped.</summary>
	public bool Truncated { get; private set; }

	public int Count
	{
		get
		{
			lock (_lock)
				return _events.Count;
		}
	}

	public void Record(UID player, SessionEventArgs args)
	{
		var data = args.ToByteArray();
		var entry = new ReplayEvent
		{
			PlayerUid = player.ToUInt64(),
			TimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			EventClass = args.Class,
			Data = data
		};

		lock (_lock)
		{
			if (_stopped)
				return;
			if (_events.Count >= MaxEvents)
			{
				Truncated = true;
				return;
			}
			_events.Add(entry);
		}
	}

	/// <summary>The events so far, in the order they were sent. Recording stops; later events are dropped.</summary>
	public List<ReplayEvent> TakeEvents()
	{
		lock (_lock)
		{
			var events = _events.ToList();
			_events.Clear();
			_stopped = true;
			return events;
		}
	}
}
