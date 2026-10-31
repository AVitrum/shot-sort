using ScreenSorter.Core.Models;
using ScreenSorter.Core.Rules;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

public class RulesEngineTests
{
    private sealed class FixedTitles(string? title) : IGameTitleProvider
    {
        public string? TryGetTitle(string exePath) => title;
    }

    private static RulesEngine Engine(string? manifestTitle = null)
    {
        var rules = Opt(DefaultRules());
        return new RulesEngine(rules, new GameDetector(rules, new FixedTitles(manifestTitle)));
    }

    [Theory]
    [InlineData("Discord.exe", "", "Chat", "Discord", "Discord")]
    [InlineData("discord.EXE", "", "Chat", "Discord", "Discord")]
    [InlineData("Zoom.exe", "", "Calls", null, "Zoom")]
    [InlineData("chrome.exe", "Daily standup - Google Meet", "Calls", null, "Meet")]
    [InlineData("msedge.exe", "#general | Discord", "Chat", "Discord", "Discord")]
    [InlineData("firefox.exe", "Some video - YouTube", "Streaming", null, "Streaming")]
    [InlineData("chrome.exe", "Wikipedia", "Browser", null, "Browser")]
    [InlineData("Code.exe", "Program.cs", "Code", null, "Code")]
    [InlineData("explorer.exe", "", "Desktop", null, "Desktop")]
    public void Classify_AppRules(string process, string title, string category, string? sub, string name)
    {
        var c = Engine().Classify(Focus(process, title));

        Assert.NotNull(c);
        Assert.Equal((category, sub, name, DecisionStage.Rule), (c.Category, c.SubFolder, c.Name, c.Stage));
    }

    [Fact]
    public void Classify_FirstMatchingRuleWins()
    {
        // "Meet" rule comes before "Discord" in the list.
        var c = Engine().Classify(Focus("chrome.exe", "Discord Meet-up"));

        Assert.Equal("Calls", c?.Category);
    }

    [Fact]
    public void Classify_UnknownApp_ReturnsNull() =>
        Assert.Null(Engine().Classify(Focus("notepad.exe", "notes.txt")));

    [Fact]
    public void Classify_SteamGame_UsesManifestTitle()
    {
        var c = Engine("ELDEN RING").Classify(
            Focus("eldenring.exe", "ELDEN RING™", @"D:\SteamLibrary\steamapps\common\ELDEN RING\Game\eldenring.exe"));

        Assert.Equal(("Games", "ELDEN RING"), (c?.Category, c?.SubFolder));
    }

    [Fact]
    public void Classify_Game_WithoutManifest_UsesInstallFolder() =>
        Assert.Equal("VALORANT", Engine().Classify(
            Focus("VALORANT-Win64-Shipping.exe", "VALORANT  ", @"C:\Riot Games\VALORANT\live\VALORANT.exe"))?.SubFolder);

    [Fact]
    public void Classify_Game_AliasByExeName()
    {
        var c = Engine("Counter-Strike Global Offensive").Classify(
            Focus("cs2.exe", "Counter-Strike 2", @"C:\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe"));

        Assert.Equal("Counter-Strike 2", c?.SubFolder);
    }

    [Fact]
    public void Classify_LauncherUnderLibrary_IsNotAGame()
    {
        var c = Engine().Classify(
            Focus("EpicGamesLauncher.exe", "Epic Games Launcher", @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe"));

        Assert.Null(c);
    }

    [Fact]
    public void Classify_GameRule_RunsBeforeAppRules()
    {
        // A game exe that happens to share a name with an app rule still goes to Games.
        var c = Engine().Classify(Focus("Code.exe", "", @"C:\Steam\steamapps\common\Some Game\Code.exe"));

        Assert.Equal("Games", c?.Category);
    }
}
