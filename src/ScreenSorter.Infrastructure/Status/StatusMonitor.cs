using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Status;

namespace ScreenSorter.Infrastructure.Status;

/// <summary>
/// Runs the status checks at start and every <see cref="StatusOptions.CheckSeconds"/>, and keeps the latest results.
/// Thread-safe. Events are raised on a background thread.
/// </summary>
public sealed class StatusMonitor(IEnumerable<IServiceProbe> probes, IOptions<StatusOptions> options, ILogger<StatusMonitor> logger) : BackgroundService
{
    private readonly IServiceProbe[] _probes = probes.ToArray();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly HashSet<string> _alerted = [];
    private volatile IReadOnlyList<ServiceCheck> _latest = [];

    /// <summary>The latest results, in display order; empty before the first check finishes.</summary>
    public IReadOnlyList<ServiceCheck> Latest => _latest;

    /// <summary>Raised after every check that changed a result.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised once when a check starts needing attention, and again only after it has recovered.</summary>
    public event EventHandler<ServiceCheck>? AttentionNeeded;

    /// <summary>Runs all checks now.</summary>
    /// <param name="ct">Cancels the checks.</param>
    public async Task RefreshAsync(CancellationToken ct)
    {
        await _oneAtATime.WaitAsync(ct);
        try
        {
            var results = await Task.WhenAll(_probes.Select(p => RunAsync(p, ct)));
            var changed = !results.SequenceEqual(_latest);
            _latest = results;
            if (changed)
                Changed?.Invoke(this, EventArgs.Empty);

            foreach (var check in results)
            {
                if (!check.NeedsAttention)
                    _alerted.Remove(check.Name);
                else if (_alerted.Add(check.Name))
                    AttentionNeeded?.Invoke(this, check);
            }
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.Value.CheckSeconds)));
            do
            {
                await RefreshAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<ServiceCheck> RunAsync(IServiceProbe probe, CancellationToken ct)
    {
        try
        {
            return await probe.CheckAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A broken check must not stop the others, or the host.
            logger.LogWarning(ex, "Status check {Probe} failed", probe.GetType().Name);
            return new ServiceCheck(probe.GetType().Name.Replace("Probe", ""), ServiceState.Problem, "The check itself failed. See the log.");
        }
    }
}
