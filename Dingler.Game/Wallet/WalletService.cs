extern alias HexGame;
using Dingler.Data.Enums;
using Dingler.Server;
using HexGame::Game.Shared.Network.Profile;

namespace Dingler.Game.Wallet;

/// <summary>
/// Each player's gold and platinum, keyed by profile id. Milestone 4 keeps it in memory only: every balance starts at 0
/// and is lost when Dingler.Terminal stops. Test money comes from the "/givemoney" chat command (ChatMessageRequestHandler).
/// </summary>
public sealed class WalletService
{
	private readonly Dictionary<ulong, Balance> _balances = new();
	private readonly Lock _lock = new();

	public readonly record struct Balance(int Gold, int Platinum)
	{
		public int Of(SiegeCurrency currency) => currency == SiegeCurrency.Platinum ? Platinum : Gold;

		public Balance Plus(SiegeCurrency currency, int amount) => currency == SiegeCurrency.Platinum
			? this with { Platinum = Platinum + amount }
			: this with { Gold = Gold + amount };
	}

	public Balance Get(ulong profileId)
	{
		lock (_lock)
			return _balances.GetValueOrDefault(profileId);
	}

	/// <summary>Adds money to a player's balance and returns the new balance.</summary>
	public Balance Add(ulong profileId, SiegeCurrency currency, int amount)
	{
		lock (_lock)
			return _balances[profileId] = _balances.GetValueOrDefault(profileId).Plus(currency, amount);
	}

	/// <summary>Takes the amount if the player has enough. Returns false and changes nothing otherwise.</summary>
	public bool TrySpend(ulong profileId, SiegeCurrency currency, int amount)
	{
		lock (_lock)
		{
			var balance = _balances.GetValueOrDefault(profileId);
			if (balance.Of(currency) < amount)
				return false;

			_balances[profileId] = balance.Plus(currency, -amount);
			return true;
		}
	}

	/// <summary>Tells the client the player's current balance, so the gold and platinum counters update straight away.</summary>
	public void SendBalance(SessionContext context)
	{
		var balance = Get(context.ProfileId);
		context.TrySendMessageToClient(new BalanceUpdateEventArgs(balance.Gold, balance.Platinum));
	}
}
