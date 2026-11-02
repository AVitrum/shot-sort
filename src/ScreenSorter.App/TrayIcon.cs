using System.Diagnostics;
using System.IO;
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
/// Notification-area icon with the tray menu (pause/resume, sort now, open folders, guide, settings, exit)
/// and batch-summary balloons. Double-clicking the icon opens the settings. Must be created on the UI thread.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly SortActivity _activity;
    private readonly PathsOptions _paths;
    private readonly Dispatcher _dispatcher;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private readonly StatusMonitor _status;
    private readonly bool _showSummaries;
    private bool _balloonOpensStatus;

    /// <summary>Creates and shows the tray icon.</summary>
    /// <param name="activity">Shared run state.</param>
    /// <param name="paths">Folder settings, for the "open folder" items.</param>
    /// <param name="notifications">Whether batch summaries are shown.</param>
    /// <param name="status">Helper status, for the tooltip and for a notification when one stops working.</param>
    public TrayIcon(SortActivity activity, IOptions<PathsOptions> paths, IOptions<NotificationsOptions> notifications, StatusMonitor status)
    {
        _activity = activity;
        _status = status;
        _paths = paths.Value;
        _showSummaries = !string.Equals(notifications.Value.Mode, NotificationsOptions.Off, StringComparison.OrdinalIgnoreCase);
        _dispatcher = Dispatcher.CurrentDispatcher;

        _pauseItem = new Forms.ToolStripMenuItem("Pause", null, (_, _) => TogglePause());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_pauseItem);
        menu.Items.Add("Sort now", null, (_, _) => _activity.RequestSortNow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Open review folder", null, (_, _) => OpenFolder(ReviewFolder));
        menu.Items.Add("Open screenshots folder", null, (_, _) => OpenFolder(PathsOptions.Expand(_paths.SortedRoot)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("History", null, (_, _) => HistoryRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Status", null, (_, _) => StatusRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("How it works", null, (_, _) => GuideRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Settings...", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        using var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "ScreenSorter",
            ContextMenuStrip = menu,
            Visible = true,
        };
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
        _icon.Visible = false;
        _icon.Dispose();
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
        _pauseItem.Text = _activity.IsPaused ? "Resume" : "Pause";
        var attention = _status.Latest.FirstOrDefault(c => c.NeedsAttention);
        _icon.Text = _activity.IsPaused ? "ScreenSorter (paused)"
            : _activity.IsHeld ? "ScreenSorter (waiting for the guide)"
            : attention is not null ? $"ScreenSorter ({attention.Name} needs attention)"
            : "ScreenSorter";
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
        var text = summary.Review > 0
            ? $"Sorted {summary.Sorted} screenshots, {summary.Review} need review"
            : $"Sorted {summary.Sorted} screenshots";
        _icon.ShowBalloonTip(5000, "ScreenSorter", text, Forms.ToolTipIcon.Info);
    });

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
