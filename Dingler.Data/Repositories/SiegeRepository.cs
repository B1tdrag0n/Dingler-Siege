using Dingler.Data.Context;
using Dingler.Data.Entities.GameData;
using Dingler.Data.Enums;
using Dingler.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dingler.Data.Repositories;

/// <summary>
/// Siege defenses and their events log. Reads open their own context; changes go through the shared write queue, which
/// runs one at a time, so a check and the write that depends on it (e.g. "no open defense yet, so add one") can't be
/// split by another player's write.
/// SQLite can't sort on ulong columns, so the Ids are sorted as long (always positive here).
/// </summary>
public sealed class SiegeRepository : BaseRepository<GameDataContext>
{
	public SiegeRepository(IDbContextFactory<GameDataContext> factory, SqliteWriter<GameDataContext> writer)
		: base(factory, writer)
	{ }

	public async Task<Siege?> GetAsync(ulong siegeId)
	{
		await using var context = await _factory.CreateDbContextAsync().ConfigureAwait(false);

		return await context.Sieges.AsNoTracking()
			.FirstOrDefaultAsync(s => s.Id == siegeId)
			.ConfigureAwait(false);
	}

	/// <summary>The player's most recently committed defense, open or not.</summary>
	public async Task<Siege?> GetLatestForPlayerAsync(ulong playerId)
	{
		await using var context = await _factory.CreateDbContextAsync().ConfigureAwait(false);

		return await context.Sieges.AsNoTracking()
			.Where(s => s.PlayerProfileId == playerId)
			.OrderByDescending(s => (long)s.Id)
			.FirstOrDefaultAsync()
			.ConfigureAwait(false);
	}

	/// <summary>The player's defenses, newest first.</summary>
	public async Task<List<Siege>> GetForPlayerAsync(ulong playerId)
	{
		await using var context = await _factory.CreateDbContextAsync().ConfigureAwait(false);

		return await context.Sieges.AsNoTracking()
			.Where(s => s.PlayerProfileId == playerId)
			.OrderByDescending(s => (long)s.Id)
			.ToListAsync()
			.ConfigureAwait(false);
	}

	/// <summary>Every open defense on the server, newest first.</summary>
	public async Task<List<Siege>> GetOpenAsync()
	{
		await using var context = await _factory.CreateDbContextAsync().ConfigureAwait(false);

		return await context.Sieges.AsNoTracking()
			.Where(s => Siege.OpenStatuses.Contains(s.Status))
			.OrderByDescending(s => (long)s.Id)
			.ToListAsync()
			.ConfigureAwait(false);
	}

	/// <summary>A defense's events log, newest first, which is the order the client keeps it in.</summary>
	public async Task<List<SiegeLedger>> GetLedgerAsync(ulong siegeId)
	{
		await using var context = await _factory.CreateDbContextAsync().ConfigureAwait(false);

		return await context.SiegeLedger.AsNoTracking()
			.Where(l => l.SiegeId == siegeId)
			.OrderByDescending(l => (long)l.Id)
			.ToListAsync()
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Saves a new defense and its first log row, or returns null if the player already has an open defense. The
	/// database numbers both rows; the returned defense carries its new Id.
	/// </summary>
	public Task<Siege?> TryCreateAsync(Siege siege, SiegeLedger firstEntry)
	{
		return EnqueueWriteAsync<Siege?>(async context =>
		{
			var hasOpen = await context.Sieges
				.AnyAsync(s => s.PlayerProfileId == siege.PlayerProfileId && Siege.OpenStatuses.Contains(s.Status))
				.ConfigureAwait(false);

			if (hasOpen)
				return null;

			firstEntry.Siege = siege;
			context.Sieges.Add(siege);
			context.SiegeLedger.Add(firstEntry);
			return siege;
		});
	}

	/// <summary>
	/// Starts an attack: an ACTIVE defense that isn't the attacker's own goes INGAME, its bounty grows by the attacker's
	/// bet and the CHALLENGE row is logged. Returns null (and changes nothing) if the defense isn't open to attack.
	/// </summary>
	public Task<Siege?> TryStartAttackAsync(ulong siegeId, int bet, SiegeLedger challenge)
	{
		return EnqueueWriteAsync<Siege?>(async context =>
		{
			var siege = await context.Sieges.FindAsync(siegeId).ConfigureAwait(false);
			if (siege is null || siege.Status != SiegeStatus.Active || siege.PlayerProfileId == challenge.PlayerProfileId)
				return null;

			siege.Status = SiegeStatus.InGame;
			siege.StatusDate = challenge.Created;
			siege.LastChallengeDate = challenge.Created;
			siege.ChallengeCount++;
			siege.Pot += bet;
			challenge.Siege = siege;
			context.SiegeLedger.Add(challenge);
			return siege;
		});
	}

	/// <summary>
	/// Ends an attack the defense won: back to ACTIVE with one more win, and the DEFENDED row logged. The row's Wins is
	/// the count before this one (the client adds 1). Returns null if the defense isn't INGAME.
	/// </summary>
	public Task<Siege?> EndAttackDefendedAsync(ulong siegeId, SiegeLedger defended)
	{
		return EnqueueWriteAsync<Siege?>(async context =>
		{
			var siege = await context.Sieges.FindAsync(siegeId).ConfigureAwait(false);
			if (siege is null || siege.Status != SiegeStatus.InGame)
				return null;

			defended.Wins = siege.Wins;
			siege.Status = SiegeStatus.Active;
			siege.StatusDate = defended.Created;
			siege.Wins++;
			defended.Siege = siege;
			context.SiegeLedger.Add(defended);
			return siege;
		});
	}

	/// <summary>
	/// Ends an attack that beat all 3 fights: the defense is DEFEATED and closed, and the DEFEATED row is logged. Returns
	/// null if the defense isn't INGAME. The returned defense's Pot is the bounty the attacker won.
	/// </summary>
	public Task<Siege?> EndAttackDefeatedAsync(ulong siegeId, SiegeLedger defeated)
	{
		return EnqueueWriteAsync<Siege?>(async context =>
		{
			var siege = await context.Sieges.FindAsync(siegeId).ConfigureAwait(false);
			if (siege is null || siege.Status != SiegeStatus.InGame)
				return null;

			defeated.Wins = siege.Wins;
			siege.Status = SiegeStatus.Defeated;
			siege.StatusDate = defeated.Created;
			siege.CloseDate = defeated.Created;
			siege.Loss++;
			defeated.Siege = siege;
			context.SiegeLedger.Add(defeated);
			return siege;
		});
	}

	/// <summary>
	/// Withdraws an open defense and logs it. Returns null if there is no such defense or it is already closed.
	/// </summary>
	public Task<Siege?> CloseAsync(ulong siegeId, DateTime when)
	{
		return EnqueueWriteAsync<Siege?>(async context =>
		{
			var siege = await context.Sieges.FindAsync(siegeId).ConfigureAwait(false);
			if (siege is null || !Siege.OpenStatuses.Contains(siege.Status))
				return null;

			siege.Status = SiegeStatus.Closed;
			siege.StatusDate = when;
			siege.CloseDate = when;
			context.SiegeLedger.Add(new SiegeLedger
			{
				Siege = siege,
				Action = SiegeAction.Closed,
				PlayerProfileId = siege.PlayerProfileId,
				PlayerName = siege.PlayerName,
				Created = when
			});
			return siege;
		});
	}
}
