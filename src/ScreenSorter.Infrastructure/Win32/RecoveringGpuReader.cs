using Microsoft.Extensions.Logging;

namespace ScreenSorter.Infrastructure.Win32;

/// <summary>
/// Reads a GPU library that can fail for a while, such as during a driver update. A temporary failure pauses reads and
/// tries again later; a missing library, as on a PC without that vendor's driver, stops them for good. Not thread-safe.
/// </summary>
/// <param name="read">Takes one reading. Throws <see cref="InvalidOperationException"/> for a temporary failure, and <see cref="DllNotFoundException"/> or <see cref="EntryPointNotFoundException"/> for a missing library.</param>
/// <param name="reset">Releases whatever a failed read left open.</param>
/// <param name="retryAfter">How long to wait after a temporary failure.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">Receives one line per outage, not one per read.</param>
public sealed class RecoveringGpuReader(Func<int> read, Action reset, TimeSpan retryAfter, TimeProvider time, ILogger logger)
{
    private DateTimeOffset? _retryAt;
    private bool _gone;

    /// <summary>Takes a reading if the library is usable now.</summary>
    /// <returns>The load in percent, or <see langword="null"/> if there is no reading at the moment.</returns>
    public int? Read()
    {
        if (_gone || (_retryAt is { } at && time.GetUtcNow() < at))
            return null;
        try
        {
            var value = read();
            if (_retryAt is not null)
                logger.LogInformation("GPU library works again");
            _retryAt = null;
            return value;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _gone = true;
            logger.LogInformation("GPU library is not on this PC: {Problem}", ex.Message);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            if (_retryAt is null)
                logger.LogWarning("GPU library failed; trying again every {Minutes} min: {Problem}", retryAfter.TotalMinutes, ex.Message);
            reset();
            _retryAt = time.GetUtcNow() + retryAfter;
            return null;
        }
    }
}
