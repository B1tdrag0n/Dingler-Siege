extern alias HexGame;
using System.Globalization;
using Dingler.Data.Entities.GameData;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Mechanics;
using HexGame::Game.Shared.Profile;

namespace Dingler.Game.Replays;

/// <summary>
/// Turns a recorded game into the .replay file the client's viewer plays. The file is written by the client's own
/// GameEventLog, so its layout is the one the viewer reads (version 5); this class only arranges the events.
/// </summary>
public static class ReplayBuilder
{
	// An identical event sent to both seats this close together is written once, with both seats as targets.
	private const long DuplicateWindowMs = 250;

	public sealed record Result(byte[] File, int Generations, int Events, int Skipped);

	/// <summary>
	/// Player 1 comes first in the file, which makes the viewer watch from their seat. Events must be in the order they
	/// were sent.
	/// </summary>
	public static Result Build(GameReplay replay, IReadOnlyList<ReplayEvent> events, uint sessionFlags,
		ProfileDeckTemplate player1Deck, ProfileDeckTemplate player2Deck)
	{
		var log = new GameEventLog();
		var (generations, written, skipped) = Arrange(log, events);

		var players = new List<GameEventLog.PlayerInfo>
		{
			new() { ID = new UID(replay.Player1Uid), Name = replay.Player1Name, Deck = player1Deck,
				Winner = replay.WinnerUid != 0 && replay.WinnerUid == replay.Player1Uid },
			new() { ID = new UID(replay.Player2Uid), Name = replay.Player2Name ?? "", Deck = player2Deck,
				Winner = replay.WinnerUid != 0 && replay.WinnerUid == replay.Player2Uid },
		};
		var info = new GameEventLog.GameMetaData(UID.Invalid, replay.SessionName, sessionFlags, players,
			replay.Started, replay.Ended, -1, true, GameEventLog.GameMetaData.SeriesFormat.SIEGE, 0, string.Empty);

		// The writer formats its dates with the current culture, the reader parses them as invariant.
		var culture = CultureInfo.CurrentCulture;
		CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
		try
		{
			using var stream = new MemoryStream();
			log.Serialize(info, stream);
			return new Result(stream.ToArray(), generations, written, skipped);
		}
		finally
		{
			CultureInfo.CurrentCulture = culture;
		}
	}

	// The viewer plays the file in groups ("generations"). It can play the first two at once without pacing
	// (ReplayClient.FlushGenZero), and its progress bar is groups played / total. So: group 0 is the setup before the
	// first turn phase, group 1 stays empty, then the mulligan and one group per turn.
	private static (int Generations, int Written, int Skipped) Arrange(GameEventLog log, IReadOnlyList<ReplayEvent> events)
	{
		var generation = 0;
		var seenTurnPhase = false;
		var written = 0;
		var skipped = 0;
		long? previousTime = null;
		var recent = new Dictionary<string, (GameEventLog.EventTargets Entry, long Time)>();
		log.EnsureGeneration(0);

		foreach (var e in events)
		{
			SessionEventArgs? message;
			try
			{
				message = SessionEventArgs.BuildArgs(e.EventClass, e.Data);
			}
			catch
			{
				message = null;
			}
			if (message is null)
			{
				skipped++;
				continue;
			}

			var target = new UID(e.PlayerUid);
			var key = e.EventClass + ":" + Convert.ToBase64String(e.Data);
			if (recent.TryGetValue(key, out var same) && !same.Entry.Targets.Contains(target) &&
			    e.TimeMs - same.Time <= DuplicateWindowMs)
			{
				same.Entry.Targets = [.. same.Entry.Targets, target];
				continue;
			}

			// Checked after the copies, so the second seat's copy of a turn start doesn't open another group.
			if (message is TurnPhaseUpdatedSessionEventArgs phase)
			{
				var next = !seenTurnPhase ? 2
					: phase.TurnPhase == ETurnPhases.StartTurn && log.GetGeneration(generation).Events.Count > 0 ? generation + 1
					: generation;
				seenTurnPhase = true;
				if (next != generation)
				{
					generation = next;
					log.EnsureGeneration(generation);
					recent.Clear();
				}
			}

			var entry = new GameEventLog.EventTargets
			{
				Targets = [target],
				Message = message,
				TOffset = previousTime is null ? 0 : (int)Math.Clamp(e.TimeMs - previousTime.Value, 0, int.MaxValue),
			};
			previousTime = e.TimeMs;
			log.GetGeneration(generation).Events.Add(entry);
			recent[key] = (entry, e.TimeMs);
			written++;
		}

		return (log.GenerationCount, written, skipped);
	}
}
