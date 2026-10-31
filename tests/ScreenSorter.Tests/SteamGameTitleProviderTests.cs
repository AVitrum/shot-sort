using ScreenSorter.Infrastructure.Games;

namespace ScreenSorter.Tests;

public class SteamGameTitleProviderTests
{
    [Fact]
    public void TryGetTitle_ReadsMatchingManifest()
    {
        using var temp = new TempFolder();
        var steamapps = Path.Combine(temp.Path, "steamapps");
        Directory.CreateDirectory(Path.Combine(steamapps, "common", "ELDEN RING", "Game"));
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_1.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"1\"\n\t\"name\"\t\t\"Other Game\"\n\t\"installdir\"\t\t\"Other\"\n}\n");
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_1245620.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"1245620\"\n\t\"name\"\t\t\"ELDEN RING\"\n\t\"installdir\"\t\t\"ELDEN RING\"\n}\n");

        var title = new SteamGameTitleProvider().TryGetTitle(Path.Combine(steamapps, "common", "ELDEN RING", "Game", "eldenring.exe"));

        Assert.Equal("ELDEN RING", title);
    }

    [Fact]
    public void TryGetTitle_NotSteam_ReturnsNull() =>
        Assert.Null(new SteamGameTitleProvider().TryGetTitle(@"C:\Riot Games\VALORANT\live\VALORANT.exe"));
}
