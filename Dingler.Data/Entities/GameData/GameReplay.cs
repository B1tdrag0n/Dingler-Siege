namespace Dingler.Data.Entities.GameData;

/// <summary>
/// One recorded game, kept so it can be watched later. Its events are in <see cref="ReplayEvents"/>; the .replay file
/// the client plays is built from them. Siege fights fill SiegeId and FightSeq.
/// Game ids start again from 1 when the server restarts, so a row gets its own Id and keeps the game's in GameId.
/// </summary>
public class GameReplay
{
    public ulong Id { get; set; }

    /// <summary>The game's session id as the client knows it (UID.ToUInt64()).</summary>
    public ulong SessionUid { get; set; }

    public ulong GameId { get; set; }

    public string SessionName { get; set; } = null!;

    /// <summary>The defense this fight was played against, for Siege fights.</summary>
    public ulong? SiegeId { get; set; }

    /// <summary>Which of the defense's 3 fights this was (0-based), for Siege fights.</summary>
    public int FightSeq { get; set; }

    // The two seats (UID.ToUInt64()). Player 1 is the human, whose view a replay opens on.
    public ulong Player1Uid { get; set; }

    public string Player1Name { get; set; } = null!;

    public ulong Player2Uid { get; set; }

    public string? Player2Name { get; set; }

    /// <summary>0 if the game had no winner.</summary>
    public ulong WinnerUid { get; set; }

    public DateTime Started { get; set; }

    public DateTime Ended { get; set; }

    public int EventCount { get; set; }

    /// <summary>
    /// The .replay file the client's viewer plays (the client's own GameEventLog format), built from Events when the
    /// game is saved. Null if building it failed; the events are kept either way.
    /// </summary>
    public byte[]? ReplayFile { get; set; }

    public virtual List<ReplayEvent> Events { get; set; } = [];
}
