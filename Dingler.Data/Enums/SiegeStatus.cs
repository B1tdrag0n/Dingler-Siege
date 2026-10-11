namespace Dingler.Data.Enums;

/// <summary>Same names and numbers as the client's siege_bits.Status, so the two convert with a cast.</summary>
public enum SiegeStatus
{
    Unknown,
    Escrow,
    Pending,
    Active,
    InGame,
    Closed,
    Defeated,
    ErrorFunds,
    ErrorAsset,
    Error
}
