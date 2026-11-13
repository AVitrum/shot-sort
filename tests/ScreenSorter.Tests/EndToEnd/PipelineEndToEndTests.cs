using ScreenSorter.Core.Sorting;
using static ScreenSorter.Tests.EndToEnd.AppHarness;

namespace ScreenSorter.Tests.EndToEnd;

/// <summary>
/// Whole-app flows: a screenshot lands in the watched folder and the real host sorts it, through the real intake,
/// rules, LM Studio client, file store and SQLite log. Run only these with <c>--filter Category=EndToEnd</c>.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class PipelineEndToEndTests : IAsyncLifetime
{
    private readonly TempFolder _temp = new();
    private FakeLmStudio _lmStudio = null!;

    public async Task InitializeAsync() => _lmStudio = await FakeLmStudio.StartAsync();

    public async Task DisposeAsync()
    {
        await _lmStudio.DisposeAsync();
        _temp.Dispose();
    }

    private Task<AppHarness> StartAppAsync(params (string Key, string Value)[] extra) =>
        AppHarness.StartAsync(_temp.Path, _lmStudio, extra);

    [Fact]
    public async Task Rule_sorts_a_screenshot_from_a_known_app_without_the_model()
    {
        await using var app = await StartAppAsync();
        app.FocusOn("Discord.exe");
        app.NewScreenshot("Screenshot 2026-11-13 101500.png");

        Assert.True(await Eventually(() => app.Files() is [var f] && f.StartsWith("Chat/Discord/")), app.Log);
        Assert.Equal("Chat/Discord/Discord_2026-11-13_10-15-00.png", app.Files()[0]);
        Assert.Empty(_lmStudio.Prompts);
    }

    [Fact]
    public async Task Model_sorts_what_no_rule_knows_and_gets_the_focus_hint()
    {
        _lmStudio.Answer = () => (200, FakeLmStudio.Json("Game", gameTitle: "Test Game"));
        await using var app = await StartAppAsync();
        app.FocusOn("Mystery.exe");
        app.NewScreenshot("Screenshot 2026-11-13 101600.png");

        Assert.True(await Eventually(() => app.Files() is [var f] && f.StartsWith("Games/Test Game/")), app.Log);
        Assert.True(Assert.Single(_lmStudio.Prompts).Contains("Mystery.exe"), app.Log);
    }

    [Fact]
    public async Task Unreadable_model_answer_goes_to_review()
    {
        _lmStudio.Answer = () => (200, "I think this is a game.");
        await using var app = await StartAppAsync();
        app.FocusOn("Mystery.exe");
        app.NewScreenshot("Screenshot 2026-11-13 101700.png");

        Assert.True(await Eventually(() => app.Files() is [var f] && f.StartsWith("_Review/")), app.Log);
    }

    [Fact]
    public async Task Model_down_leaves_the_file_alone_until_it_is_back()
    {
        _lmStudio.Answer = () => (500, "");
        await using var app = await StartAppAsync();
        app.FocusOn("Mystery.exe");
        app.NewScreenshot("Screenshot 2026-11-13 101800.png");

        Assert.True(await Eventually(() => _lmStudio.Prompts.Count >= 1), app.Log);
        await Task.Delay(1500);
        Assert.Equal(["Screenshot 2026-11-13 101800.png"], app.Files());

        _lmStudio.Answer = () => (200, FakeLmStudio.Json("Other"));
        app.Get<SortActivity>().RequestSortNow();
        Assert.True(await Eventually(() => app.Files() is [var f] && f.StartsWith("Other/")), app.Log);
    }

    [Fact]
    public async Task Waiting_file_keeps_its_focus_hint_across_a_restart()
    {
        _lmStudio.Answer = () => (500, "");
        await using (var first = await StartAppAsync())
        {
            first.FocusOn("Mystery.exe");
            first.NewScreenshot("Screenshot 2026-11-13 101900.png");
            Assert.True(await Eventually(() => _lmStudio.Prompts.Count >= 1), first.Log);
        }

        _lmStudio.Answer = () => (200, FakeLmStudio.Json("Other"));
        await using var second = await StartAppAsync();
        Assert.True(await Eventually(() => second.Files() is [var f] && f.StartsWith("Other/")), second.Log);
        Assert.True(_lmStudio.Prompts[^1].Contains("Mystery.exe"), second.Log);
    }

    [Fact]
    public async Task Undone_file_stays_where_the_user_put_it()
    {
        await using var app = await StartAppAsync();
        app.FocusOn("Discord.exe");
        app.NewScreenshot("Screenshot 2026-11-13 102000.png");
        Assert.True(await Eventually(() => app.Files() is [var f] && f.StartsWith("Chat/Discord/")), app.Log);

        // The pipeline moves the file first and logs the decision after, so the file can show up before its entry.
        var history = app.Get<DecisionHistory>();
        IReadOnlyList<HistoryItem> items = [];
        Assert.True(await Eventually(() => (items = history.ListAsync(10, CancellationToken.None).GetAwaiter().GetResult()).Count == 1), app.Log);
        var item = items[0];
        Assert.True(item.CanUndo);
        await history.UndoAsync(item, CancellationToken.None);
        Assert.Equal(["Screenshot 2026-11-13 102000.png"], app.Files());

        app.Get<SortActivity>().RequestSortNow();
        await Task.Delay(3000);
        Assert.Equal(["Screenshot 2026-11-13 102000.png"], app.Files());
    }

    // Text in a screenshot can steer the model, so whatever it answers must not move a file out of the sorted folder.
    [Theory]
    [InlineData(@"..\..\outside")]
    [InlineData("../../outside")]
    [InlineData(@"C:\Windows\Temp")]
    [InlineData(@"\\server\share\x")]
    [InlineData("x:stream")]
    [InlineData("CON")]
    [InlineData(" . ")]
    [InlineData(". .")]
    [InlineData("\u202Egnp.exe")]
    public async Task Hostile_model_answer_keeps_the_file_inside_the_sorted_folder(string appName)
    {
        _lmStudio.Answer = () => (200, FakeLmStudio.Json("Chat", appName: appName));
        await using var app = await StartAppAsync();
        app.FocusOn("Mystery.exe");
        app.NewScreenshot("Screenshot 2026-11-13 102100.png");

        Assert.True(await Eventually(() => app.Files() is [var f] && f.Contains('/')), app.Log);
        var everywhere = Directory.EnumerateFiles(_temp.Path, "*.png", SearchOption.AllDirectories).ToList();
        var file = Assert.Single(everywhere);
        Assert.StartsWith(app.Shots + Path.DirectorySeparatorChar, Path.GetFullPath(file));
        Assert.DoesNotContain(':', Path.GetRelativePath(app.Shots, file));
    }

    [Fact]
    public async Task Overlong_model_answer_is_cut_to_a_usable_folder_name()
    {
        _lmStudio.Answer = () => (200, FakeLmStudio.Json("Chat", appName: new string('a', 5000)));
        await using var app = await StartAppAsync();
        app.FocusOn("Mystery.exe");
        app.NewScreenshot("Screenshot 2026-11-13 102200.png");

        Assert.True(await Eventually(() => app.Files() is [var f] && f.StartsWith("Chat/")), app.Log);
        Assert.True(app.Files()[0].Split('/')[1].Length <= 80);
    }
}
