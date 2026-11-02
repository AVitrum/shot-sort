using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Sorting;
using ScreenSorter.Infrastructure.Status;

namespace ScreenSorter.App;

/// <summary>
/// Shows what the app is doing and has done: whether its helpers run, where each screenshot went and why, and how
/// often the user disagreed. The history page can undo a move or correct it; everything else only reads.
/// </summary>
public partial class ActivityWindow : Window
{
    private const int HistoryLimit = 2000;
    private const int PreviewWidth = 520;
    // The correction watcher waits for the file to settle before it learns from a move, then renames it.
    private const int SecondsAfterSettling = 2;

    private readonly StatusMonitor _monitor;
    private readonly DecisionHistory _history;
    private readonly IDecisionLog _log;
    private readonly string _reviewFolder;
    private readonly string _sortedRoot;
    private readonly TimeSpan _learningDelay;
    private IReadOnlyList<HistoryItem> _items = [];
    private int _previewRequest;

    /// <summary>Creates the window and follows the status monitor until it closes.</summary>
    /// <param name="monitor">Source of the status page.</param>
    /// <param name="history">Source of the history page.</param>
    /// <param name="log">Source of the accuracy page.</param>
    /// <param name="paths">Folder settings.</param>
    /// <param name="corrections">How long a moved file takes to be learned.</param>
    public ActivityWindow(StatusMonitor monitor, DecisionHistory history, IDecisionLog log, IOptions<PathsOptions> paths, IOptions<CorrectionsOptions> corrections)
    {
        InitializeComponent();
        _monitor = monitor;
        _history = history;
        _log = log;
        _reviewFolder = paths.Value.ReviewFolder;
        _sortedRoot = PathsOptions.Expand(paths.Value.SortedRoot);
        _learningDelay = TimeSpan.FromSeconds(corrections.Value.SettleSeconds + SecondsAfterSettling);

        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        VersionText.Text = $"ScreenSorter {version.Split('+')[0]}";

        ShowStatus();
        _monitor.Changed += OnStatusChanged;
        Closed += (_, _) => _monitor.Changed -= OnStatusChanged;
        Tabs.SelectionChanged += OnPageChanged;
    }

    /// <summary>Brings the status page to the front.</summary>
    public void ShowStatusPage() => Tabs.SelectedItem = StatusTab;

    /// <summary>Brings the history page to the front.</summary>
    public void ShowHistoryPage() => Tabs.SelectedItem = HistoryTab;

