using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MyGameBuilder.Local.Api.Accounts;
using MyGameBuilder.Local.Api.Configuration;
using MyGameBuilder.Local.Api.GameStats;
using MyGameBuilder.Local.Api.Pieces;
using MyGameBuilder.Local.Api.Soap;

namespace MyGameBuilder.Local.Api.Extensions;

/// <summary>
/// Registers the backend's services. Only the piece store is backed by real archive
/// data; the accounts and game-stats stores are in-memory fakes. Paths in
/// <see cref="PieceStoreOptions"/> are resolved relative to the content root and are
/// bound lazily so test hosts can override them before the container is built.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBackend(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.Configure<PieceStoreOptions>(configuration.GetSection(PieceStoreOptions.SectionName));
        services.Configure<ServerOptions>(configuration.GetSection(ServerOptions.SectionName));
        services.Configure<FrontendOptions>(configuration.GetSection(FrontendOptions.SectionName));

        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy => policy
                .AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod());
        });

        // The launch token is sourced from the environment so a launcher can confirm
        // it reached this specific instance via /healthz. PostConfigure runs after
        // the config-bound values so it takes precedence when set.
        services.PostConfigure<ServerOptions>(options =>
        {
            var launchToken = Environment.GetEnvironmentVariable("MGB_LAUNCH_TOKEN");
            if (!string.IsNullOrEmpty(launchToken))
            {
                options.LaunchToken = launchToken;
            }
        });

        // Resolve store paths lazily inside the factories so any configuration source
        // (including test overrides) added before the container is built is honored.
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<PieceStoreOptions>>().Value;
            // A dedicated, size-limited cache isolates archive index/listing eviction from
            // the rest of the app and keeps memory bounded even with tens of thousands of users.
            var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = options.CacheSizeLimit });
            return new ArchivePieceStore(ResolvePath(environment.ContentRootPath, options.ArchiveRoot), cache);
        });

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<PieceStoreOptions>>().Value;
            return new DataPieceStore(ResolvePath(environment.ContentRootPath, options.DataRoot));
        });

        services.AddSingleton<IPieceStore>(provider =>
            new OverlayPieceStore(provider.GetRequiredService<ArchivePieceStore>(), provider.GetRequiredService<DataPieceStore>()));

        services.AddHostedService<PieceStoreInitializer>();
        services.AddSingleton<AccountStore>();
        services.AddSingleton<GameStatStore>();
        services.AddSingleton<SoapOperationHandler>();

        return services;
    }

    private static string ResolvePath(string contentRoot, string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return contentRoot;
        }

        return Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured);
    }
}
