using Dingler.Data.Enums;
using Dingler.Game.Wallet;

namespace Dingler.Game.Sieges;

/// <summary>
/// Every Siege money movement for an attack, in one place, so a different wallet can be dropped in later by changing
/// only this class. The amounts come from the defense's bounty (see SiegeCost).
/// </summary>
public sealed class SiegePayments
{
	private readonly WalletService _wallet;

	public SiegePayments(WalletService wallet) => _wallet = wallet;

	/// <summary>Takes the attack price from the attacker and gives the defender their cut. False if the attacker can't
	/// pay; nothing changes then.</summary>
	public bool TryChargeAttack(ulong attackerId, ulong defenderId, SiegeCurrency currency, SiegeCost cost)
	{
		if (!_wallet.TrySpend(attackerId, currency, cost.Total))
			return false;

		_wallet.Add(defenderId, currency, cost.Fee);
		return true;
	}

	/// <summary>Undoes TryChargeAttack, for an attack that couldn't start after all.</summary>
	public void RefundAttack(ulong attackerId, ulong defenderId, SiegeCurrency currency, SiegeCost cost)
	{
		_wallet.Add(defenderId, currency, -cost.Fee);
		_wallet.Add(attackerId, currency, cost.Total);
	}
}

/// <summary>
/// What attacking a defense costs, worked out from its bounty exactly the way the client's FeeDetail does it (float
/// maths, rounded up), so the numbers match what the Attack screen showed: Total is paid by the attacker, Fee goes to
/// the defender, Bet is added to the bounty and the rest (Rake) is burned.
/// </summary>
public readonly record struct SiegeCost(int Total, int Fee, int Bet)
{
	public int Rake => Total - Fee - Bet;

	public static SiegeCost For(int pot, float costPct, float feePct, float betPct)
	{
		var total = (int)Math.Ceiling((float)pot * costPct);
		return new SiegeCost(total, (int)Math.Ceiling((float)total * feePct), (int)Math.Ceiling((float)total * betPct));
	}
}
