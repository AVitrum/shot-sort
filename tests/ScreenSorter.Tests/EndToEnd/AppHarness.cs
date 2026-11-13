using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScreenSorter.Core.Focus;
using ScreenSorter.Core.Idle;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Rules;
using ScreenSorter.Infrastructure;
using ScreenSorter.Infrastructure.Win32;

namespace ScreenSorter.Tests.EndToEnd;

/// <summary>
/// The app's real host (<see cref="ServiceCollectionExtensions.AddScreenSorter"/>, the shipped appsettings.json, every
/// hosted service) on a temp folder, talking to a <see cref="FakeLmStudio"/>. Only what reads the user's machine is
/// replaced: the focus tracker (tests say which window had focus), idle and GPU readings (always idle), and the
/// Steam and Epic game libraries (one known game, "Test Game").
/// </summary>
internal sealed class AppHarness : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
    private readonly IHost _host;
    private readonly ConcurrentQueue<string> _log;

    private AppHarness(IHost host, string shots, ConcurrentQueue<string> log)
    {
        _host = host;
        Shots = shots;
        _log = log;
    }

    /// <summary>The watched folder, which is also the sorted root.</summary>
    public string Shots { get; }

    /// <summary>The review folder under <see cref="Shots"/>.</summary>
    public string Review => Path.Combine(Shots, "_Review");

    /// <summary>Resolves a service from the running app.</summary>
    public T Get<T>() where T : notnull => _host.Services.GetRequiredService<T>();

    /// <summary>Everything the app logged so far, plus focus and file times, for failure messages.</summary>
    public string Log =>
        $"focus tracking since {Get<FocusHistory>().TrackingSince:HH:mm:ss.fffffff}, last focus {_lastFocus:HH:mm:ss.fffffff}; files: " +
        string.Join(", ", Directory.EnumerateFiles(Shots, "*.png", SearchOption.AllDirectories)
            .Select(f => $"{Path.GetFileName(f)} created {File.GetCreationTimeUtc(f):HH:mm:ss.fffffff}")) +
        Environment.NewLine + string.Join(Environment.NewLine, _log);

    private DateTimeOffset _lastFocus;

    /// <summary>Builds and starts the app on <paramref name="root"/>; a second start on the same root is a restart.</summary>
    /// <param name="root">Folder that holds <c>shots</c> and <c>data</c>.</param>
    /// <param name="lmStudio">The fake model server.</param>
    /// <param name="extra">More configuration overrides, as <c>Section:Key</c> pairs.</param>
    public static async Task<AppHarness> StartAsync(string root, FakeLmStudio lmStudio, params (string Key, string Value)[] extra)
    {
        var shots = Path.Combine(root, "shots");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(shots);
        Directory.CreateDirectory(data);

        var settings = new Dictionary<string, string?>
        {
            ["Paths:WatchFolders:0"] = shots,
            ["Paths:SortedRoot"] = shots,
            ["Paths:DatabasePath"] = Path.Combine(data, "test.db"),
            ["Paths:LogFolder"] = Path.Combine(data, "logs"),
            ["Paths:UserSettingsPath"] = Path.Combine(data, "settings.json"),
            ["Intake:StabilityPollMilliseconds"] = "50",
            ["Intake:BatchQuietSeconds"] = "1",
            ["LmStudio:BaseUrl"] = lmStudio.BaseUrl,
            ["LmStudio:ManagementUrl"] = lmStudio.ManagementUrl,
            ["LmStudio:AutoStartServer"] = "false",
            ["LmStudio:TimeoutSeconds"] = "10",
            ["LmStudio:ManagementTimeoutSeconds"] = "5",
            // No CLIP model: the example stage stays off, so only rules and the model decide.
            ["Clip:ModelUrl"] = "",
            ["Clip:ModelPath"] = Path.Combine(data, "none.onnx"),
            ["Corrections:SettleSeconds"] = "1",
            ["Updates:Enabled"] = "false",
        };
        foreach (var (key, value) in extra)
            settings[key] = value;

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "EndToEnd", "appsettings.json"));
        builder.Configuration.AddInMemoryCollection(settings);
        var log = new ConcurrentQueue<string>();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(new ListLoggerProvider(log));

        builder.Services.AddScreenSorter(builder.Configuration);
        var tracker = builder.Services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(WindowsFocusTracker));
        builder.Services.Remove(tracker);
        builder.Services.AddSingleton<ISystemActivity, IdleMachine>();
        builder.Services.AddSingleton<IGameTitleProvider, NoGameTitles>();
        builder.Services.AddSingleton<IGameCatalog>(new FixedCatalog("Test Game"));

        var host = builder.Build();
        await host.StartAsync();
        await WaitForFileClockAsync(data, host.Services.GetRequiredService<FocusHistory>().TrackingSince);
        return new AppHarness(host, shots, log);
    }

    // NTFS stamps creation times from the coarse system clock (~16 ms ticks), FocusHistory from the precise one. A file
    // made within one tick of start-up can look older than TrackingSince and count as backlog, so wait out that tick.
    private static async Task WaitForFileClockAsync(string folder, DateTimeOffset trackingSince)
    {
        // A new name each time: NTFS gives a file re-created under a recent name its old creation time (tunneling).
        for (var i = 0; i < 100; i++)
        {
            var probe = Path.Combine(folder, $"clock-{i}.probe");
            await File.WriteAllBytesAsync(probe, []);
            var created = File.GetCreationTimeUtc(probe);
            File.Delete(probe);
            if (created > trackingSince.UtcDateTime)
                return;
            await Task.Delay(5);
        }
    }

    /// <summary>Records that <paramref name="process"/> had focus a moment ago, as the focus tracker would.</summary>
    public void FocusOn(string process)
    {
        _lastFocus = DateTimeOffset.UtcNow.AddMilliseconds(-200);
        Get<FocusHistory>().Record(new FocusSnapshot(_lastFocus, process, $@"C:\Apps\{process}", $"{process} window"));
    }

    /// <summary>Writes a PNG with random noise (not blank) into the watched folder, as a screenshot tool would.</summary>
    public string NewScreenshot(string name, int seed = 1)
    {
        var pixels = new byte[64 * 48];
        new Random(seed).NextBytes(pixels);
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(64, 48, 96, 96,
            System.Windows.Media.PixelFormats.Gray8, null, pixels, 64);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        var path = Path.Combine(Shots, name);
        using (var stream = File.Create(path))
            encoder.Save(stream);
        return path;
    }

    /// <summary>Image files anywhere under the sorted root, relative to it, with forward slashes.</summary>
    public IReadOnlyList<string> Files() =>
        Directory.EnumerateFiles(Shots, "*.png", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Shots, f).Replace('\\', '/'))
            .Order()
            .ToList();

    /// <summary>Polls until <paramref name="condition"/> holds or the patience runs out.</summary>
    /// <returns>Whether it held in time.</returns>
    public static async Task<bool> Eventually(Func<bool> condition, TimeSpan? patience = null)
    {
        var deadline = DateTime.UtcNow + (patience ?? Patience);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(100);
        }
        return condition();
    }

    /// <summary>Stops the app as the tray's Exit does.</summary>
    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(TimeSpan.FromSeconds(10));
        _host.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private sealed class IdleMachine : ISystemActivity
    {
        public TimeSpan TimeSinceLastInput() => TimeSpan.FromHours(1);
        public bool IsFullscreenAppFocused() => false;
        public int? GpuUtilizationPercent() => 0;
    }

    private sealed class NoGameTitles : IGameTitleProvider
    {
        public string? TryGetTitle(string exePath) => null;
    }

    private sealed class ListLoggerProvider(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(lines, categoryName);
        public void Dispose() { }
    }

    private sealed class ListLogger(ConcurrentQueue<string> lines, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {logLevel} {category.Split('.')[^1]}: {formatter(state, exception)}{(exception is null ? "" : " | " + exception.Message)}");
    }
}
