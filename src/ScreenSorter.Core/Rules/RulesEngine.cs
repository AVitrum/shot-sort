using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Models;

namespace ScreenSorter.Core.Rules;

/// <summary>Instant, model-free classification from the focused window.</summary>
public sealed class RulesEngine(IOptions<RulesOptions> options, GameDetector games)
{
    /// <summary>Category folder for games.</summary>
    public const string GamesCategory = "Games";

    private readonly RulesOptions _options = options.Value;

    /// <summary>Classifies a screenshot by the app that was in focus when it was taken.</summary>
    /// <param name="focus">The focused window at capture time.</param>
    /// <returns>A <see cref="DecisionStage.Rule"/> classification, or <see langword="null"/> when no rule matches.</returns>
    public Classification? Classify(FocusSnapshot focus)
    {
        var game = games.TryDetect(focus);
        if (game is not null)
            return new Classification(GamesCategory, game, game, 1.0, DecisionStage.Rule);

        foreach (var rule in _options.AppRules)
        {
            if (!MatchesProcess(rule, focus.ProcessName) || !MatchesTitle(rule, focus.WindowTitle))
                continue;

            var parts = rule.Target.Split(['/', '\\'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
                continue;
            var sub = parts.Length > 1 ? parts[1] : null;
            var name = string.IsNullOrWhiteSpace(rule.Name) ? sub ?? parts[0] : rule.Name;
            return new Classification(parts[0], sub, name, 1.0, DecisionStage.Rule);
        }
        return null;
    }

    private bool MatchesProcess(AppRule rule, string process) =>
        rule.Processes.Any(p => p == RulesOptions.BrowserToken
            ? _options.BrowserProcesses.Contains(process, StringComparer.OrdinalIgnoreCase)
            : string.Equals(p, process, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesTitle(AppRule rule, string title) =>
        rule.TitleContains.Count == 0
        || rule.TitleContains.Any(t => title.Contains(t, StringComparison.OrdinalIgnoreCase));
}
