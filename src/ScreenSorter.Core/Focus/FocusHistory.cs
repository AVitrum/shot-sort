using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Models;

namespace ScreenSorter.Core.Focus;

/// <summary>
/// Thread-safe ring buffer of focus changes. A native tracker calls <see cref="Record"/>;
/// the pipeline calls <see cref="GetFocusAt"/>.
/// </summary>
public sealed class FocusHistory : IFocusHistory
{
    private readonly Lock _gate = new();
    private readonly FocusSnapshot[] _buffer;
    private readonly HashSet<string> _ignored;
    private int _next;
    private int _count;

    /// <summary>Creates an empty history.</summary>
    /// <param name="options">Buffer size and ignored processes.</param>
    /// <param name="time">Clock; <see cref="TrackingSince"/> is set from it.</param>
    public FocusHistory(IOptions<FocusOptions> options, TimeProvider time)
    {
        _buffer = new FocusSnapshot[Math.Max(8, options.Value.HistorySize)];
        _ignored = new HashSet<string>(options.Value.IgnoredProcesses, StringComparer.OrdinalIgnoreCase);
        TrackingSince = time.GetUtcNow();
    }

    /// <summary>Start of the covered time range. Files captured earlier get no focus answer.</summary>
    public DateTimeOffset TrackingSince { get; }

    /// <summary>Adds a snapshot. Snapshots must arrive in time order.</summary>
    /// <param name="snapshot">The foreground window and when it was observed.</param>
    public void Record(FocusSnapshot snapshot)
    {
        lock (_gate)
        {
            _buffer[_next] = snapshot;
            _next = (_next + 1) % _buffer.Length;
            _count = Math.Min(_count + 1, _buffer.Length);
        }
    }

    /// <inheritdoc />
    public FocusSnapshot? GetFocusAt(DateTimeOffset time)
    {
        if (time < TrackingSince)
            return null;

        lock (_gate)
        {
            for (var i = 1; i <= _count; i++)
            {
                var s = _buffer[(_next - i + _buffer.Length) % _buffer.Length];
                if (s.Timestamp <= time && !_ignored.Contains(s.ProcessName))
                    return s;
            }
        }
        return null;
    }
}
