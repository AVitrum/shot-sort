using ScreenSorter.Core.Models;

namespace ScreenSorter.Core.Vision;

/// <summary>A screenshot waiting for the vision model.</summary>
/// <param name="Path">File at the top level of a watch folder; it stays there while queued.</param>
/// <param name="Focus">Focused window at capture time, passed to the model as a hint.</param>
public sealed record VisionWorkItem(string Path, FocusSnapshot? Focus);

/// <summary>
/// FIFO of files waiting for the vision model. Thread-safe. Items can be put back at the front
/// when the model is unavailable, so order is kept across retries.
/// </summary>
public sealed class VisionQueue
{
    private readonly Lock _gate = new();
    private readonly LinkedList<VisionWorkItem> _items = new();
    private readonly SemaphoreSlim _available = new(0);

    /// <summary>
    /// Raised by <see cref="MarkCompleted"/> when an item left the queue for good (sorted, reviewed or gone),
    /// so intake can forget the path.
    /// </summary>
    public event EventHandler<VisionWorkItem>? ItemCompleted;

    /// <summary>Number of queued items.</summary>
    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    /// <summary>Adds an item at the back.</summary>
    /// <param name="item">The file to classify.</param>
    public void Enqueue(VisionWorkItem item)
    {
        lock (_gate)
            _items.AddLast(item);
        _available.Release();
    }

    /// <summary>Puts an item back at the front, e.g. after a failed request.</summary>
    /// <param name="item">The item taken by <see cref="DequeueAsync"/>.</param>
    public void Requeue(VisionWorkItem item)
    {
        lock (_gate)
            _items.AddFirst(item);
        _available.Release();
    }

    /// <summary>Waits for and removes the front item.</summary>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The oldest item.</returns>
    public async Task<VisionWorkItem> DequeueAsync(CancellationToken ct)
    {
        await _available.WaitAsync(ct);
        lock (_gate)
        {
            var item = _items.First!.Value;
            _items.RemoveFirst();
            return item;
        }
    }

    /// <summary>Signals that a dequeued item is finished and will not be requeued.</summary>
    /// <param name="item">The finished item.</param>
    public void MarkCompleted(VisionWorkItem item) => ItemCompleted?.Invoke(this, item);

    /// <summary>Waits until at least one item is queued, without removing it.</summary>
    /// <param name="ct">Cancels the wait.</param>
    public async Task WaitForItemAsync(CancellationToken ct)
    {
        await _available.WaitAsync(ct);
        _available.Release();
    }
}
