using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ScreenSorter.Core.Settings;

namespace ScreenSorter.App;

/// <summary>
/// Edits a <see cref="SettingsEditModel"/>. Nothing is applied here: <see cref="SaveRequested"/> hands valid values to
/// the owner. Settings take effect on the restart that follows, because the services read them once at start.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsEditModel _model;

    /// <summary>Creates the window.</summary>
    /// <param name="model">The values to edit.</param>
    /// <param name="startWithWindows">Current state of the Start with Windows checkbox.</param>
    public SettingsWindow(SettingsEditModel model, bool startWithWindows)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        StartWithWindowsBox.IsChecked = startWithWindows;
    }

    /// <summary>Raised when the user saves valid values.</summary>
    public event EventHandler? SaveRequested;

    /// <summary>The state of the Start with Windows checkbox.</summary>
    public bool StartWithWindows => StartWithWindowsBox.IsChecked == true;

    /// <summary>Raised when the user asks for the guide.</summary>
    public event EventHandler? GuideRequested;

    private void OnOpenGuide(object sender, RoutedEventArgs e) => GuideRequested?.Invoke(this, EventArgs.Empty);

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // A cell still in edit mode has not reached the model yet.
        foreach (var grid in new[] { RulesGrid, AliasesGrid, CategoriesGrid })
        {
            grid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
            grid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        }

        var problems = _model.Validate();
        Problems.Text = string.Join(Environment.NewLine, problems);
        if (problems.Count == 0)
            SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnAddWatchFolder(object sender, RoutedEventArgs e)
    {
        if (PickFolder() is { } folder && !_model.WatchFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            _model.WatchFolders.Add(folder);
    }

    private void OnRemoveWatchFolder(object sender, RoutedEventArgs e)
    {
        if (WatchFolderList.SelectedItem is string folder)
            _model.WatchFolders.Remove(folder);
    }

    private void OnBrowseSortedRoot(object sender, RoutedEventArgs e)
    {
        if (PickFolder() is { } folder)
            SortedRootBox.Text = folder;
    }

    private void OnAddRule(object sender, RoutedEventArgs e) => AddRow(RulesGrid, _model.Rules, new RuleRow());

    private void OnRemoveRule(object sender, RoutedEventArgs e) => RemoveSelectedRow(RulesGrid, _model.Rules);

    private void OnAddAlias(object sender, RoutedEventArgs e) => AddRow(AliasesGrid, _model.GameAliases, new AliasRow());

    private void OnRemoveAlias(object sender, RoutedEventArgs e) => RemoveSelectedRow(AliasesGrid, _model.GameAliases);

    private static void AddRow<T>(DataGrid grid, ICollection<T> rows, T row)
    {
        grid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        rows.Add(row);
        grid.SelectedItem = row;
        grid.ScrollIntoView(row);
        grid.CurrentCell = new DataGridCellInfo(row, grid.Columns[0]);
        grid.Focus();
        grid.BeginEdit();
    }

    private static void RemoveSelectedRow<T>(DataGrid grid, ICollection<T> rows)
    {
        // Cancel, not commit: a row in edit mode cannot be removed, and its edits are going away with it.
        grid.CancelEdit(DataGridEditingUnit.Row);
        if (grid.SelectedItem is T row)
            rows.Remove(row);
    }

    private void OnMoveRuleUp(object sender, RoutedEventArgs e) => MoveRule(-1);

    private void OnMoveRuleDown(object sender, RoutedEventArgs e) => MoveRule(1);

    private void MoveRule(int offset)
    {
        RulesGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        if (RulesGrid.SelectedItem is not RuleRow rule)
            return;
        var from = _model.Rules.IndexOf(rule);
        var to = from + offset;
        if (from >= 0 && to >= 0 && to < _model.Rules.Count)
        {
            _model.Rules.Move(from, to);
            RulesGrid.SelectedItem = rule;
        }
    }

    private string? PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder" };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }
}
