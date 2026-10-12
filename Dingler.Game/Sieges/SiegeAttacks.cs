using System.Collections.Concurrent;
using System.Text.Json;
using Dingler.Server;

namespace Dingler.Game.Sieges;

/// <summary>
/// The attacks running right now, in memory: one per defense and one per attacker. The defense's row in the database
/// says INGAME while its attack is here. A server restart forgets the attacks; their defenses stay INGAME until the
/// attacker forfeits (or the defender withdraws), see MessagingAsyncRequestHandler.Forfeit.
/// </summary>
public sealed class SiegeAttacks
{
	public sealed class Attack
	{
		public required ulong SiegeId { get; init; }
		public required ulong AttackerId { get; init; }
		public required string AttackerName { get; init; }

		/// <summary>The attacker's deck exactly as the client sent it with the challenge.</summary>
		public required JsonElement Deck { get; init; }

		/// <summary>The attacker's connection; the attack's events are pushed through it.</summary>
		public required SessionContext Session { get; init; }

		/// <summary>Which of the 3 fights is next (0-based).</summary>
		public int Seq { get; set; }
	}

	private readonly ConcurrentDictionary<ulong, Attack> _bySiege = new();
	private readonly Lock _lock = new();

	/// <summary>Adds the attack unless the defense is already under attack or the attacker is already attacking.</summary>
	public bool TryAdd(Attack attack)
	{
		lock (_lock)
		{
			if (_bySiege.ContainsKey(attack.SiegeId) || _bySiege.Values.Any(a => a.AttackerId == attack.AttackerId))
				return false;

			_bySiege[attack.SiegeId] = attack;
			return true;
		}
	}

	public bool TryGetBySiege(ulong siegeId, out Attack attack) => _bySiege.TryGetValue(siegeId, out attack!);

	public bool IsAttacking(ulong attackerId) => _bySiege.Values.Any(a => a.AttackerId == attackerId);

	public void Remove(Attack attack) => _bySiege.TryRemove(new KeyValuePair<ulong, Attack>(attack.SiegeId, attack));
}
