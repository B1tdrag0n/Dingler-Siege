extern alias HexGame;
using Dingler.Data.Entities.GameData;
using HexGame::Game.Shared;
using Network = HexGame::Game.Shared.Profile.Network;

namespace Dingler.Game.Replays;

/// <summary>How a saved replay is named and described to the client.</summary>
public static class ReplayWire
{
	private const string KeyPrefix = "replay-";

	/// <summary>
	/// The name the client asks for the replay by (ReplayRecord.Session, sent back in replayfetch). Built from the row's
	/// Id because session names repeat ("Siege 4 fight 1" is every first fight on defense 4).
	/// </summary>
	public static string Key(ulong replayId) => KeyPrefix + replayId;

	public static bool TryParseKey(string? key, out ulong replayId)
	{
		replayId = 0;
		return key is not null && key.StartsWith(KeyPrefix, StringComparison.Ordinal) &&
		       ulong.TryParse(key.AsSpan(KeyPrefix.Length), out replayId);
	}

	/// <summary>
	/// The client saves a downloaded replay as Documents\HexReplay\{EndUTC seconds}.{Session}.{SFormat}.{SPoints}.{STemplate}.replay
	/// and doesn't download it again while that file exists, so none of those may be null. Server is where the client
	/// sends replayfetch; Dingler answers it whatever the address.
	/// </summary>
	public static Network.ReplayRecord ToRecord(GameReplay replay) => new()
	{
		StartUTC = replay.Started,
		EndUTC = replay.Ended,
		Server = new UID(UID.Type.ServiceGameSession, 1),
		Session = Key(replay.Id),
		SFormat = "SIEGE",
		SPoints = 0,
		STemplate = string.Empty,
		PubGame = true,
		Players = string.Join(",", new[] { replay.Player1Name, replay.Player2Name ?? "" }),
		Winners = replay.WinnerUid == 0 ? string.Empty
			: replay.WinnerUid == replay.Player1Uid ? replay.Player1Name
			: replay.Player2Name ?? string.Empty,
		TournRound = -1,
		ExpireUTC = replay.Ended.AddYears(10),
	};
}
