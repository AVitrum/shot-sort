using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Examples;
using ScreenSorter.Core.Focus;
using ScreenSorter.Core.Idle;
using ScreenSorter.Core.Rules;
using ScreenSorter.Core.Sorting;
using ScreenSorter.Core.Status;
using ScreenSorter.Core.Vision;
using ScreenSorter.Infrastructure.Clip;
using ScreenSorter.Infrastructure.Corrections;
using ScreenSorter.Infrastructure.Files;
using ScreenSorter.Infrastructure.Games;
using ScreenSorter.Infrastructure.Imaging;
using ScreenSorter.Infrastructure.Intake;
using ScreenSorter.Infrastructure.Qdrant;
using ScreenSorter.Infrastructure.Status;
using ScreenSorter.Infrastructure.Vision;
using ScreenSorter.Infrastructure.Storage;
using ScreenSorter.Infrastructure.Win32;

namespace ScreenSorter.Infrastructure;

/// <summary>DI registration for the sorting pipeline and its Windows implementations.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers options, the pipeline and its stages, and seven hosted services started in this order:
    /// focus tracker, CLIP model download, intake worker, vision worker, correction watcher, status monitor, privacy maintenance.
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
        services.Configure<ClipOptions>(configuration.GetSection(ClipOptions.Section));
        services.Configure<QdrantOptions>(configuration.GetSection(QdrantOptions.Section));
        services.Configure<CorrectionsOptions>(configuration.GetSection(CorrectionsOptions.Section));
        services.Configure<BacklogOptions>(configuration.GetSection(BacklogOptions.Section));
        services.Configure<NotificationsOptions>(configuration.GetSection(NotificationsOptions.Section));
        services.Configure<StatusOptions>(configuration.GetSection(StatusOptions.Section));
        services.Configure<PrivacyOptions>(configuration.GetSection(PrivacyOptions.Section));

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
        // No client timeout: the model download takes minutes. It has its own cancellation.
        services.AddSingleton(sp => new ClipModelFile(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            sp.GetRequiredService<IOptions<ClipOptions>>(),
            sp.GetRequiredService<ILogger<ClipModelFile>>()));
        services.AddSingleton<IImageEmbedder, ClipImageEmbedder>();
        services.AddSingleton<IExampleStore>(sp => new QdrantExampleStore(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            sp.GetRequiredService<IOptions<QdrantOptions>>(),
            sp.GetRequiredService<IOptions<ClipOptions>>()));
        services.AddSingleton<ExampleClassifier>();
        services.AddSingleton<CorrectionProcessor>();
        services.AddSingleton<SortPipeline>();
        services.AddSingleton<DecisionHistory>();

        // Registered in display order.
        services.AddSingleton<IServiceProbe, SortingProbe>();
        services.AddSingleton<IServiceProbe>(sp => new LmStudioProbe(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            sp.GetRequiredService<VisionQueue>(),
            sp.GetRequiredService<IOptions<LmStudioOptions>>(),
            sp.GetRequiredService<IOptions<StatusOptions>>()));
        services.AddSingleton<IServiceProbe, QdrantProbe>();
        services.AddSingleton<IServiceProbe, ClipModelProbe>();
        services.AddSingleton<IServiceProbe, WatchFolderProbe>();
        services.AddSingleton<StatusMonitor>();

        // Focus tracking must be running before intake sorts the first new file.
        services.AddHostedService<WindowsFocusTracker>();
        services.AddHostedService(sp => sp.GetRequiredService<ClipModelFile>());
        services.AddHostedService<IntakeService>();
        services.AddHostedService<VisionWorker>();
        services.AddHostedService<CorrectionWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<StatusMonitor>());
        services.AddHostedService<PrivacyMaintenance>();
        return services;
    }
}
