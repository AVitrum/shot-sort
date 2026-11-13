using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Settings;
using ScreenSorter.Core.Sorting;
using ScreenSorter.Infrastructure;
using ScreenSorter.Infrastructure.Files;
using ScreenSorter.Infrastructure.Logging;
using ScreenSorter.Infrastructure.Status;
using ScreenSorter.Infrastructure.Updates;
using ScreenSorter.Infrastructure.Win32;

namespace ScreenSorter.App;

/// <summary>
/// Tray-only WPF application: builds the Generic Host, starts the background services and shows
/// the tray icon. There is no main window.
/// </summary>
public partial class App : Application
{
    // Long enough for an instance that is restarting itself to finish shutting down.
    private static readonly TimeSpan SingleInstanceWait = TimeSpan.FromSeconds(10);
    private readonly RunAtLoginRegistration _runAtLogin = new();
    private string[] _args = [];
    private Mutex? _singleInstance;
    private IHost? _host;
    private TrayIcon? _tray;
    private SettingsWindow? _settings;
    private GuideWindow? _guide;
    private ActivityWindow? _activityWindow;
    private bool _firstRun;
    private bool _exiting;

    /// <summary>Starts the host and the tray icon; exits if another instance keeps running.</summary>
    /// <param name="e">Command-line arguments; <c>--Section:Key=value</c> overrides configuration.</param>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _args = e.Args;
        DispatcherUnhandledException += OnWindowError;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log(LogLevel.Critical, args.ExceptionObject as Exception, "Unhandled exception; the app ends");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(LogLevel.Warning, args.Exception, "A background task failed and nothing awaited it");
            args.SetObserved();
        };

        if (!TryBecomeSingleInstance())
        {
            Shutdown();
            return;
        }

        try
        {
            ScreenshotsFolder.PublishEnvironmentVariable();
            // Content root = exe folder: at login the working directory is not the app folder.
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = e.Args,
                ContentRootPath = AppContext.BaseDirectory,
            });
            var settingsProblem = UserSettingsFile.Apply(builder.Configuration, AppContext.BaseDirectory);
            var paths = builder.Configuration.GetSection(PathsOptions.Section).Get<PathsOptions>() ?? new PathsOptions();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new FileLoggerProvider(PathsOptions.Expand(paths.LogFolder)));
            builder.Logging.AddDebug();
            builder.Services.AddScreenSorter(builder.Configuration);
            builder.Services.AddSingleton<TrayIcon>();

            _host = builder.Build();
            if (settingsProblem is not null)
                _host.Services.GetRequiredService<ILogger<App>>().LogWarning("{Problem}", settingsProblem);
            // Set before the services start: intake sorts what is already in the folder as soon as it runs.
            _firstRun = !GuideMarker.IsSet(paths);
            if (_firstRun)
                _host.Services.GetRequiredService<SortActivity>().Hold();
            await _host.StartAsync();
            // Exit stops the host too, but sets _exiting first; any other stop means a service failed.
            _host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(
                () => Dispatcher.BeginInvoke(OnServicesStoppedOnTheirOwn));
            _tray = _host.Services.GetRequiredService<TrayIcon>();
            _tray.ExitRequested += async (_, _) => await ExitAsync(restart: false);
            _tray.SettingsRequested += (_, _) => OpenSettings();
            _tray.GuideRequested += (_, _) => OpenGuide();
            _tray.StatusRequested += (_, _) => OpenActivity().ShowStatusPage();
            _tray.HistoryRequested += (_, _) => OpenActivity().ShowHistoryPage();
            _host.Services.GetRequiredService<UpdateService>().ReadyToInstall +=
                (_, setup) => Dispatcher.BeginInvoke(() => InstallUpdateAsync(setup));

            if (_firstRun)
                OpenGuide();
            if (builder.Configuration.GetValue<bool>("Ui:OpenSettingsOnStart"))
                OpenSettings();
            if (builder.Configuration.GetValue<bool>("Ui:OpenActivityOnStart"))
                OpenActivity();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ScreenSorter failed to start:\n{ex.Message}", "ScreenSorter",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync(restart: false);
        }
    }

    // Without this the tray icon would stay while nothing behind it sorts. One restart clears a one-off fault; a fault
    // that comes straight back gets a message instead of a restart loop.
    private async void OnServicesStoppedOnTheirOwn()
    {
        if (_exiting || _host is null)
            return;
        Log(LogLevel.Critical, null, "The background services stopped on their own; the error is logged above");
        var paths = _host.Services.GetRequiredService<IOptions<PathsOptions>>().Value;
        if (CrashRestartMarker.TryClaim(paths, DateTimeOffset.UtcNow))
        {
            await ExitAsync(restart: true);
            return;
        }

        MessageBox.Show(
            "ScreenSorter stopped after an error, and again right after restarting, so it does not restart by itself now.\n\n" +
            $"Details are in the log in {PathsOptions.Expand(paths.LogFolder)}.\nStart ScreenSorter again from the Start menu.",
            "ScreenSorter", MessageBoxButton.OK, MessageBoxImage.Error);
        await ExitAsync(restart: false);
    }

    // A fault in a window or menu handler is local to it; the sorting services keep running, so the app does too.
    private void OnWindowError(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Log(LogLevel.Error, e.Exception, "Unhandled error in a window or menu");
        e.Handled = true;
        MessageBox.Show($"Something went wrong:\n{e.Exception.Message}\n\nScreenSorter keeps running. Details are in the log.",
            "ScreenSorter", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // Before the host exists, or after it is disposed, there is no logger; the message boxes still tell the user.
    private void Log(LogLevel level, Exception? exception, string message)
    {
        try
        {
            _host?.Services.GetService<ILogger<App>>()?.Log(level, exception, "{Message}", message);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool TryBecomeSingleInstance()
    {
        var mutex = new Mutex(initiallyOwned: false, SingleInstanceName.FromArgs(_args));
        try
        {
            if (!mutex.WaitOne(SingleInstanceWait))
            {
                mutex.Dispose();
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous instance was killed; this thread now owns the mutex.
        }
        _singleInstance = mutex;
        return true;
    }

    // The guide's "Open settings" closes the guide, and the user may be about to turn off sorting of existing
    // screenshots, so the hold lasts until both windows are gone.
    private void FinishFirstRunIfDone()
    {
        if (!_firstRun || _guide is not null || _settings is not null)
            return;
        _firstRun = false;
        RememberGuideSeen();
        _host!.Services.GetRequiredService<SortActivity>().ReleaseHold();
    }

    private void RememberGuideSeen()
    {
        try
        {
            GuideMarker.Set(_host!.Services.GetRequiredService<IOptions<PathsOptions>>().Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host!.Services.GetRequiredService<ILogger<App>>().LogWarning(ex, "Could not record that the guide was shown");
        }
    }

    private void OpenGuide()
    {
        if (_guide is not null)
        {
            _guide.Activate();
            return;
        }

        var services = _host!.Services;
        var paths = services.GetRequiredService<IOptions<PathsOptions>>().Value;
        _guide = new GuideWindow(
            paths.WatchFolders.Select(PathsOptions.Expand),
            paths.ReviewFolder,
            services.GetRequiredService<IOptions<BacklogOptions>>().Value.Enabled,
            services.GetRequiredService<SortActivity>().IsHeld);
        _guide.Closed += (_, _) =>
        {
            _guide = null;
            FinishFirstRunIfDone();
        };
        _guide.SettingsRequested += (_, _) => OpenSettings();
        _guide.Show();
    }

    private ActivityWindow OpenActivity()
    {
        if (_activityWindow is not null)
        {
            _activityWindow.Activate();
            return _activityWindow;
        }

        var services = _host!.Services;
        _activityWindow = new ActivityWindow(
            services.GetRequiredService<StatusMonitor>(),
            services.GetRequiredService<DecisionHistory>(),
            services.GetRequiredService<IDecisionLog>(),
            services.GetRequiredService<IOptions<PathsOptions>>(),
            services.GetRequiredService<IOptions<CorrectionsOptions>>());
        _activityWindow.Closed += (_, _) => _activityWindow = null;
        _activityWindow.Show();
        return _activityWindow;
    }

    private void OpenSettings()
    {
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }

        var services = _host!.Services;
        var model = SettingsEditModel.From(
            services.GetRequiredService<IOptions<PathsOptions>>().Value,
            services.GetRequiredService<IOptions<RulesOptions>>().Value,
            services.GetRequiredService<IOptions<GameCatalogOptions>>().Value,
            services.GetRequiredService<IOptions<ThresholdsOptions>>().Value,
            services.GetRequiredService<IOptions<IdleOptions>>().Value,
            services.GetRequiredService<IOptions<LmStudioOptions>>().Value,
            services.GetRequiredService<IOptions<BacklogOptions>>().Value,
            services.GetRequiredService<IOptions<NotificationsOptions>>().Value,
            services.GetRequiredService<IOptions<CorrectionsOptions>>().Value,
            services.GetRequiredService<IOptions<PrivacyOptions>>().Value,
            services.GetRequiredService<IOptions<UpdatesOptions>>().Value);

        var wasEnabled = _runAtLogin.IsEnabled;
        _settings = new SettingsWindow(model, wasEnabled);
        _settings.Closed += (_, _) =>
        {
            _settings = null;
            FinishFirstRunIfDone();
        };
        _settings.GuideRequested += (_, _) => OpenGuide();
        _settings.EraseWindowTitlesRequested += async (_, _) => await EraseWindowTitlesAsync();
        _settings.SaveRequested += async (_, _) => await SaveSettingsAsync(model, wasEnabled);
        _settings.Show();
    }

    private async Task EraseWindowTitlesAsync()
    {
        try
        {
            var erased = await _host!.Services.GetRequiredService<IDecisionLog>().EraseFocusDetailsAsync(DateTimeOffset.MaxValue, CancellationToken.None);
            _settings?.ShowEraseResult(erased == 1 ? "Erased from 1 record." : $"Erased from {erased} records.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _host!.Services.GetRequiredService<ILogger<App>>().LogWarning(ex, "Could not erase window titles");
            _settings?.ShowEraseResult("That did not work. See the log.");
        }
    }

    private async Task SaveSettingsAsync(SettingsEditModel model, bool wasEnabled)
    {
        try
        {
            UserSettingsFile.Save(_host!.Services.GetRequiredService<IOptions<PathsOptions>>().Value, model.ToJson());
            if (_settings!.StartWithWindows != wasEnabled)
                _runAtLogin.Set(_settings.StartWithWindows, Environment.ProcessPath!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show(_settings!, $"Could not save the settings:\n{ex.Message}", "ScreenSorter",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Sorting must wait for the restart: only then do the saved settings apply. The restarted app does not
        // ask again, because the user has seen the guide and made their choices.
        if (_firstRun)
        {
            _firstRun = false;
            RememberGuideSeen();
        }
        _settings!.Close();
        await ExitAsync(restart: true);
    }

    // An open window may hold unsaved edits or something being read. The service offers again until it is a good moment.
    private async Task InstallUpdateAsync(string setup)
    {
        if (_settings is null && _guide is null && _activityWindow is null && !_exiting)
            await ExitAsync(restart: false, updateSetup: setup);
    }

    private async Task ExitAsync(bool restart, string? updateSetup = null)
    {
        if (_exiting)
            return;
        _exiting = true;

        _tray?.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();

        if (restart)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var arg in _args)
                start.ArgumentList.Add(arg);
            Process.Start(start);
        }
        // Only now: the installer refuses to run while the single-instance mutex is held.
        if (updateSetup is not null)
            Process.Start(UpdateInstallCommand.Create(updateSetup, AppContext.BaseDirectory, Environment.ProcessPath!));
        Shutdown();
    }
}
