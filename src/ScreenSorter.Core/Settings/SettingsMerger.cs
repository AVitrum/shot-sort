using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenSorter.Core.Settings;

/// <summary>
/// Layers the user's settings file over <c>appsettings.json</c>. Objects merge key by key; arrays and scalars replace.
/// Plain configuration layering merges arrays by index, so a shorter list would keep the tail of the base list and
/// a rule the user deleted would come back.
/// </summary>
public static class SettingsMerger
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Objects used as dictionaries: an entry the user removed must stay removed.
    private static readonly HashSet<string> ReplacedWhole = new(StringComparer.OrdinalIgnoreCase)
    {
        "Rules.GameAliases",
    };

    /// <summary>Merges two JSON documents.</summary>
    /// <param name="baseJson">Contents of <c>appsettings.json</c>.</param>
    /// <param name="userJson">Contents of the user's settings file, or <see langword="null"/> if there is none.</param>
    /// <returns>The merged JSON.</returns>
    /// <exception cref="JsonException">Either document is not valid JSON.</exception>
    public static string Merge(string baseJson, string? userJson)
    {
        var merged = JsonNode.Parse(baseJson, documentOptions: ReadOptions)
            ?? throw new JsonException("The base settings are empty");
        if (!string.IsNullOrWhiteSpace(userJson))
        {
            var user = JsonNode.Parse(userJson, documentOptions: ReadOptions);
            if (user is not JsonObject userObject || merged is not JsonObject baseObject)
                throw new JsonException("Settings must be a JSON object");
            MergeInto(baseObject, userObject, "");
        }
        return merged.ToJsonString();
    }

    private static void MergeInto(JsonObject target, JsonObject source, string path)
    {
        foreach (var (key, value) in source.ToList())
        {
            var childPath = path.Length == 0 ? key : $"{path}.{key}";
            if (value is JsonObject sourceChild && target[key] is JsonObject targetChild && !ReplacedWhole.Contains(childPath))
                MergeInto(targetChild, sourceChild, childPath);
            else
                target[key] = value?.DeepClone();
        }
    }
}
