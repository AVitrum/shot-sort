using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Focus;
using ScreenSorter.Core.Rules;
using ScreenSorter.Core.Sorting;
using ScreenSorter.Infrastructure.Files;
using ScreenSorter.Infrastructure.Games;
using ScreenSorter.Infrastructure.Intake;
using ScreenSorter.Infrastructure.Storage;
using ScreenSorter.Infrastructure.Win32;

namespace ScreenSorter.Infrastructure;

/// <summary>DI registration for the sorting pipeline and its Windows implementations.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers options, the pipeline, the focus tracker and the intake worker
    /// (the last two as hosted services, started in that order).
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

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SortActivity>();
        services.AddSingleton<FocusHistory>();
        services.AddSingleton<IFocusHistory>(sp => sp.GetRequiredService<FocusHistory>());
        services.AddSingleton<IGameTitleProvider, SteamGameTitleProvider>();
        services.AddSingleton<GameDetector>();
        services.AddSingleton<RulesEngine>();
        services.AddSingleton<IScreenshotStore, FileSystemScreenshotStore>();
        services.AddSingleton<IDecisionLog, SqliteDecisionLog>();
        services.AddSingleton<SortPipeline>();

        // Focus tracking must be running before intake sorts the first new file.
        services.AddHostedService<WindowsFocusTracker>();
        services.AddHostedService<IntakeService>();
        return services;
    }
}
