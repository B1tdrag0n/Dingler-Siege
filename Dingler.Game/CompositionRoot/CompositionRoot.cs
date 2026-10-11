extern alias HexGame;
using System.Net;
using Dingler.Server;
using Dingler.Server.Abstractions;
using Dingler.Server.Startup;
using Dingler.Data.Configuration;
using Dingler.Data.Context;
using Dingler.Data.Repositories;
using Dingler.Data.Sqlite;
using Dingler.Game.Configuration;
using Dingler.Game.GameObjects;
using Dingler.Game.GameObjects.TrackedGameZones;
using Dingler.Game.Games;
using Dingler.Game.Protocol;
using Dingler.Game.Protocol.Chat;
using Dingler.Game.Protocol.Middleware;
using Dingler.Game.Services;
using Dingler.Game.Tournaments;
using HexGame::Game.Shared;
using HexGame::Game.Shared.Mechanics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;


namespace Dingler.Game.CompositionRoot
{
    public static class CompositionRoot
    {
        public static IHost BuildHex(this IHostBuilder hostBuilder)
        {
            hostBuilder.ConfigureServices((hb, sc) =>
            {
                var gameDataLocation = hb.Configuration["GamedataLocation"] ??
                                       throw new InvalidOperationException("GamedataLocation is not configured");

                if (!Directory.Exists(gameDataLocation))
                {
                    throw new InvalidOperationException(
                        $"Game data Location does not exist or is not a directory: '{gameDataLocation}' ");
                }

                sc.AddOptions<AuthOptions>()
                    .Bind(hb.Configuration.GetSection(AuthOptions.SectionName))
                    .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _),
                        "Auth:BaseUrl must be an absolute URL, e.g. https://localhost:5000");
                
                
                // Frost Ring Arena runs: one JSON file per player, next to gameData.db unless Arena:StorePath says otherwise.
                var arenaStorePath = hb.Configuration["Arena:StorePath"] is { Length: > 0 } configuredPath
                    ? configuredPath
                    : Path.Combine(AppContext.BaseDirectory, "data", "arena");
                var arenaRunStore = new Dingler.Game.Arena.ArenaRunStore(arenaStorePath);
                sc.AddSingleton(arenaRunStore);
                sc.AddSingleton<Dingler.Game.Arena.ArenaBattleService>();

                // Siege defenses and their events log, in memory until Milestone 3 moves them into the database.
                sc.AddSingleton<Dingler.Game.Siege.SiegeStore>();

                // Deck import from the Hex Codex deck builder: the site's data folder (ids.json, gems.json) and the inbox.
                sc.AddSingleton(new Dingler.Game.DeckImport.DeckImportOptions
                {
                    SiteDataPath = hb.Configuration["DeckImport:SiteDataPath"] ?? "",
                    InboxPath = hb.Configuration["DeckImport:InboxPath"] is { Length: > 0 } inbox
                        ? inbox
                        : Path.Combine(AppContext.BaseDirectory, "data", "deck-inbox"),
                });

                sc.AddSingletonStartupService(_ => new CollectionCacheService(gameDataLocation, arenaRunStore))
                    .AddHttpClient("AuthClient", (sp, client) =>
                    {
                        var auth = sp.GetRequiredService<IOptions<AuthOptions>>().Value;
                        client.BaseAddress = new Uri(auth.BaseUrl);
                    })
                    .ConfigurePrimaryHttpMessageHandler(() =>
                    {
                        var handler = new HttpClientHandler();
                        return handler;
                    });
                var connectionString =
                    SqliteConnection.ResolveDataSource(hb.Configuration.GetConnectionString("GameData"));
                SqliteConnection.EnsureDirectoryExists(connectionString);

                sc
                    .AddSqliteDbContext<GameDataContext>(connectionString)
                    .AddScopedAsyncStartupService<TournamentManager>()
                    .AddScopedAsyncStartupService<SessionService>()
                    .AddScoped<ChatManager>()
                    .AddScoped<ChatRoomFactory>()
                    .AddScoped<TournamentCommunicator>()
                    .AddScoped<DeckRepository>()
                    .AddScoped<AccountRepository>()
                    .AddScoped<PlayerProfileRepository>()
                    .AddScoped<FriendRepository>()
                    .AddScoped<DeckService>()
                    .AddScoped<Dingler.Game.DeckImport.DeckImportService>()
                    .AddScoped<GameManager>()
                    .AddScoped<SessionManager>()
                    .AddScoped<IStreamHandler, HexStreamHandler>()
                    .AddScoped<TournamentRepository>();
            });

            DinglerEncoder.RegisterTypeSwap<TrackedPlayer, RemotePlayer>();
            DinglerEncoder.RegisterTypeSwap<TrackedCastSpells, CastSpells>();
            DinglerEncoder.RegisterTypeSwap<TrackedChampions, Champions>();
            DinglerEncoder.RegisterTypeSwap<TrackedChoosing, ChoosingZone>();
            DinglerEncoder.RegisterTypeSwap<TrackedDeck, Deck>();
            DinglerEncoder.RegisterTypeSwap<TrackedDiscard, DiscardPile>();
            DinglerEncoder.RegisterTypeSwap<TrackedHand, Hand>();
            DinglerEncoder.RegisterTypeSwap<TrackedPlayedResources, PlayedResources>();
            DinglerEncoder.RegisterTypeSwap<TrackedSimulacrum, Simulacrum>();
            DinglerEncoder.RegisterTypeSwap<TrackedUnderground, Underground>();
            DinglerEncoder.RegisterTypeSwap<TrackedWarzone, Warzone>();
            DinglerEncoder.RegisterTypeSwap<TrackedVoid, VoidPile>();

            hostBuilder.BuildGameServer((hb, options) =>
            {
                var url = hb.Configuration["Dingler:Endpoints:TCP:Url"] ?? "127.0.0.1";

                if (!int.TryParse(hb.Configuration["Dingler:Endpoints:TCP:Port"], out var port))
                {
                    port = 9933;
                }

                if (!int.TryParse(hb.Configuration["Dingler:Endpoints:TCP:IdleTimeoutSeconds"],
                        out var idleTimeoutSeconds))
                {
                    idleTimeoutSeconds = 120;
                }
                
                options.Url = IPAddress.Parse(url);
                options.Port = port;
                options.IdleTimeoutSeconds = idleTimeoutSeconds;

                options.IncomingPipelineBuilder
                    .Use(new ParseMiddleware())
                    .Use(new DecodeMiddleware());
                
                options.OutgoingPipelineBuilder
                    .Use(new EncodeMiddleware());
            });

            var host = hostBuilder.Build();
            return host;
        }
    }
}
