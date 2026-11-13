using ScreenSorter.Core.Configuration;
using ScreenSorter.Infrastructure.Files;

namespace ScreenSorter.Tests;

public sealed class CrashRestartMarkerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 22, 0, 0, TimeSpan.Zero);
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private PathsOptions Paths => new() { UserSettingsPath = Path.Combine(_temp.Path, "cfg", "settings.json") };

    [Fact]
    public void First_restart_is_allowed() => Assert.True(CrashRestartMarker.TryClaim(Paths, Now));

    [Fact]
    public void Second_restart_within_the_window_is_refused()
    {
        Assert.True(CrashRestartMarker.TryClaim(Paths, Now));
        Assert.False(CrashRestartMarker.TryClaim(Paths, Now.AddMinutes(9)));
    }

    [Fact]
    public void Restart_after_the_window_is_allowed_again()
    {
        Assert.True(CrashRestartMarker.TryClaim(Paths, Now));
        Assert.True(CrashRestartMarker.TryClaim(Paths, Now + CrashRestartMarker.Window + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void No_settings_path_means_no_restart() =>
        Assert.False(CrashRestartMarker.TryClaim(new PathsOptions { UserSettingsPath = "" }, Now));
}
