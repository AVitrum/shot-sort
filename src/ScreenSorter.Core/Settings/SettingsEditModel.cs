using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Vision;
using ScreenSorter.Core.Sorting;

namespace ScreenSorter.Core.Settings;

/// <summary>One row of the rule table. Lists are comma-separated text so they can be edited in a cell.</summary>
public sealed class RuleRow
{
    /// <summary>Process names, or <see cref="RulesOptions.BrowserToken"/> for any browser.</summary>
    public string Processes { get; set; } = "";

    /// <summary>Optional text that the window title must contain.</summary>
    public string TitleContains { get; set; } = "";

    /// <summary>Target folder: <c>Category</c> or <c>Category/Sub</c>.</summary>
    public string Target { get; set; } = "";

    /// <summary>Optional file-name prefix.</summary>
    public string Name { get; set; } = "";
}

/// <summary>One game alias: an exe name, install folder or title, and the title to use.</summary>
public sealed class AliasRow
{
    /// <summary>The name to match.</summary>
    public string Key { get; set; } = "";

    /// <summary>The title to use for it.</summary>
    public string Title { get; set; } = "";
}

/// <summary>One vision-model category and the folder its screenshots go to.</summary>
public sealed class CategoryRow
{
    /// <summary>The category name the model answers with.</summary>
    public string Key { get; set; } = "";

    /// <summary>The folder under the sorted root.</summary>
    public string Folder { get; set; } = "";
}

/// <summary>
/// What the settings window edits. Numbers are kept as text so that bad input reaches <see cref="Validate"/>
/// instead of being dropped by the UI binding. Not thread-safe; used on the UI thread.
/// </summary>
public sealed class SettingsEditModel
{
    /// <summary>Folders watched for new screenshots.</summary>
    public ObservableCollection<string> WatchFolders { get; } = [];

    /// <summary>Root of the category folders.</summary>
    public string SortedRoot { get; set; } = "";

    /// <summary>Name of the review folder under <see cref="SortedRoot"/>.</summary>
    public string ReviewFolder { get; set; } = "";

    /// <summary>Whether screenshots taken while the app was not running are sorted too.</summary>
    public bool BacklogEnabled { get; set; }

    /// <summary>Whether new versions are looked for and installed by themselves.</summary>
    public bool UpdatesEnabled { get; set; }

    /// <summary>Whether a screenshot the user moves is renamed to match its new folder.</summary>
    public bool RenameMovedFiles { get; set; }

    /// <summary>Whether batch summaries are shown.</summary>
    public bool NotificationsEnabled { get; set; }

    /// <summary>App rules, in priority order.</summary>
    public ObservableCollection<RuleRow> Rules { get; } = [];

    /// <summary>Browser process names, comma-separated.</summary>
    public string BrowserProcesses { get; set; } = "";

    /// <summary>Game aliases.</summary>
    public ObservableCollection<AliasRow> GameAliases { get; } = [];

    /// <summary>Extra known game titles, one per line.</summary>
    public string ExtraGameTitles { get; set; } = "";

    /// <summary>Vision categories and their folders.</summary>
    public ObservableCollection<CategoryRow> Categories { get; } = [];

    /// <summary>Minimum similarity of an example, 0–1.</summary>
    public string ExampleSimilarity { get; set; } = "";

    /// <summary>Share of examples that must agree, 0–1.</summary>
    public string ExampleAgreement { get; set; } = "";

    /// <summary>Minimum confidence of a vision answer, 0–1.</summary>
    public string VisionConfidence { get; set; } = "";

    /// <summary>Minutes without input before the vision model may run.</summary>
    public string NoInputMinutes { get; set; } = "";

    /// <summary>Highest GPU load, in percent, at which the vision model may start.</summary>
    public string MaxGpuUtilPercent { get; set; } = "";

    /// <summary>Whether a fullscreen app keeps the vision model from running.</summary>
    public bool SkipWhenFullscreen { get; set; }

    /// <summary>Whether a vision model looks at screenshots the rules and examples could not place.</summary>
    public bool VisionEnabled { get; set; }

    /// <summary>LM Studio's OpenAI-compatible URL.</summary>
    public string LmStudioUrl { get; set; } = "";

