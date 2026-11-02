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

    [Fact]
    public async Task Hold_BlocksUntilReleased()
    {
        var activity = new SortActivity();
        activity.Hold();
        var wait = activity.WaitWhilePausedAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);
        Assert.True(activity.IsStopped);
        Assert.False(activity.IsPaused);

        activity.ReleaseHold();

        await wait.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(activity.IsStopped);
    }

    [Fact]
    public void Resume_DoesNotEndTheHold()
    {
        var activity = new SortActivity();
        activity.Hold();
        activity.Pause();

        activity.Resume();

        Assert.True(activity.IsHeld);
        Assert.False(activity.WaitWhilePausedAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public void ReleaseHold_KeepsAPauseTheUserSet()
    {
        var activity = new SortActivity();
        activity.Hold();
        activity.Pause();

        activity.ReleaseHold();

        Assert.True(activity.IsPaused);
        Assert.False(activity.WaitWhilePausedAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public async Task RequestSortNow_EndsTheHold()
    {
        var activity = new SortActivity();
        activity.Hold();
        var wait = activity.WaitWhilePausedAsync(CancellationToken.None);

        activity.RequestSortNow();

        await wait.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(activity.IsHeld);
    }

    [Fact]
    public async Task PauseThenHold_NeedsBothCleared()
    {
        var activity = new SortActivity();
        activity.Pause();
        var wait = activity.WaitWhilePausedAsync(CancellationToken.None);
        activity.Hold();

        activity.Resume();
        Assert.False(wait.IsCompleted);

        activity.ReleaseHold();
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
