using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Vision;

namespace ScreenSorter.Infrastructure.Vision;

/// <summary>Starts LM Studio's server with its command line tool, <c>lms server start</c>, on the port of <see cref="LmStudioOptions.BaseUrl"/>.</summary>
public sealed class LmsServerStarter(IOptions<LmStudioOptions> options, ILogger<LmsServerStarter> logger) : IVisionServerStarter
{
    private readonly LmStudioOptions _options = options.Value;
    private volatile bool _lastStartFailed;

    /// <inheritdoc />
    public bool CanStart => _options.AutoStartServer && LocalUrl is not null && File.Exists(Tool);

    /// <inheritdoc />
    public bool LastStartFailed => _lastStartFailed;

    private string Tool => PathsOptions.Expand(_options.CliPath);

    private Uri? LocalUrl => Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var url) && url.IsLoopback ? url : null;

    /// <inheritdoc />
    public async Task<bool> TryStartAsync(CancellationToken ct)
    {
        // Something already listening means the failure was not a stopped server, and a restart could only disturb it.
        if (!CanStart || await IsListeningAsync(LocalUrl!.Port, ct))
            return false;

        // Output is not redirected: the server outlives this call, and it would inherit the pipes and keep the wait below open.
        var start = new ProcessStartInfo(Tool) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "server", "start", "--port", LocalUrl!.Port.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.StartTimeoutSeconds));
        try
        {
            using var process = Process.Start(start)!;
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            _lastStartFailed = process.ExitCode != 0;
            if (_lastStartFailed)
                logger.LogWarning("Starting the LM Studio server failed: exit code {Code}", process.ExitCode);
            else
                logger.LogInformation("Started the LM Studio server");
            return !_lastStartFailed;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _lastStartFailed = true;
            logger.LogWarning("Starting the LM Studio server did not finish within {Seconds} s", _options.StartTimeoutSeconds);
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _lastStartFailed = true;
            logger.LogWarning(ex, "Could not run {Tool}", Tool);
            return false;
        }
    }

    private static async Task<bool> IsListeningAsync(int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
