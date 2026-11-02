using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Settings;
using ScreenSorter.Infrastructure.Win32;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

public class SettingsMergerTests
{
    [Fact]
    public void NoUserFile_ReturnsBaseUnchanged() =>
        Assert.Equal(1, JsonNode.Parse(SettingsMerger.Merge("""{"A":1}""", null))!["A"]!.GetValue<int>());

    [Fact]
    public void BaseMayContainCommentsAndTrailingCommas() =>
        Assert.Equal(1, JsonNode.Parse(SettingsMerger.Merge("{\n// note\n\"A\":1,\n}", "{}"))!["A"]!.GetValue<int>());

    [Fact]
    public void Objects_MergeKeyByKey()
    {
        var merged = JsonNode.Parse(SettingsMerger.Merge("""{"Idle":{"A":1,"B":2}}""", """{"Idle":{"B":3}}"""))!;

        Assert.Equal((1, 3), (merged["Idle"]!["A"]!.GetValue<int>(), merged["Idle"]!["B"]!.GetValue<int>()));
    }

    [Fact]
    public void Arrays_Replace_SoDeletedRulesStayDeleted()
    {
        var merged = JsonNode.Parse(SettingsMerger.Merge("""{"Rules":{"AppRules":[1,2,3]}}""", """{"Rules":{"AppRules":[9]}}"""))!;

        Assert.Equal([9], merged["Rules"]!["AppRules"]!.AsArray().Select(n => n!.GetValue<int>()));
    }

    [Fact]
    public void GameAliases_Replace_SoDeletedAliasesStayDeleted()
    {
        var merged = JsonNode.Parse(SettingsMerger.Merge("""{"Rules":{"GameAliases":{"cs2":"CS"}}}""", """{"Rules":{"GameAliases":{"x":"X"}}}"""))!;

        Assert.Null(merged["Rules"]!["GameAliases"]!["cs2"]);
        Assert.Equal("X", merged["Rules"]!["GameAliases"]!["x"]!.GetValue<string>());
    }

    [Fact]
    public void BrokenUserFile_Throws() =>
        Assert.ThrowsAny<JsonException>(() => SettingsMerger.Merge("{}", "{not json"));

    [Fact]
    public void UserFileThatIsNotAnObject_Throws() =>
        Assert.ThrowsAny<JsonException>(() => SettingsMerger.Merge("{}", "[1]"));
}

public class SettingsEditModelTests
{
    private static SettingsEditModel Model() => SettingsEditModel.From(
        new PathsOptions { WatchFolders = [@"%USERPROFILE%\Pictures\Screenshots"], SortedRoot = @"%USERPROFILE%\Pictures\Screenshots", ReviewFolder = "_Review" },
        DefaultRules(),
        new GameCatalogOptions { ExtraTitles = ["Wuthering Waves"] },
        new ThresholdsOptions(),
        new IdleOptions(),
        new LmStudioOptions { BaseUrl = "http://localhost:1234/v1", Model = "m", Categories = SortPipelineTests.LmStudio().Categories },
        new BacklogOptions(),
        new NotificationsOptions(),
        new CorrectionsOptions(),
        new PrivacyOptions());

    [Fact]
    public void Defaults_AreValid() => Assert.Empty(Model().Validate());

    [Theory]
    [InlineData("0,85")]
    [InlineData("0.85")]
    public void Validate_AcceptsDecimalCommaOrPoint(string text)
    {
        var model = Model();
        model.VisionConfidence = text;

        Assert.Empty(model.Validate());
    }

    public static TheoryData<Action<SettingsEditModel>, string> Problems => new()
    {
        { m => m.WatchFolders.Clear(), "watch folder" },
        { m => m.SortedRoot = " ", "sorted screenshots" },
        { m => m.ReviewFolder = "a/b", "review folder" },
        { m => m.ExampleSimilarity = "abc", "Example similarity" },
        { m => m.VisionConfidence = "1.5", "Vision confidence" },
        { m => m.MaxGpuUtilPercent = "101", "GPU" },
        { m => m.NoInputMinutes = "-1", "Minutes" },
        { m => m.WindowTitleDays = "-1", "window titles" },
        { m => m.LogDays = "0", "logs" },
        { m => m.LmStudioUrl = "localhost", "LM Studio URL" },
        { m => m.LmStudioModel = "", "model name" },
        { m => m.Rules.Add(new RuleRow { Target = "Code" }), "at least one process" },
        { m => m.Rules.Add(new RuleRow { Processes = "a.exe" }), "needs a target" },
        { m => m.Rules.Add(new RuleRow { Processes = "a.exe", Target = "a/b/c" }), "needs a target" },
        { m => m.Rules.Add(new RuleRow { Processes = "a.exe", Target = "a:b" }), "needs a target" },
        { m => m.GameAliases.Add(new AliasRow { Key = "x" }), "alias" },
        { m => { m.GameAliases.Add(new AliasRow { Key = "x", Title = "1" }); m.GameAliases.Add(new AliasRow { Key = "X", Title = "2" }); }, "more than once" },
        { m => m.Categories[0].Folder = "", "plain folder name" },
    };

