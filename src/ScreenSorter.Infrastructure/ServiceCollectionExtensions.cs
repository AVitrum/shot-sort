using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Focus;
using ScreenSorter.Core.Idle;
using ScreenSorter.Core.Rules;
using ScreenSorter.Core.Sorting;
using ScreenSorter.Core.Vision;
using ScreenSorter.Infrastructure.Files;
using ScreenSorter.Infrastructure.Games;
using ScreenSorter.Infrastructure.Imaging;
using ScreenSorter.Infrastructure.Intake;
using ScreenSorter.Infrastructure.Vision;
using ScreenSorter.Infrastructure.Storage;
using ScreenSorter.Infrastructure.Win32;

namespace ScreenSorter.Infrastructure;

/// <summary>DI registration for the sorting pipeline and its Windows implementations.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers options, the pipeline and its stages, and three hosted services started in this order:
    /// focus tracker, intake worker, vision worker.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Root configuration containing the sections named in the options classes.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddScreenSorter(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PathsOptions>(configuration.GetSection(PathsOptions.Section));
        services.Configure<IntakeOptions>(configuration.GetSection(IntakeOptions.Section));
        services.Configure<FocusOptions>(configuration.GetSection(FocusOptions.Section));
        services.Configure<RulesOptions>(configuration.GetSection(RulesOptions.Section));
        services.Configure<LmStudioOptions>(configuration.GetSection(LmStudioOptions.Section));
        services.Configure<ThresholdsOptions>(configuration.GetSection(ThresholdsOptions.Section));
        services.Configure<IdleOptions>(configuration.GetSection(IdleOptions.Section));
        services.Configure<GameCatalogOptions>(configuration.GetSection(GameCatalogOptions.Section));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SortActivity>();
        services.AddSingleton<FocusHistory>();
        services.AddSingleton<IFocusHistory>(sp => sp.GetRequiredService<FocusHistory>());
        services.AddSingleton<IGameTitleProvider, SteamGameTitleProvider>();
        services.AddSingleton<IGameCatalog, GameCatalog>();
        services.AddSingleton<GameDetector>();
        services.AddSingleton<RulesEngine>();
        services.AddSingleton<IScreenshotStore, FileSystemScreenshotStore>();
        services.AddSingleton<IDecisionLog, SqliteDecisionLog>();
        services.AddSingleton<IImageInspector, WicImageInspector>();
        services.AddSingleton<ISystemActivity, WindowsSystemActivity>();
        services.AddSingleton<IdleDetector>();
        services.AddSingleton<VisionQueue>();
        services.AddSingleton<VisionMapper>();
        // One long-lived HttpClient for a single local endpoint; per-request timeouts are applied by the client.
        services.AddSingleton<IVisionClassifier>(sp => new LmStudioVisionClient(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            sp.GetRequiredService<IImageInspector>(),
            sp.GetRequiredService<IGameCatalog>(),
            sp.GetRequiredService<IOptions<LmStudioOptions>>(),
            sp.GetRequiredService<ILogger<LmStudioVisionClient>>()));
        services.AddSingleton<SortPipeline>();

        // Focus tracking must be running before intake sorts the first new file.
        services.AddHostedService<WindowsFocusTracker>();
        services.AddHostedService<IntakeService>();
        services.AddHostedService<VisionWorker>();
        return services;
    }
}
