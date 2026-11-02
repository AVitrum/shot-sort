using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;

namespace ScreenSorter.Infrastructure.Updates;

/// <summary>A published version and where to get it.</summary>
/// <param name="Version">The version, three parts.</param>
/// <param name="SetupName">File name of the installer.</param>
/// <param name="SetupUrl">Where to download the installer.</param>
/// <param name="ChecksumUrl">Where to download its SHA-256.</param>
public sealed record UpdateRelease(Version Version, string SetupName, string SetupUrl, string ChecksumUrl);

/// <summary>A release could not be read, downloaded or verified. Nothing was installed; try again later.</summary>
/// <param name="message">What failed.</param>
/// <param name="inner">The underlying error.</param>
public sealed class UpdateFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Reads the latest published release from GitHub and downloads its installer. Drafts and pre-releases are never returned by this endpoint.</summary>
public sealed partial class GitHubUpdateClient(HttpClient http, IOptions<UpdatesOptions> options)
{
    private readonly UpdatesOptions _options = options.Value;

    /// <summary>Asks for the latest release.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The release, or <see langword="null"/> if none is published or it has no usable installer.</returns>
    /// <exception cref="UpdateFailedException">The answer could not be read.</exception>
    public async Task<UpdateRelease?> GetLatestAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        try
        {
            using var request = Request($"{_options.ApiUrl.TrimEnd('/')}/repos/{_options.Repository}/releases/latest");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new UpdateFailedException($"GitHub answered {(int)response.StatusCode}");
            return Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateFailedException($"GitHub could not be reached: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UpdateFailedException($"GitHub did not answer within {_options.TimeoutSeconds} s", ex);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new UpdateFailedException("GitHub's answer could not be read", ex);
        }
    }

    /// <summary>Downloads the installer and checks it against the published SHA-256. A file already there and correct is reused.</summary>
    /// <param name="release">What to download.</param>
    /// <param name="folder">Where to put it; created if needed.</param>
    /// <param name="ct">Cancels the download.</param>
    /// <returns>The path of the verified installer. An unverified file is never left under that name.</returns>
    /// <exception cref="UpdateFailedException">The download failed or the file does not match.</exception>
    public async Task<string> DownloadAsync(UpdateRelease release, string folder, CancellationToken ct)
    {
        var path = Path.Combine(folder, release.SetupName);
        var part = path + ".part";
        try
        {
            Directory.CreateDirectory(folder);
            var expected = await ReadChecksumAsync(release, ct);
            if (File.Exists(path) && await HashAsync(path, ct) == expected)
                return path;

            using var request = Request(release.SetupUrl);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new UpdateFailedException($"Download answered {(int)response.StatusCode}");
            await using (var target = File.Create(part))
                await response.Content.CopyToAsync(target, ct);

            var actual = await HashAsync(part, ct);
            if (actual != expected)
                throw new UpdateFailedException($"{release.SetupName} has SHA-256 {actual}, expected {expected}");
            File.Move(part, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateFailedException($"Downloading {release.SetupName} failed: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(part);
        }
    }

    private UpdateRelease? Parse(string body)
    {
        var release = JsonNode.Parse(body);
        var tag = release?["tag_name"]?.GetValue<string>() ?? "";
        var match = VersionTag().Match(tag);
        if (!match.Success)
            return null;

        var version = match.Groups[1].Value;
        var setupName = $"ScreenSorter-{version}-setup.exe";
        var setupUrl = AssetUrl(release!, setupName);
        var checksumUrl = AssetUrl(release!, setupName + ".sha256");
        return setupUrl is null || checksumUrl is null ? null : new UpdateRelease(Version.Parse(version), setupName, setupUrl, checksumUrl);
    }

    private string? AssetUrl(JsonNode release, string name)
    {
        var url = release["assets"]?.AsArray()
            .FirstOrDefault(a => a?["name"]?.GetValue<string>() == name)?["browser_download_url"]?.GetValue<string>();
        return url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || (uri.IsLoopback && IsLocalTestServer))
            ? url
            : null;
    }

    private bool IsLocalTestServer => Uri.TryCreate(_options.ApiUrl, UriKind.Absolute, out var api) && api.IsLoopback;

    private async Task<string> ReadChecksumAsync(UpdateRelease release, CancellationToken ct)
    {
        using var request = Request(release.ChecksumUrl);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new UpdateFailedException($"Checksum download answered {(int)response.StatusCode}");
        // Format of sha256sum: the hash, two spaces, the file name.
        var hash = (await response.Content.ReadAsStringAsync(ct)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return Sha256Hex().IsMatch(hash) ? hash.ToLowerInvariant() : throw new UpdateFailedException("The published checksum could not be read");
    }

    private static HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        // GitHub refuses requests without a user agent.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ScreenSorter", typeof(GitHubUpdateClient).Assembly.GetName().Version?.ToString(3)));
        return request;
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Overwritten by the next attempt.
        }
    }

    [GeneratedRegex(@"^v(\d+\.\d+\.\d+)$")]
    private static partial Regex VersionTag();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Hex();
}
