using Dingler.Data.Enums;

namespace Dingler.Data.Entities.GameData;

/// <summary>
/// A committed Siege defense, one row per defense. Modeled on the client's siege_bits.
/// </summary>
public class Siege
{
    public ulong Id { get; set; }

    public ulong PlayerProfileId { get; set; }

    public virtual PlayerProfile PlayerProfile { get; set; } = null!;

    /// <summary>The defender's name when the defense was committed.</summary>
    public string PlayerName { get; set; } = null!;

    public SiegeCurrency Currency { get; set; }

    public int Pot { get; set; }

    public int ChallengeLimit { get; set; }

    /// <summary>The 3 fights (deck, message) as the client sent them, gzipped: a snapshot later deck edits can't change.</summary>
    public byte[] FightInfo { get; set; } = null!;

    public SiegeStatus Status { get; set; }

    public int Wins { get; set; }

    public int Loss { get; set; }

    public int ChallengeCount { get; set; }

    public DateTime Created { get; set; }

    public DateTime StatusDate { get; set; }

    /// <summary>When the defender asked to withdraw while under attack; the withdraw happens after the attack.</summary>
    public DateTime? RequestClose { get; set; }

    public DateTime? CloseDate { get; set; }

    public DateTime? LastChallengeDate { get; set; }

    /// <summary>Active or in an attack: still on the public list and counting as the player's one defense.</summary>
    public static readonly SiegeStatus[] OpenStatuses = [SiegeStatus.Active, SiegeStatus.InGame];

    public virtual ICollection<SiegeLedger> Ledger { get; set; } = new List<SiegeLedger>();
}
