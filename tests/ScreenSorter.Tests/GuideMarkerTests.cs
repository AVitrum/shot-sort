using ScreenSorter.Core.Configuration;
using ScreenSorter.Infrastructure.Files;

namespace ScreenSorter.Tests;

public class GuideMarkerTests
{
    [Fact]
    public void NotSet_UntilMarked()
    {
        using var temp = new TempFolder();
        var paths = new PathsOptions { UserSettingsPath = Path.Combine(temp.Path, "settings.json") };

        Assert.False(GuideMarker.IsSet(paths));
        GuideMarker.Set(paths);
        Assert.True(GuideMarker.IsSet(paths));
    }

    [Fact]
    public void Set_CreatesMissingFolder()
    {
        using var temp = new TempFolder();
        var paths = new PathsOptions { UserSettingsPath = Path.Combine(temp.Path, "new", "settings.json") };

        GuideMarker.Set(paths);

        Assert.True(GuideMarker.IsSet(paths));
    }

    [Fact]
    public void SurvivesTheSettingsFileBeingRewritten()
    {
        using var temp = new TempFolder();
        var paths = new PathsOptions { UserSettingsPath = Path.Combine(temp.Path, "settings.json") };
        GuideMarker.Set(paths);

        UserSettingsFile.Save(paths, "{}");

        Assert.True(GuideMarker.IsSet(paths));
    }

    [Fact]
    public void NoSettingsPath_CountsAsSeen_SoTheGuideDoesNotReturnEveryStart()
    {
        var paths = new PathsOptions { UserSettingsPath = "" };

        GuideMarker.Set(paths);

        Assert.True(GuideMarker.IsSet(paths));
    }
}