    private async void OnPageChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection events from the grids and the filter inside a page bubble up to here.
        if (e.OriginalSource != Tabs)
            return;
        if (Tabs.SelectedItem == HistoryTab)
            await LoadHistoryAsync();
        else if (Tabs.SelectedItem == AccuracyTab)
            await LoadAccuracyAsync();
    }

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

    private async Task LoadHistoryAsync()
    {
        var selected = (HistoryGrid.SelectedItem as HistoryItem)?.Decisions.Latest.FileHash;
        _items = await Task.Run(() => _history.ListAsync(HistoryLimit, CancellationToken.None));
        ApplyHistoryFilter();
        if (selected is not null)
            HistoryGrid.SelectedItem = HistoryGrid.Items.OfType<HistoryItem>().FirstOrDefault(i => i.Decisions.Latest.FileHash == selected);
    }

    private void ApplyHistoryFilter()
    {
        var search = HistorySearch.Text.Trim();
        var filter = HistoryFilter.SelectedIndex;
        HistoryGrid.ItemsSource = _items.Where(item => MatchesFilter(item, filter)
            && (search.Length == 0
                || item.FileName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.Folder.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private bool MatchesFilter(HistoryItem item, int filter) => filter switch
    {
        1 => item.FileExists && string.Equals(item.Decisions.Latest.Classification.Category, _reviewFolder, StringComparison.OrdinalIgnoreCase),
        2 => item.Stage is not (DecisionStage.UserCorrection or DecisionStage.Undone),
        3 => item.Stage is DecisionStage.UserCorrection or DecisionStage.Undone,
        _ => true,
    };

    private async void OnRefreshHistory(object sender, RoutedEventArgs e) => await LoadHistoryAsync();

    private void OnHistoryFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            ApplyHistoryFilter();
    }

    private void OnHistorySearchChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
            ApplyHistoryFilter();
    }

    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        HistoryMessage.Text = "";
        if (HistoryGrid.SelectedItem is not HistoryItem item)
        {
            HistoryDetails.Visibility = Visibility.Collapsed;
            HistoryPreview.Source = null;
            return;
        }

        HistoryDetails.Visibility = Visibility.Visible;
        HistoryName.Text = item.FileName;
        HistoryWhy.Text = item.Why;
        HistoryMissing.Visibility = item.FileExists ? Visibility.Collapsed : Visibility.Visible;
        UndoButton.IsEnabled = item.CanUndo;
        MoveButton.IsEnabled = item.FileExists;
        ShowButton.IsEnabled = item.FileExists;
        _ = ShowPreviewAsync(item);
    }

    private async Task ShowPreviewAsync(HistoryItem item)
    {
        var request = ++_previewRequest;
        HistoryPreview.Source = null;
        if (!item.FileExists)
            return;

        var image = await Task.Run(() => LoadPreview(item.Path));
        // A newer selection may have finished first.
        if (request == _previewRequest)
            HistoryPreview.Source = image;
    }

    private static BitmapImage? LoadPreview(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = PreviewWidth;
            // OnLoad reads the file now and releases it; otherwise the preview would keep the user from moving it.
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not HistoryItem item)
            return;
        var result = await _history.UndoAsync(item, CancellationToken.None);
        await LoadHistoryAsync();
        HistoryMessage.Text = result switch
        {
            HistoryChange.Done => "Moved back. The app leaves it alone until you move it yourself.",
            HistoryChange.Missing => "The file is no longer in this folder.",
            HistoryChange.Changed => "A different file has this name now, so it was left alone.",
            HistoryChange.NotAllowed => "This one cannot be undone.",
            _ => "It could not be moved. It may be open in another app.",
        };
    }

    private async void OnMoveTo(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not HistoryItem item)
            return;
        var dialog = new OpenFolderDialog { Title = "Move the screenshot to", InitialDirectory = _sortedRoot };
        if (dialog.ShowDialog(this) != true)
            return;

        var result = _history.MoveTo(item, dialog.FolderName);
        HistoryMessage.Text = result switch
        {
            HistoryChange.Done => "Moved. The app is learning from it, the same as when you drag a file.",
            HistoryChange.NotAllowed => "Choose a folder inside the sorted folders, not the top level or a folder elsewhere.",
            HistoryChange.Missing => "The file is no longer in this folder.",
            _ => "It could not be moved. It may be open in another app.",
        };
        if (result != HistoryChange.Done)
            return;

        var message = HistoryMessage.Text;
        await LoadHistoryAsync();
        HistoryMessage.Text = message;
        // The log changes only when the watcher has learned from the move.
        var timer = new DispatcherTimer { Interval = _learningDelay };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await LoadHistoryAsync();
        };
        timer.Start();
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is HistoryItem item && File.Exists(item.Path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
    }

    private async Task LoadAccuracyAsync()
    {
        var facts = await Task.Run(() => _log.ListFactsAsync(CancellationToken.None));
        var report = AccuracyReport.Build(facts, _reviewFolder);
        AccuracyGrid.ItemsSource = report.Stages.Select(s => new
        {
            Stage = StageName(s.Stage),
            Placed = s.Sorted,
            Moved = s.Changed,
            Kept = s.KeptShare is { } kept ? $"{kept:P0}" : "-",
            s.Review,
        }).ToList();
        CorrectionsGrid.ItemsSource = report.Corrections;
        ReviewedText.Text = report.ReviewedByUser == 1
            ? "You placed 1 screenshot from the review folder."
            : $"You placed {report.ReviewedByUser} screenshots from the review folder.";
    }

    private static string StageName(DecisionStage stage) => stage switch
    {
        DecisionStage.Rule => "App rules",
        DecisionStage.Example => "Your examples",
        DecisionStage.Vision => "Vision model",
        DecisionStage.ImageCheck => "Blank image check",
        _ => stage.ToString(),
    };
}
