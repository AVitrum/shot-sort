using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Sorting;

namespace ScreenSorter.Infrastructure.Files;

/// <summary><see cref="IScreenshotStore"/> on the local file system.</summary>
public sealed class FileSystemScreenshotStore(IOptions<IntakeOptions> options, TimeProvider time) : IScreenshotStore
{
    private const int MaxCollisionSuffix = 1000;
    private readonly IntakeOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<bool> WaitUntilReadyAsync(string path, CancellationToken ct)
    {
        var poll = TimeSpan.FromMilliseconds(Math.Max(50, _options.StabilityPollMilliseconds));
        var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(_options.StabilityTimeoutSeconds);
        long lastSize = -1;

        while (time.GetUtcNow() < deadline)
        {
            if (!File.Exists(path))
                return false;

            var size = new FileInfo(path).Length;
            // Snipping Tool creates the file first and writes it a moment later; require one
            // unchanged, non-empty size between polls before trying an exclusive open.
            if (size > 0 && size == lastSize && CanOpenExclusively(path))
                return true;

            lastSize = size;
            await Task.Delay(poll, time, ct);
        }
        return false;
    }

    /// <inheritdoc />
    public DateTimeOffset GetCreationTime(string path) => new(File.GetCreationTimeUtc(path), TimeSpan.Zero);

    /// <inheritdoc />
    public async Task<string> ComputeHashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListSubfolders(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateDirectories(folder).Select(Path.GetFileName).OfType<string>().ToList()
            : [];

    /// <inheritdoc />
    public string Move(string source, string desiredTarget)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(desiredTarget)!);
        var target = desiredTarget;
        for (var n = 2; ; n++)
        {
            try
            {
                File.Move(source, target, overwrite: false);
                return target;
            }
            catch (IOException) when (File.Exists(target) && n <= MaxCollisionSuffix)
            {
                target = SortedPathBuilder.WithSuffix(desiredTarget, n);
            }
        }
    }

    private static bool CanOpenExclusively(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
