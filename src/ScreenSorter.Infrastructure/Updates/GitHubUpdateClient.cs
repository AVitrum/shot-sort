using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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
/// <param name="SignatureUrl">Where to download the signature of that checksum file.</param>
public sealed record UpdateRelease(Version Version, string SetupName, string SetupUrl, string ChecksumUrl, string SignatureUrl);

/// <summary>A release could not be read, downloaded or verified. Nothing was installed; try again later.</summary>
/// <param name="message">What failed.</param>
/// <param name="inner">The underlying error.</param>
public sealed class UpdateFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads the latest published release from GitHub and downloads its installer. Drafts and pre-releases are never returned
/// by this endpoint. The release's checksum file must carry a signature from <see cref="UpdatesOptions.PublicKey"/>,
/// whose private half is not on GitHub, so control of a release is not enough to install anything.
/// </summary>
public sealed partial class GitHubUpdateClient(HttpClient http, IOptions<UpdatesOptions> options)
{
    private readonly UpdatesOptions _options = options.Value;

    /// <summary>Asks for the latest release.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The release, or <see langword="null"/> if none is published or it has no usable installer.</returns>
    /// <exception cref="UpdateFailedException">The answer could not be read, or the release is not signed.</exception>
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

    /// <summary>Downloads the installer and checks it against the signed SHA-256. A file already there and correct is reused.</summary>
    /// <param name="release">What to download.</param>
    /// <param name="folder">Where to put it; created if needed.</param>
    /// <param name="ct">Cancels the download.</param>
    /// <returns>The path of the verified installer. An unverified file is never left under that name.</returns>
    /// <exception cref="UpdateFailedException">The download failed, the signature is not valid, or the file does not match.</exception>
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
        if (setupUrl is null || checksumUrl is null)
            return null;
        var signatureUrl = AssetUrl(release!, setupName + ".sha256.sig")
            ?? throw new UpdateFailedException($"Release {tag} is not signed");
        return new UpdateRelease(Version.Parse(version), setupName, setupUrl, checksumUrl, signatureUrl);
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
        if (string.IsNullOrWhiteSpace(_options.PublicKey))
            throw new UpdateFailedException("This copy has no key to verify updates with");

        var checksum = await ReadBytesAsync(release.ChecksumUrl, "Checksum", ct);
        var signature = await ReadBytesAsync(release.SignatureUrl, "Signature", ct);
        if (!IsSignedByOurKey(checksum, signature))
            throw new UpdateFailedException("The release's signature does not match this app's key");

        // Format of sha256sum: the hash, two spaces, the file name.
        var parts = Encoding.ASCII.GetString(checksum).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !Sha256Hex().IsMatch(parts[0]))
            throw new UpdateFailedException("The published checksum could not be read");
        // The signature vouches for one file. Without this, an old signed installer could be published as a new version.
        if (parts[1] != release.SetupName)
            throw new UpdateFailedException($"The signed checksum is for {parts[1]}, not {release.SetupName}");
        return parts[0].ToLowerInvariant();
    }

    private async Task<byte[]> ReadBytesAsync(string url, string what, CancellationToken ct)
    {
        using var request = Request(url);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new UpdateFailedException($"{what} download answered {(int)response.StatusCode}");
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private bool IsSignedByOurKey(byte[] checksum, byte[] signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(_options.PublicKey), out _);
            return key.VerifyData(checksum, Convert.FromBase64String(Encoding.ASCII.GetString(signature).Trim()), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
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
