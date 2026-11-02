using ScreenSorter.Core.Models;

namespace ScreenSorter.Core.Sorting;

/// <summary>
/// Shared run state between the UI and the background worker: pause/resume, the first-run hold, "sort now"
/// requests and batch notifications. Thread-safe.
/// </summary>
/// <remarks>
/// Pause is the user's choice and the hold is the app's, so they are kept apart: closing the guide must not undo a
/// pause the user set while reading it. Work waits while either is on.
/// </remarks>
public sealed class SortActivity
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _resumed = NewGate(open: true);
    private bool _paused;
    private bool _held;

    /// <summary>Raised on a thread-pool thread when <see cref="IsPaused"/> or <see cref="IsHeld"/> changes.</summary>
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

    /// <summary>Whether the app is holding all work until the user has read the first-run guide.</summary>
    public bool IsHeld
    {
        get { lock (_gate) return _held; }
    }

    /// <summary>Whether work must wait, because of a pause or the hold.</summary>
    public bool IsStopped
    {
        get { lock (_gate) return _paused || _held; }
    }

    /// <summary>Pauses processing after the current file.</summary>
    public void Pause() => Update(paused: true, held: null);

    /// <summary>Resumes processing, unless the first-run hold is still on.</summary>
    public void Resume() => Update(paused: false, held: null);

    /// <summary>Holds all work until <see cref="ReleaseHold"/>. Call before the background services start.</summary>
    public void Hold() => Update(paused: null, held: true);

    /// <summary>Ends the first-run hold; work continues unless the user paused it.</summary>
    public void ReleaseHold() => Update(paused: null, held: false);

    /// <summary>Clears a pause and the hold, since the user asked for this explicitly, and asks the worker to rescan the watch folders now.</summary>
    public void RequestSortNow()
    {
        Update(paused: false, held: false);
        SortNowRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Completes immediately when running; otherwise when neither a pause nor the hold is on.</summary>
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

    private void Update(bool? paused, bool? held)
    {
        lock (_gate)
        {
            var newPaused = paused ?? _paused;
            var newHeld = held ?? _held;
            if (newPaused == _paused && newHeld == _held)
                return;

            var wasStopped = _paused || _held;
            _paused = newPaused;
            _held = newHeld;
            var isStopped = _paused || _held;
            if (isStopped && !wasStopped)
                _resumed = NewGate(open: false);
            else if (!isStopped && wasStopped)
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
