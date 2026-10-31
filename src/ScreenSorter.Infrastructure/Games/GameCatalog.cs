using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Rules;
using ScreenSorter.Core.Sorting;

namespace ScreenSorter.Infrastructure.Games;

/// <summary>
/// <see cref="IGameCatalog"/> from, in priority order: existing <c>Games/*</c> folders (the user's
/// spelling), game alias values, <see cref="GameCatalogOptions.ExtraTitles"/>, installed Steam games
/// (all libraries), installed Epic games. Duplicates by <see cref="SortedPathBuilder.MatchKey"/> keep the
/// first spelling. Launcher scans are cached for <see cref="GameCatalogOptions.RefreshMinutes"/>. Thread-safe.
/// </summary>
public sealed class GameCatalog(
    IOptions<GameCatalogOptions> options,
    IOptions<PathsOptions> paths,
    IOptions<RulesOptions> rules,
    TimeProvider time,
    ILogger<GameCatalog> logger) : IGameCatalog
{
    private const string SteamRegistryKey = @"Software\Valve\Steam";
    private readonly Lock _gate = new();
    private IReadOnlyList<string> _launcherTitles = [];
    private DateTimeOffset _scannedAt = DateTimeOffset.MinValue;

    /// <inheritdoc />
    public IReadOnlyList<string> GetTitles()
    {
        var o = options.Value;
        var gamesFolder = Path.Combine(PathsOptions.Expand(paths.Value.SortedRoot), RulesEngine.GamesCategory);
        var folders = Directory.Exists(gamesFolder)
            ? Directory.EnumerateDirectories(gamesFolder).Select(Path.GetFileName).OfType<string>()
            : [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in folders.Concat(rules.Value.GameAliases.Values).Concat(o.ExtraTitles).Concat(LauncherTitles()))
        {
            var title = Clean(raw);
            var key = SortedPathBuilder.MatchKey(title);
            if (key.Length == 0
                || o.ExcludeTitles.Any(x => title.StartsWith(x, StringComparison.OrdinalIgnoreCase))
                || !seen.Add(key))
                continue;
            result.Add(title);
        }
        return result;
    }

    private IReadOnlyList<string> LauncherTitles()
    {
        lock (_gate)
        {
            if (time.GetUtcNow() - _scannedAt < TimeSpan.FromMinutes(Math.Max(0, options.Value.RefreshMinutes)))
                return _launcherTitles;

            _launcherTitles = [.. SteamTitles(), .. EpicTitles()];
            _scannedAt = time.GetUtcNow();
            logger.LogDebug("Game catalog scan: {Count} launcher titles", _launcherTitles.Count);
            return _launcherTitles;
        }
    }

    private List<string> SteamTitles()
    {
        var root = options.Value.SteamPath;
        if (string.IsNullOrWhiteSpace(root))
            root = Registry.CurrentUser.OpenSubKey(SteamRegistryKey)?.GetValue("SteamPath") as string ?? "";
        if (string.IsNullOrWhiteSpace(root))
            return [];

        try
        {
            return SteamFiles.SteamAppsFolders(PathsOptions.Expand(root))
                .SelectMany(SteamFiles.Manifests)
                .Select(m => m.Name)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Steam library scan failed");
            return [];
        }
    }

    private List<string> EpicTitles()
    {
        var folder = PathsOptions.Expand(options.Value.EpicManifestsFolder);
        if (folder.Length == 0 || !Directory.Exists(folder))
            return [];

        var titles = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("DisplayName", out var name) && name.ValueKind == JsonValueKind.String)
                    titles.Add(name.GetString()!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                logger.LogDebug(ex, "Skipped Epic manifest {File}", file);
            }
        }
        return titles;
    }

    private static string Clean(string title) =>
        string.Join(' ', title.Replace("™", "").Replace("®", "").Replace("©", "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
