using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Sorting;
using ScreenSorter.Core.Status;
using ScreenSorter.Infrastructure.Status;
using Forms = System.Windows.Forms;

namespace ScreenSorter.App;

/// <summary>
/// Notification-area icon with the tray menu (pause/resume, sort now, open folders, history, status, guide, settings,
/// exit) and notifications. Double-clicking the icon opens the settings. Must be created on the UI thread.
/// </summary>
/// <remarks>
/// WPF has no notification-area icon, so that part is WinForms. The menu itself is WPF, so it follows the Fluent theme,
/// dark mode and the accent color like the windows do.
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private readonly SortActivity _activity;
    private readonly PathsOptions _paths;
    private readonly Dispatcher _dispatcher;
    private readonly Forms.NotifyIcon _icon;
    private readonly ContextMenu _menu;
    private readonly Window _menuHost;
    private readonly MenuItem _pauseItem;
    private readonly StatusMonitor _status;
    private readonly bool _showSummaries;
    private bool _balloonOpensStatus;

    /// <summary>Creates and shows the tray icon.</summary>
    /// <param name="activity">Shared run state.</param>
    /// <param name="paths">Folder settings, for the "open folder" items.</param>
    /// <param name="notifications">Whether notifications are shown.</param>
    /// <param name="status">Helper status, for the tooltip and for a notification when one stops working.</param>
    public TrayIcon(SortActivity activity, IOptions<PathsOptions> paths, IOptions<NotificationsOptions> notifications, StatusMonitor status)
    {
        _activity = activity;
        _status = status;
        _paths = paths.Value;
        _showSummaries = !string.Equals(notifications.Value.Mode, NotificationsOptions.Off, StringComparison.OrdinalIgnoreCase);
        _dispatcher = Dispatcher.CurrentDispatcher;

        _pauseItem = Item("Pause", "", TogglePause);
        _menuHost = new Window
        {
            Width = 0,
            Height = 0,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Title = "ScreenSorter menu",
        };
        _menu = new ContextMenu { Placement = PlacementMode.MousePoint, PlacementTarget = _menuHost };
        _menuHost.Deactivated += (_, _) => _menu.IsOpen = false;
        _menu.Closed += (_, _) => _menuHost.Hide();
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(Item("Sort now", "", _activity.RequestSortNow));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("Open review folder", "", () => OpenFolder(ReviewFolder)));
        _menu.Items.Add(Item("Open screenshots folder", "", () => OpenFolder(PathsOptions.Expand(_paths.SortedRoot))));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("History", "", () => HistoryRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(Item("Status", "", () => StatusRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(Item("Check for updates", "", () => CheckForUpdatesRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(Item("How it works", "", () => GuideRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(Item("Settings...", "", () => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("Exit", "", () => ExitRequested?.Invoke(this, EventArgs.Empty)));

        using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "ScreenSorter",
            Visible = true,
        };
        _icon.MouseUp += OnIconMouseUp;
        _icon.BalloonTipClicked += OnBalloonClicked;
        _icon.DoubleClick += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        _activity.StateChanged += OnStateChanged;
        _activity.BatchCompleted += OnBatchCompleted;
        _status.Changed += OnStateChanged;
        _status.AttentionNeeded += OnAttentionNeeded;
        UpdateStateText();
    }

    /// <summary>Raised on the UI thread when the user picks "History".</summary>
    public event EventHandler? HistoryRequested;

    /// <summary>Raised on the UI thread when the user picks "Status" or clicks a notification about a helper.</summary>
    public event EventHandler? StatusRequested;

    /// <summary>Raised on the UI thread when the user picks "Check for updates".</summary>
    public event EventHandler? CheckForUpdatesRequested;

    /// <summary>Raised on the UI thread when the user picks "Exit".</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised on the UI thread when the user picks "Settings..." or double-clicks the icon.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised on the UI thread when the user picks "How it works".</summary>
    public event EventHandler? GuideRequested;

    private string ReviewFolder => Path.Combine(PathsOptions.Expand(_paths.SortedRoot), _paths.ReviewFolder);

    /// <summary>Removes the icon from the notification area.</summary>
    public void Dispose()
    {
        _activity.StateChanged -= OnStateChanged;
        _activity.BatchCompleted -= OnBatchCompleted;
        _status.Changed -= OnStateChanged;
        _status.AttentionNeeded -= OnAttentionNeeded;
        _menu.IsOpen = false;
        _menuHost.Close();
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static MenuItem Item(string header, string glyph, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") },
        };
        item.Click += (_, _) => action();
        return item;
    }

    private void OnIconMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Right)
            return;

        // A popup cannot take the foreground reliably: activating it directly leaves it without the keyboard, and it
        // closes at once when one of the app's windows is in front. A hidden window takes the foreground for it, which
        // also tells us when the user clicks elsewhere.
        _menuHost.Show();
        SetForegroundWindow(new WindowInteropHelper(_menuHost).Handle);
        _menuHost.Activate();
        _menu.IsOpen = true;
        _menu.Focus();
    }

    private void TogglePause()
    {
        if (_activity.IsPaused)
            _activity.Resume();
        else
            _activity.Pause();
    }

    private void OnStateChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(UpdateStateText);

    private void UpdateStateText()
    {
        _pauseItem.Header = _activity.IsPaused ? "Resume" : "Pause";
        var attention = _status.Latest.FirstOrDefault(c => c.NeedsAttention);
        _icon.Text = TrayText.Tooltip(_activity.IsPaused, _activity.IsHeld, attention?.Name);
    }

    private void OnAttentionNeeded(object? sender, ServiceCheck check) => _dispatcher.BeginInvoke(() =>
    {
        if (!_showSummaries)
            return;
        _balloonOpensStatus = true;
        _icon.ShowBalloonTip(5000, $"ScreenSorter: {check.Name}", check.Detail, Forms.ToolTipIcon.Warning);
    });

    private void OnBalloonClicked(object? sender, EventArgs e)
    {
        if (_balloonOpensStatus)
            StatusRequested?.Invoke(this, EventArgs.Empty);
        else
            OpenFolder(ReviewFolder);
    }

    private void OnBatchCompleted(object? sender, BatchSummary summary) => _dispatcher.BeginInvoke(() =>
    {
        if (!_showSummaries)
            return;
        _balloonOpensStatus = false;
        _icon.ShowBalloonTip(5000, "ScreenSorter", TrayText.Summary(summary), Forms.ToolTipIcon.Info);
    });

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);
}
