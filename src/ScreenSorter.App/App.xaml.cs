using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Infrastructure;
using ScreenSorter.Infrastructure.Logging;

namespace ScreenSorter.App;

/// <summary>
/// Tray-only WPF application: builds the Generic Host, starts the background services and shows
/// the tray icon. There is no main window.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\ScreenSorter.SingleInstance";
    private Mutex? _singleInstance;
    private IHost? _host;
    private TrayIcon? _tray;

    /// <summary>Starts the host and the tray icon; exits at once if another instance is running.</summary>
    /// <param name="e">Command-line arguments; <c>--Section:Key=value</c> overrides configuration.</param>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        try
        {
            // Content root = exe folder: at login the working directory is not the app folder.
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = e.Args,
                ContentRootPath = AppContext.BaseDirectory,
            });
            var paths = builder.Configuration.GetSection(PathsOptions.Section).Get<PathsOptions>() ?? new PathsOptions();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new FileLoggerProvider(PathsOptions.Expand(paths.LogFolder)));
            builder.Logging.AddDebug();
            builder.Services.AddScreenSorter(builder.Configuration);
            builder.Services.AddSingleton<TrayIcon>();

            _host = builder.Build();
            await _host.StartAsync();
            _tray = _host.Services.GetRequiredService<TrayIcon>();
            _tray.ExitRequested += async (_, _) => await ExitAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ScreenSorter failed to start:\n{ex.Message}", "ScreenSorter",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync();
        }
    }

    private async Task ExitAsync()
    {
        _tray?.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        Shutdown();
    }
}
