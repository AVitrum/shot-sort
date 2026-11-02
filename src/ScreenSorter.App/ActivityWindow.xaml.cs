using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ScreenSorter.Infrastructure.Status;

namespace ScreenSorter.App;

/// <summary>Shows what the app is doing: whether its helpers run. Reads shared state; it starts and stops nothing.</summary>
public partial class ActivityWindow : Window
{
    private readonly StatusMonitor _monitor;

    /// <summary>Creates the window and follows the status monitor until it closes.</summary>
    /// <param name="monitor">Source of the status page.</param>
    public ActivityWindow(StatusMonitor monitor)
    {
        InitializeComponent();
        _monitor = monitor;

        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        VersionText.Text = $"ScreenSorter {version.Split('+')[0]}";

        ShowStatus();
        _monitor.Changed += OnStatusChanged;
        Closed += (_, _) => _monitor.Changed -= OnStatusChanged;
    }

    /// <summary>Brings the status page to the front.</summary>
    public void ShowStatusPage() => Tabs.SelectedItem = StatusTab;

    private void OnStatusChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ShowStatus);

    private void ShowStatus() => StatusList.ItemsSource = _monitor.Latest;

    private async void OnCheckNow(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            await _monitor.RefreshAsync(CancellationToken.None);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void OnCopyCommand(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is string command)
            Clipboard.SetText(command);
    }
}