    [Theory]
    [MemberData(nameof(Problems))]
    public void Validate_ReportsProblem(Action<SettingsEditModel> breakIt, string expected)
    {
        var model = Model();
        breakIt(model);

        Assert.Contains(model.Validate(), p => p.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_IgnoresRowsLeftEmpty()
    {
        var model = Model();
        model.Rules.Add(new RuleRow());
        model.GameAliases.Add(new AliasRow());

        Assert.Empty(model.Validate());
    }

    /// <summary>The whole path: edit, save, merge over the base file, bind into the options the app uses.</summary>
    [Fact]
    public void SavedSettings_RoundTripIntoOptions()
    {
        var model = Model();
        model.WatchFolders.Add(@"D:\Steam shots");
        model.BacklogEnabled = false;
        model.RenameMovedFiles = false;
        model.NotificationsEnabled = false;
        model.VisionConfidence = "0,5";
        model.MaxGpuUtilPercent = "30";
        model.LmStudioUrl = "http://192.168.1.5:1234/v1";
        model.BrowserProcesses = "vivaldi.exe";
        model.ExtraGameTitles = "Game A\r\n\r\nGame B\n";
        model.Rules.Clear();
        model.Rules.Add(new RuleRow { Processes = "a.exe, b.exe", TitleContains = "x, y", Target = "Chat/Thing", Name = "Thing" });
        model.Rules.Add(new RuleRow());
        model.GameAliases.Clear();
        model.GameAliases.Add(new AliasRow { Key = "StarRail", Title = "Honkai Star Rail" });

        const string baseJson = """{"Rules":{"AppRules":[{"Target":"Old1"},{"Target":"Old2"}],"GameAliases":{"cs2":"CS2"},"GameLibraryMarkers":["\\keep\\"]},"LmStudio":{"TtlSeconds":300}}""";
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(SettingsMerger.Merge(baseJson, model.ToJson()))))
            .Build();

        var rules = config.GetSection(RulesOptions.Section).Get<RulesOptions>()!;
        var rule = Assert.Single(rules.AppRules);
        Assert.Equal(["a.exe", "b.exe"], rule.Processes);
        Assert.Equal(["x", "y"], rule.TitleContains);
        Assert.Equal(("Chat/Thing", "Thing"), (rule.Target, rule.Name));
        Assert.Equal(["vivaldi.exe"], rules.BrowserProcesses);
        Assert.Equal("Honkai Star Rail", Assert.Single(rules.GameAliases).Value);
        Assert.Equal([@"\keep\"], rules.GameLibraryMarkers);

        Assert.Equal(2, config.GetSection(PathsOptions.Section).Get<PathsOptions>()!.WatchFolders.Count);
        Assert.False(config.GetSection(BacklogOptions.Section).Get<BacklogOptions>()!.Enabled);
        Assert.False(config.GetSection(CorrectionsOptions.Section).Get<CorrectionsOptions>()!.RenameMovedFiles);
        Assert.Equal(NotificationsOptions.Off, config.GetSection(NotificationsOptions.Section).Get<NotificationsOptions>()!.Mode);
        Assert.Equal(0.5, config.GetSection(ThresholdsOptions.Section).Get<ThresholdsOptions>()!.VisionConfidence);
        Assert.Equal(30, config.GetSection(IdleOptions.Section).Get<IdleOptions>()!.MaxGpuUtilPercent);
        Assert.Equal(["Game A", "Game B"], config.GetSection(GameCatalogOptions.Section).Get<GameCatalogOptions>()!.ExtraTitles);

        var lm = config.GetSection(LmStudioOptions.Section).Get<LmStudioOptions>()!;
        Assert.Equal(("http://192.168.1.5:1234/v1", "http://192.168.1.5:1234/api/v1", 300), (lm.BaseUrl, lm.ManagementUrl, lm.TtlSeconds));
    }
}

/// <summary>Uses a throwaway registry key, never the real Run key.</summary>
public sealed class RunAtLoginRegistrationTests : IDisposable
{
    private readonly string _key = @"Software\ScreenSorterTests\" + Guid.NewGuid().ToString("N");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\ScreenSorterTests", throwOnMissingSubKey: false);

    [Fact]
    public void Default_IsNotEnabled() => Assert.False(new RunAtLoginRegistration(_key).IsEnabled);

    [Fact]
    public void Set_WritesQuotedPath_ThenRemovesIt()
    {
        var registration = new RunAtLoginRegistration(_key, "App");

        registration.Set(true, @"C:\Program Files\App\App.exe");
        Assert.True(registration.IsEnabled);
        using (var key = Registry.CurrentUser.OpenSubKey(_key))
            Assert.Equal("\"C:\\Program Files\\App\\App.exe\"", key!.GetValue("App"));

        registration.Set(false, "");
        Assert.False(registration.IsEnabled);
    }

    [Fact]
    public void Removing_WhenNotSet_IsFine() => new RunAtLoginRegistration(_key).Set(false, "");
}
