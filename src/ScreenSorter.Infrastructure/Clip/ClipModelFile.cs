using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;

namespace ScreenSorter.Infrastructure.Clip;

/// <summary>
/// Makes sure the CLIP model is on disk and intact: on start it checks the file's SHA-256 and, if the file is
/// missing or wrong, downloads it from <see cref="ClipOptions.ModelUrl"/> in the background and retries on failure.
/// Sorting never waits for this; the example stage stays off until <see cref="ReadyPath"/> is set.
/// </summary>
public sealed class ClipModelFile(HttpClient http, IOptions<ClipOptions> options, ILogger<ClipModelFile> logger) : BackgroundService
{
    private readonly ClipOptions _options = options.Value;
    private volatile string? _readyPath;

    /// <summary>Path of the verified model, or <see langword="null"/> until it is available.</summary>
    public string? ReadyPath => _readyPath;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = PathsOptions.Expand(_options.ModelPath);
        if (path.Length == 0 || _options.ModelUrl.Length == 0)
        {
            logger.LogWarning("Clip:ModelPath or Clip:ModelUrl is not set; the example stage stays off");
            return;
        }

        try
        {
            while (true)
            {
                try
                {
                    await EnsureAsync(path, stoppingToken);
                    _readyPath = path;
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    logger.LogWarning(ex, "CLIP model not available; retrying in {Minutes} min", _options.DownloadRetryMinutes);
                }
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _options.DownloadRetryMinutes)), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Verifies the file, downloading it first if needed.</summary>
    /// <param name="path">Target path.</param>
    /// <param name="ct">Cancels the download.</param>
    /// <exception cref="HttpRequestException">The download failed.</exception>
    /// <exception cref="InvalidDataException">The downloaded file does not match <see cref="ClipOptions.ModelSha256"/>.</exception>
    internal async Task EnsureAsync(string path, CancellationToken ct)
    {
        if (File.Exists(path) && await MatchesHashAsync(path, ct))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        logger.LogInformation("Downloading CLIP model from {Url}", _options.ModelUrl);
        using (var response = await http.GetAsync(_options.ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await response.Content.CopyToAsync(target, ct);
        }

        if (!await MatchesHashAsync(part, ct))
        {
            File.Delete(part);
            throw new InvalidDataException($"Downloaded model does not match SHA-256 {_options.ModelSha256}");
        }
        File.Move(part, path, overwrite: true);
        logger.LogInformation("CLIP model saved to {Path}", path);
    }

    private async Task<bool> MatchesHashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        var matches = string.Equals(hash, _options.ModelSha256, StringComparison.OrdinalIgnoreCase);
        if (!matches)
            logger.LogWarning("{Path} has SHA-256 {Hash}, expected {Expected}", path, hash, _options.ModelSha256);
        return matches;
    }
}
