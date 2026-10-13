using Dingler.Data.Entities.GameData;
using Dingler.Data.Enums;
using Dingler.Data.Repositories;
using Dingler.Server.Abstractions;
using Microsoft.Extensions.Logging;
using static Dingler.Game.Sieges.SiegeWire;

namespace Dingler.Game.Sieges;

/// <summary>
/// Attacks live in memory (SiegeAttacks), so a server restart forgets them, and their defenses would stay INGAME: locked,
/// with nobody able to attack, finish or withdraw them. When the server starts, each such attack ends as forfeited by
/// its attacker, the same as a disconnect: the defense counts it as defended and goes back to ACTIVE.
/// </summary>
public sealed class SiegeStartupCleanup : IAsyncStartupService
{
	private readonly SiegeRepository _sieges;
	private readonly SiegeAttacks _attacks;
	private readonly ILogger<SiegeStartupCleanup>? _logger;

	public SiegeStartupCleanup(SiegeRepository sieges, SiegeAttacks attacks, ILogger<SiegeStartupCleanup>? logger = null)
	{
		_sieges = sieges;
		_attacks = attacks;
		_logger = logger;
	}

	public async Task InitializeAsync(CancellationToken token)
	{
		foreach (var defense in await _sieges.GetOpenAsync())
		{
			// The server can be stopped and started again from the menu without forgetting anything.
			if (defense.Status != SiegeStatus.InGame || _attacks.TryGetBySiege(defense.Id, out _))
				continue;

			var lastChallenge = (await _sieges.GetLedgerAsync(defense.Id)).FirstOrDefault(l => l.Action == SiegeAction.Challenge);
			var now = Now();
			var ended = await _sieges.EndAttackDefendedAsync(defense.Id, new SiegeLedger
			{
				Action = SiegeAction.Defended,
				PlayerProfileId = lastChallenge?.PlayerProfileId ?? 0,
				PlayerName = lastChallenge?.PlayerName ?? "",
				Forfeit = now,
				Created = now
			});
			if (ended is not null)
				_logger?.LogInformation("Siege: the attack by {user} on {defender}'s defense {id} was cut off by a server restart; it counts as forfeited",
					lastChallenge?.PlayerName, ended.PlayerName, ended.Id);
		}
	}
}
