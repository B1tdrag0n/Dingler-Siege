using Dingler.Data.Context;
using Dingler.Data.Entities.GameData;
using Dingler.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dingler.Data.Repositories;

/// <summary>Recorded games and their events (see GameReplay).</summary>
public sealed class ReplayRepository : BaseRepository<GameDataContext>
{
	public ReplayRepository(IDbContextFactory<GameDataContext> factory, SqliteWriter<GameDataContext> writer)
		: base(factory, writer)
	{ }

	/// <summary>Saves a finished game with all its events in one write. The returned row carries its new Id.</summary>
	public Task<GameReplay> AddAsync(GameReplay replay)
	{
		return EnqueueWriteAsync(context =>
		{
			context.GameReplays.Add(replay);
			return Task.FromResult(replay);
		});
	}

	/// <summary>
	/// The watchable replays of a defense's fights: those with a .replay file and an events log row. The file itself
	/// and the events are left out (ReplayFile and Events stay empty).
	/// </summary>
	public async Task<List<GameReplay>> GetWatchableForSiegeAsync(ulong siegeId)
	{
		await using var context = await _factory.CreateDbContextAsync().ConfigureAwait(false);

		return await context.GameReplays.AsNoTracking()
			.Where(r => r.SiegeId == siegeId && r.SiegeLedgerId != null && r.ReplayFile != null)
			.Select(r => new GameReplay
			{
				Id = r.Id,
				SessionName = r.SessionName,
				SiegeId = r.SiegeId,
				FightSeq = r.FightSeq,
				SiegeLedgerId = r.SiegeLedgerId,
				Player1Uid = r.Player1Uid,
				Player1Name = r.Player1Name,
				Player2Uid = r.Player2Uid,
				Player2Name = r.Player2Name,
				WinnerUid = r.WinnerUid,
				Started = r.Started,
				Ended = r.Ended,
				EventCount = r.EventCount,
			})
			.ToListAsync()
			.ConfigureAwait(false);
	}

	/// <summary>A replay's .replay file, or null if there is no such replay or it has no file.</summary>
	public async Task<byte[]?> GetFileAsync(ulong replayId)
	{
		await using var context = await _factory.CreateDbContextAsync().ConfigureAwait(false);

		return await context.GameReplays.AsNoTracking()
			.Where(r => r.Id == replayId)
			.Select(r => r.ReplayFile)
			.FirstOrDefaultAsync()
			.ConfigureAwait(false);
	}
}
