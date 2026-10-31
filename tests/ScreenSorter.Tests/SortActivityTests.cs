using ScreenSorter.Core.Sorting;

namespace ScreenSorter.Tests;

public class SortActivityTests
{
    [Fact]
    public async Task WaitWhilePaused_BlocksUntilResume()
    {
        var activity = new SortActivity();
        Assert.True(activity.WaitWhilePausedAsync(CancellationToken.None).IsCompleted);

        activity.Pause();
        var wait = activity.WaitWhilePausedAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        activity.Resume();
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void RequestSortNow_ResumesAndRaises()
    {
        var activity = new SortActivity();
        var raised = false;
        activity.SortNowRequested += (_, _) => raised = true;
        activity.Pause();

        activity.RequestSortNow();

        Assert.False(activity.IsPaused);
        Assert.True(raised);
    }
}
