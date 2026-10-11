using Dingler.Data.Enums;

namespace Dingler.Data.Entities.GameData;

/// <summary>
/// One row of a defense's events log. Modeled on the client's siege_ledger_bits.
/// </summary>
public class SiegeLedger
{
    public ulong Id { get; set; }

    public ulong SiegeId { get; set; }

    public virtual Siege Siege { get; set; } = null!;

    public SiegeAction Action { get; set; }

    /// <summary>Extra detail, e.g. the new status for a STATE row.</summary>
    public string? Context { get; set; }

    /// <summary>Which of the 3 fights an attack reached.</summary>
    public int FightSeq { get; set; }

    public int Wins { get; set; }

    /// <summary>Who did it: the defender for STATE and CLOSED rows, the attacker for attack rows.</summary>
    public ulong PlayerProfileId { get; set; }

    public virtual PlayerProfile PlayerProfile { get; set; } = null!;

    public string PlayerName { get; set; } = null!;

    /// <summary>The attacker's deck, for attack rows.</summary>
    public byte[]? PlayerDeck { get; set; }

    public DateTime? Forfeit { get; set; }

    // Money moved by this event. Stays empty until the wallet (Milestone 4).
    public SiegeEscrow Escrow { get; set; }

    public int Bet { get; set; }

    public int Fee { get; set; }

    public int Rake { get; set; }

    public DateTime Created { get; set; }
}
