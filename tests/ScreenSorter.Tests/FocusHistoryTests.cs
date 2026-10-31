using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Focus;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

public class FocusHistoryTests
{
    private static FocusHistory History(int size = 16) =>
        new(Opt(new FocusOptions { HistorySize = size, IgnoredProcesses = ["ScreenClippingHost.exe"] }), new FakeTime(T0));

    [Fact]
    public void GetFocusAt_SkipsSnippingOverlay()
    {
        var h = History();
        h.Record(Focus("Discord.exe", at: T0.AddSeconds(1)));
        h.Record(Focus("ScreenClippingHost.exe", at: T0.AddSeconds(5)));

        Assert.Equal("Discord.exe", h.GetFocusAt(T0.AddSeconds(6))?.ProcessName);
    }

    [Fact]
    public void GetFocusAt_IgnoresLaterSnapshots()
    {
        var h = History();
        h.Record(Focus("Discord.exe", at: T0.AddSeconds(1)));
        h.Record(Focus("chrome.exe", at: T0.AddSeconds(10)));

        Assert.Equal("Discord.exe", h.GetFocusAt(T0.AddSeconds(5))?.ProcessName);
    }

    [Fact]
    public void GetFocusAt_BeforeTrackingStarted_ReturnsNull()
    {
        var h = History();
        h.Record(Focus("Discord.exe", at: T0));

        Assert.Null(h.GetFocusAt(T0.AddDays(-1)));
    }

    [Fact]
    public void Record_WrapsAround_KeepsNewest()
    {
        var h = History(size: 8);
        for (var i = 0; i < 20; i++)
            h.Record(Focus($"app{i}.exe", at: T0.AddSeconds(i)));

        Assert.Equal("app19.exe", h.GetFocusAt(T0.AddSeconds(100))?.ProcessName);
        Assert.Equal("app12.exe", h.GetFocusAt(T0.AddSeconds(12))?.ProcessName);
        Assert.Null(h.GetFocusAt(T0.AddSeconds(5)));
    }
}
