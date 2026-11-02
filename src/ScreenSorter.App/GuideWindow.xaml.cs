using System.Windows;

namespace ScreenSorter.App;

/// <summary>A short paged guide to what the app does and how to teach it. It changes nothing by itself.</summary>
public partial class GuideWindow : Window
{
    private readonly FrameworkElement[] _pages;
    private int _page;

    /// <summary>Creates the window on its first page.</summary>
    /// <param name="watchFolders">The watched folders, with environment variables expanded.</param>
    /// <param name="reviewFolder">Name of the folder for screenshots the app could not place.</param>
    /// <param name="backlogEnabled">Whether screenshots already in the watched folders get sorted.</param>
    /// <param name="sortingHeld">Whether sorting is waiting for this window to close.</param>
    public GuideWindow(IEnumerable<string> watchFolders, string reviewFolder, bool backlogEnabled, bool sortingHeld)
    {
        InitializeComponent();
        _pages = [WelcomePage, DecidePage, TeachPage, TrayPage];

        WaitingRow.Visibility = sortingHeld ? Visibility.Visible : Visibility.Collapsed;
        WatchingRow.Text = string.Join(Environment.NewLine, watchFolders);
        BacklogRow.Visibility = backlogEnabled ? Visibility.Visible : Visibility.Collapsed;
        ReviewRow.Text = $"It goes to {reviewFolder} for you to place. Blank or black images go there too.";
        ForgetRow.Text = $"Move a screenshot into {reviewFolder} and it stops being an example.";

        ShowPage(0);
    }

    /// <summary>Raised when the user asks for the settings window from the guide.</summary>
    public event EventHandler? SettingsRequested;

    private bool OnLastPage => _page == _pages.Length - 1;

    private void ShowPage(int page)
    {
        _page = page;
        for (var i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;

        StepText.Text = $"Step {page + 1} of {_pages.Length}";
        BackButton.Visibility = page > 0 ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = OnLastPage ? Visibility.Collapsed : Visibility.Visible;
        SettingsButton.Visibility = OnLastPage ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = OnLastPage ? "Done" : "Next";
    }

    private void OnBack(object sender, RoutedEventArgs e) => ShowPage(_page - 1);

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (OnLastPage)
            Close();
        else
            ShowPage(_page + 1);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
        Close();
    }
}
