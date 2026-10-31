using ScreenSorter.Core.Models;

namespace ScreenSorter.Core.Sorting;

/// <summary>
/// Shared run state between the UI and the background worker: pause/resume, "sort now"
/// requests and batch notifications. Thread-safe.
/// </summary>
public sealed class SortActivity
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _resumed = NewGate(open: true);
    private bool _paused;

    /// <summary>Raised on a thread-pool thread when <see cref="IsPaused"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised on the worker thread after a non-empty batch of files is processed.</summary>
    public event EventHandler<BatchSummary>? BatchCompleted;

    /// <summary>Raised when the user asks to process pending files immediately.</summary>
    public event EventHandler? SortNowRequested;

    /// <summary>Whether processing is paused. Files keep being queued while paused.</summary>
    public bool IsPaused
    {
        get { lock (_gate) return _paused; }
    }

    /// <summary>Pauses processing after the current file.</summary>
    public void Pause() => SetPaused(true);

    /// <summary>Resumes processing.</summary>
    public void Resume() => SetPaused(false);

    /// <summary>Resumes if paused and asks the worker to rescan the watch folders now.</summary>
    public void RequestSortNow()
    {
        Resume();
        SortNowRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Completes immediately when running; otherwise when <see cref="Resume"/> is called.</summary>
    /// <param name="ct">Cancels the wait.</param>
    public Task WaitWhilePausedAsync(CancellationToken ct)
    {
        Task t;
        lock (_gate) t = _resumed.Task;
        return t.WaitAsync(ct);
    }

    /// <summary>Publishes a batch summary to <see cref="BatchCompleted"/> subscribers.</summary>
    /// <param name="summary">Counts for the batch.</param>
    public void ReportBatch(BatchSummary summary) => BatchCompleted?.Invoke(this, summary);

    private void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_paused == paused)
                return;
            _paused = paused;
            if (paused)
                _resumed = NewGate(open: false);
            else
                _resumed.TrySetResult();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static TaskCompletionSource NewGate(bool open)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (open)
            tcs.SetResult();
        return tcs;
    }
}
