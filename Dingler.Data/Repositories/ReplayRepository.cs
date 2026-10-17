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
}