    /// <summary>Whether the LM Studio address may be on another computer (https only); screenshots are sent there.</summary>
    public bool LmStudioAllowRemote { get; set; }

    /// <summary>Vision model name in LM Studio.</summary>
    public string LmStudioModel { get; set; } = "";

    /// <summary>Days window titles and program paths are kept; 0 means they are not stored.</summary>
    public string WindowTitleDays { get; set; } = "";

    /// <summary>Days log files are kept.</summary>
    public string LogDays { get; set; } = "";

    /// <summary>Fills a model from the settings in effect.</summary>
    /// <param name="paths">Folder settings.</param>
    /// <param name="rules">Rule settings.</param>
    /// <param name="catalog">Known-game settings.</param>
    /// <param name="thresholds">Thresholds.</param>
    /// <param name="idle">Idle conditions.</param>
    /// <param name="lmStudio">LM Studio settings.</param>
    /// <param name="backlog">Backlog settings.</param>
    /// <param name="notifications">Notification settings.</param>
    /// <param name="corrections">Correction settings.</param>
    /// <param name="privacy">Retention settings.</param>
    /// <param name="updates">Automatic update settings.</param>
    /// <returns>A model holding copies of the values.</returns>
    public static SettingsEditModel From(PathsOptions paths, RulesOptions rules, GameCatalogOptions catalog,
        ThresholdsOptions thresholds, IdleOptions idle, LmStudioOptions lmStudio, BacklogOptions backlog,
        NotificationsOptions notifications, CorrectionsOptions corrections, PrivacyOptions privacy, UpdatesOptions updates)
    {
        var model = new SettingsEditModel
        {
            SortedRoot = paths.SortedRoot,
            ReviewFolder = paths.ReviewFolder,
            BacklogEnabled = backlog.Enabled,
            UpdatesEnabled = updates.Enabled,
            RenameMovedFiles = corrections.RenameMovedFiles,
            NotificationsEnabled = !string.Equals(notifications.Mode, NotificationsOptions.Off, StringComparison.OrdinalIgnoreCase),
            BrowserProcesses = string.Join(", ", rules.BrowserProcesses),
            ExtraGameTitles = string.Join(Environment.NewLine, catalog.ExtraTitles),
            ExampleSimilarity = Text(thresholds.ExampleSimilarity),
            ExampleAgreement = Text(thresholds.ExampleAgreement),
            VisionConfidence = Text(thresholds.VisionConfidence),
            NoInputMinutes = Text(idle.NoInputMinutes),
            MaxGpuUtilPercent = idle.MaxGpuUtilPercent.ToString(CultureInfo.InvariantCulture),
            SkipWhenFullscreen = idle.SkipWhenFullscreen,
            VisionEnabled = lmStudio.Enabled,
            LmStudioUrl = lmStudio.BaseUrl,
            LmStudioAllowRemote = lmStudio.AllowRemote,
            LmStudioModel = lmStudio.Model,
            WindowTitleDays = privacy.WindowTitleDays.ToString(CultureInfo.InvariantCulture),
            LogDays = privacy.LogDays.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var folder in paths.WatchFolders)
            model.WatchFolders.Add(folder);
        foreach (var rule in rules.AppRules)
        {
            model.Rules.Add(new RuleRow
            {
                Processes = string.Join(", ", rule.Processes),
                TitleContains = string.Join(", ", rule.TitleContains),
                Target = rule.Target,
                Name = rule.Name ?? "",
            });
        }
        foreach (var (key, title) in rules.GameAliases.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
            model.GameAliases.Add(new AliasRow { Key = key, Title = title });
        foreach (var (key, folder) in lmStudio.Categories)
            model.Categories.Add(new CategoryRow { Key = key, Folder = folder });
        return model;

        static string Text(double value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Adds a folder to watch unless it is blank or already listed (paths are compared without regard to case).</summary>
    /// <param name="folder">Full path of the folder.</param>
    /// <returns>True when the folder was added.</returns>
    public bool AddWatchFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || WatchFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            return false;
        WatchFolders.Add(folder);
        return true;
    }

    /// <summary>Moves a rule up or down the table; rules are tried in table order.</summary>
    /// <param name="rule">A row of <see cref="Rules"/>.</param>
    /// <param name="offset">Negative moves it up, positive down.</param>
    /// <returns>True when it moved; false when the row is not in the table or would leave it.</returns>
    public bool MoveRule(RuleRow rule, int offset)
    {
        var from = Rules.IndexOf(rule);
        var to = from + offset;
        if (from < 0 || offset == 0 || to < 0 || to >= Rules.Count)
            return false;
        Rules.Move(from, to);
        return true;
    }

    /// <summary>Checks the values. Rows left completely empty are ignored.</summary>
    /// <returns>One message per problem; empty when the model can be saved.</returns>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (WatchFolders.All(string.IsNullOrWhiteSpace))
            problems.Add("Add at least one watch folder.");
        if (string.IsNullOrWhiteSpace(SortedRoot))
            problems.Add("Choose the folder for sorted screenshots.");
        if (!IsFolderName(ReviewFolder))
            problems.Add("The review folder must be a plain folder name.");

        foreach (var rule in ActiveRules())
        {
            if (Split(rule.Processes).Count == 0)
                problems.Add($"Rule for \"{rule.Target}\" needs at least one process.");
            if (!IsTarget(rule.Target))
                problems.Add($"Rule for \"{rule.Processes}\" needs a target like Code or Chat/Discord.");
        }

        var aliases = GameAliases.Where(a => !IsBlank(a.Key, a.Title)).ToList();
        if (aliases.Any(a => IsBlank(a.Key) || IsBlank(a.Title)))
            problems.Add("Every game alias needs both a name and a title.");
        foreach (var duplicate in aliases.Where(a => !IsBlank(a.Key)).GroupBy(a => a.Key.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"Game alias \"{duplicate.Key}\" appears more than once.");

        foreach (var category in Categories.Where(c => !IsFolderName(c.Folder)))
            problems.Add($"Category \"{category.Key}\" needs a plain folder name.");

        CheckRange(problems, ExampleSimilarity, "Example similarity", 0, 1);
        CheckRange(problems, ExampleAgreement, "Example agreement", 0, 1);
        CheckRange(problems, VisionConfidence, "Vision confidence", 0, 1);
        CheckRange(problems, NoInputMinutes, "Minutes without input", 0, 1440);
        CheckRange(problems, MaxGpuUtilPercent, "Maximum GPU load", 0, 100);
        CheckRange(problems, WindowTitleDays, "Days to keep window titles", 0, 3650);
        CheckRange(problems, LogDays, "Days to keep logs", 1, 3650);

        if (LmStudioAddress.Problem(LmStudioUrl, LmStudioAllowRemote) is { } addressProblem)
            problems.Add(addressProblem);
        if (string.IsNullOrWhiteSpace(LmStudioModel))
            problems.Add("Enter the LM Studio model name.");

        return problems;
    }

    /// <summary>Serializes the editable settings for the user's settings file. Call only after <see cref="Validate"/> passes.</summary>
    /// <returns>Indented JSON.</returns>
    public string ToJson()
    {
        var url = new Uri(LmStudioUrl.Trim());
        var aliases = new JsonObject();
        foreach (var alias in GameAliases.Where(a => !IsBlank(a.Key, a.Title)))
            aliases[alias.Key.Trim()] = alias.Title.Trim();
        var categories = new JsonObject();
        foreach (var category in Categories)
            categories[category.Key] = category.Folder.Trim();

        var root = new JsonObject
        {
            [PathsOptions.Section] = new JsonObject
            {
                [nameof(PathsOptions.WatchFolders)] = Array(WatchFolders.Where(f => !IsBlank(f)).Select(f => f.Trim())),
                [nameof(PathsOptions.SortedRoot)] = SortedRoot.Trim(),
                [nameof(PathsOptions.ReviewFolder)] = ReviewFolder.Trim(),
            },
            [BacklogOptions.Section] = new JsonObject { [nameof(BacklogOptions.Enabled)] = BacklogEnabled },
            [UpdatesOptions.Section] = new JsonObject { [nameof(UpdatesOptions.Enabled)] = UpdatesEnabled },
            [CorrectionsOptions.Section] = new JsonObject { [nameof(CorrectionsOptions.RenameMovedFiles)] = RenameMovedFiles },
            [NotificationsOptions.Section] = new JsonObject
            {
                [nameof(NotificationsOptions.Mode)] = NotificationsEnabled ? NotificationsOptions.BatchSummary : NotificationsOptions.Off,
            },
            [RulesOptions.Section] = new JsonObject
            {
                [nameof(RulesOptions.BrowserProcesses)] = Array(Split(BrowserProcesses)),
                [nameof(RulesOptions.GameAliases)] = aliases,
                [nameof(RulesOptions.AppRules)] = new JsonArray(ActiveRules().Select(RuleJson).ToArray<JsonNode?>()),
            },
            [GameCatalogOptions.Section] = new JsonObject
            {
                [nameof(GameCatalogOptions.ExtraTitles)] = Array(ExtraGameTitles.Split('\n').Select(t => t.Trim()).Where(t => t.Length > 0)),
            },
            [ThresholdsOptions.Section] = new JsonObject
            {
                [nameof(ThresholdsOptions.ExampleSimilarity)] = Number(ExampleSimilarity),
                [nameof(ThresholdsOptions.ExampleAgreement)] = Number(ExampleAgreement),
                [nameof(ThresholdsOptions.VisionConfidence)] = Number(VisionConfidence),
            },
            [IdleOptions.Section] = new JsonObject
            {
                [nameof(IdleOptions.NoInputMinutes)] = Number(NoInputMinutes),
                [nameof(IdleOptions.MaxGpuUtilPercent)] = (int)Math.Round(Number(MaxGpuUtilPercent)),
                [nameof(IdleOptions.SkipWhenFullscreen)] = SkipWhenFullscreen,
            },
            [LmStudioOptions.Section] = new JsonObject
            {
                [nameof(LmStudioOptions.Enabled)] = VisionEnabled,
                [nameof(LmStudioOptions.BaseUrl)] = LmStudioUrl.Trim(),
                [nameof(LmStudioOptions.AllowRemote)] = LmStudioAllowRemote,
                // LM Studio serves its own API from the same address, so the user edits one URL.
                [nameof(LmStudioOptions.ManagementUrl)] = $"{url.Scheme}://{url.Authority}/api/v1",
                [nameof(LmStudioOptions.Model)] = LmStudioModel.Trim(),
                [nameof(LmStudioOptions.Categories)] = categories,
            },
            [PrivacyOptions.Section] = new JsonObject
            {
                [nameof(PrivacyOptions.WindowTitleDays)] = (int)Math.Round(Number(WindowTitleDays)),
                [nameof(PrivacyOptions.LogDays)] = (int)Math.Round(Number(LogDays)),
            },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        static JsonArray Array(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)i).ToArray());
    }

    private IEnumerable<RuleRow> ActiveRules() =>
        Rules.Where(r => !IsBlank(r.Processes, r.TitleContains, r.Target, r.Name));

    private static JsonObject RuleJson(RuleRow rule)
    {
        var json = new JsonObject
        {
            [nameof(AppRule.Processes)] = new JsonArray(Split(rule.Processes).Select(p => (JsonNode?)p).ToArray()),
            [nameof(AppRule.TitleContains)] = new JsonArray(Split(rule.TitleContains).Select(p => (JsonNode?)p).ToArray()),
            [nameof(AppRule.Target)] = rule.Target.Trim(),
        };
        if (!IsBlank(rule.Name))
            json[nameof(AppRule.Name)] = rule.Name.Trim();
        return json;
    }

    private static List<string> Split(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool IsBlank(params string?[] values) => values.All(string.IsNullOrWhiteSpace);

    private static bool IsFolderName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && SortedPathBuilder.Sanitize(name.Trim()) == name.Trim();

    private static bool IsTarget(string? target)
    {
        var parts = (target ?? "").Split(['/', '\\']);
        return parts.Length is 1 or 2 && parts.All(IsFolderName);
    }

    // Accepts a decimal comma too: this is typed by hand on machines with any locale.
    private static double Number(string? text) =>
        double.Parse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static void CheckRange(List<string> problems, string? text, string label, double min, double max)
    {
        if (!double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || value < min || value > max)
            problems.Add($"{label} must be a number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.");
    }
}
