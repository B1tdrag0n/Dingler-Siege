namespace Dingler.Data.Entities.GameData;

/// <summary>
/// One game event as the server sent it to one seat: the AI seat's events are kept too, so a replay can show either
/// side. Data is the event exactly as serialized for the client; it starts with the event's Class and session id.
/// </summary>
public class ReplayEvent
{
    public ulong Id { get; set; }

    public ulong GameReplayId { get; set; }

    public virtual GameReplay GameReplay { get; set; } = null!;

    /// <summary>The seat the event was sent to (UID.ToUInt64()).</summary>
    public ulong PlayerUid { get; set; }

    /// <summary>When it was sent, in Unix milliseconds; playback keeps the gaps between events.</summary>
    public long TimeMs { get; set; }

    /// <summary>SessionEventArgs.Class, e.g. 1 = game started, 2 = game ended.</summary>
    public int EventClass { get; set; }

    public byte[] Data { get; set; } = null!;
}
