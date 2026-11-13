using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Rules;
using ScreenSorter.Core.Vision;

namespace ScreenSorter.Infrastructure.Vision;

/// <summary>
/// <see cref="IVisionClassifier"/> using LM Studio's OpenAI-compatible <c>/chat/completions</c> with an
/// image content part (base64 JPEG data URL) and <c>response_format.json_schema</c> structured output.
/// <see cref="ReleaseModelAsync"/> ejects the model after a batch through LM Studio's own REST API; the <c>ttl</c>
/// field is the safety net if that fails. Not thread-safe: one caller at a time, as <see cref="VisionWorker"/> does.
/// </summary>
public sealed class LmStudioVisionClient(
    HttpClient http,
    IImageInspector images,
    IGameCatalog games,
    IOptions<LmStudioOptions> options,
    ILogger<LmStudioVisionClient> logger) : IVisionClassifier
{
    private const string SystemPrompt =
        "You classify Windows screenshots by the application or content they show. Reply only with the requested JSON.";

    private readonly LmStudioOptions _options = options.Value;
    private bool _ownershipKnown;
    private bool _weLoadedModel;

    /// <inheritdoc />
    public async Task<VisionResult?> ClassifyAsync(string imagePath, FocusSnapshot? hint, CancellationToken ct)
    {
        // Settings refuse such an address too; this also covers a settings file edited by hand.
        if (LmStudioAddress.Problem(_options.BaseUrl, _options.AllowRemote) is { } problem)
            throw new VisionUnavailableException(problem);

        // Asked before this request can load the model; a failure leaves it unknown, so the next request asks again.
        if (_options.EjectAfterBatch && !_ownershipKnown && await TryListInstancesAsync(ct) is { } loaded)
        {
            _weLoadedModel = loaded.Count == 0;
            _ownershipKnown = true;
        }

        var jpeg = images.ToJpeg(imagePath, _options.MaxImageSide, _options.JpegQuality);
        var request = BuildRequest(Convert.ToBase64String(jpeg), hint, games.GetTitles());

        var body = await SendAsync(request, ct);
        // A reasoning model can spend the whole limit before it answers. Most images need far less, so only those that
        // ran out pay for the larger limit.
        if (_options.RetryMaxTokens > _options.MaxTokens && RanOutOfTokens(body))
        {
            logger.LogInformation("Asking again with {Tokens} tokens: the answer did not fit in {Limit}", _options.RetryMaxTokens, _options.MaxTokens);
            request["max_tokens"] = _options.RetryMaxTokens;
            body = await SendAsync(request, ct);
        }
        return Parse(body);
    }

    private async Task<string> SendAsync(JsonObject request, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            using var response = await http.PostAsJsonAsync(Endpoint(), request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new VisionUnavailableException($"LM Studio returned {(int)response.StatusCode}: {Truncate(body)}");
            return body;
        }
        catch (HttpRequestException ex)
        {
            throw new VisionUnavailableException($"LM Studio unreachable at {_options.BaseUrl}", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new VisionUnavailableException($"LM Studio did not answer within {_options.TimeoutSeconds} s", ex);
        }
    }

    private static bool RanOutOfTokens(string body)
    {
        try
        {
            var choice = JsonNode.Parse(body)?["choices"]?[0];
            return choice?["finish_reason"]?.GetValue<string>() == "length"
                && string.IsNullOrWhiteSpace(choice["message"]?["content"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ReleaseModelAsync(CancellationToken ct)
    {
        var ours = _ownershipKnown && _weLoadedModel;
        _ownershipKnown = false;
        if (!ours)
            return;

        // Failures only delay the unload: the request's ttl still frees the model.
        var instances = await TryListInstancesAsync(ct);
        if (instances is null)
            return;
        foreach (var id in instances)
        {
            try
            {
                using var timeout = ManagementTimeout(ct);
                using var response = await http.PostAsJsonAsync(ManagementEndpoint("/models/unload"),
                    new JsonObject { ["instance_id"] = id }, timeout.Token);
                if (response.IsSuccessStatusCode)
                    logger.LogInformation("Ejected {Instance} from LM Studio", id);
                else
                    logger.LogWarning("Could not eject {Instance}: LM Studio returned {Status}; it unloads after {Ttl} s", id, (int)response.StatusCode, _options.TtlSeconds);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                logger.LogWarning(ex, "Could not eject {Instance}; it unloads after {Ttl} s", id, _options.TtlSeconds);
            }
        }
    }

    /// <summary>Builds the chat-completions request body.</summary>
    /// <param name="imageBase64">Base64 JPEG.</param>
    /// <param name="hint">Focused window, if known.</param>
    /// <param name="knownGames">
    /// Valid game titles. When non-empty, <c>game_title</c> is restricted to these or "";
    /// when empty it is free text.
    /// </param>
    /// <returns>The JSON body.</returns>
    internal JsonObject BuildRequest(string imageBase64, FocusSnapshot? hint, IReadOnlyList<string> knownGames)
    {
        var categories = _options.Categories.Keys.ToList();
        var gameTitle = new JsonObject { ["type"] = "string" };
        if (knownGames.Count > 0)
            gameTitle["enum"] = new JsonArray(knownGames.Append("").Select(t => (JsonNode)t).ToArray());

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["category"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(categories.Select(c => (JsonNode)c).ToArray()) },
                ["game_title"] = gameTitle,
                ["app_name"] = new JsonObject { ["type"] = "string" },
                ["confidence"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 },
            },
            ["required"] = new JsonArray("category", "game_title", "app_name", "confidence"),
            ["additionalProperties"] = false,
        };

        return new JsonObject
        {
            ["model"] = _options.Model,
            ["temperature"] = 0,
            ["max_tokens"] = _options.MaxTokens,
            ["stream"] = false,
            ["ttl"] = _options.TtlSeconds,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = UserPrompt(categories, hint, knownGames) },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + imageBase64 },
                        }),
                }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "screenshot_classification",
                    ["strict"] = true,
                    ["schema"] = schema,
                },
            },
        };
    }

    /// <summary>Parses a chat-completions response body.</summary>
    /// <param name="body">Raw response JSON.</param>
    /// <returns>The result, or <see langword="null"/> if the content is not the expected JSON.</returns>
    internal VisionResult? Parse(string body)
    {
        try
        {
            var choice = JsonNode.Parse(body)?["choices"]?[0];
            var content = choice?["message"]?["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(content))
            {
                // finish_reason "length" means reasoning used up LmStudio:MaxTokens before the answer.
                logger.LogWarning("Empty vision answer (finish_reason {Reason})", choice?["finish_reason"]?.ToString() ?? "?");
                return null;
            }

            var answer = JsonNode.Parse(StripCodeFence(content));
            var category = answer?["category"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(category))
                return null;

            return new VisionResult(
                category.Trim(),
                NullIfEmpty(answer?["game_title"]),
                NullIfEmpty(answer?["app_name"]),
                Math.Clamp(ReadNumber(answer?["confidence"]), 0, 1));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            logger.LogWarning(ex, "Unparseable vision answer: {Body}", Truncate(body));
            return null;
        }
    }

    private Uri Endpoint() => new(_options.BaseUrl.TrimEnd('/') + "/chat/completions");

    private Uri ManagementEndpoint(string path) => new(_options.ManagementUrl.TrimEnd('/') + path);

    private CancellationTokenSource ManagementTimeout(CancellationToken ct)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.ManagementTimeoutSeconds));
        return timeout;
    }

    // Returns the configured model's loaded instance ids, or null if LM Studio could not be asked.
    private async Task<IReadOnlyList<string>?> TryListInstancesAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = ManagementTimeout(ct);
            using var response = await http.GetAsync(ManagementEndpoint("/models"), timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("LM Studio model list returned {Status}", (int)response.StatusCode);
                return null;
            }

            var models = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token))?["models"]?.AsArray();
            if (models is null)
                return null;
            var model = models.FirstOrDefault(m => m?["key"]?.GetValue<string>() == _options.Model);
            return model?["loaded_instances"]?.AsArray().Select(i => i!["id"]!.GetValue<string>()).ToList() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogDebug(ex, "Could not list LM Studio models");
            return null;
        }
    }

    private static string UserPrompt(IReadOnlyList<string> categories, FocusSnapshot? hint, IReadOnlyList<string> knownGames)
    {
        var gameRule = knownGames.Count > 0
            ? "Set game_title to the matching game from this list of the user's games, or an empty string if the game is " +
              "not in the list or you are not sure which one it is: " + string.Join("; ", knownGames) + ".\n"
            : "Set game_title to the game's official title.\n";
        var text =
            "Classify this screenshot into exactly one category: " + string.Join(", ", categories) + ".\n" +
            "- Game: a video game (gameplay, menus, launchers in-game). " + gameRule +
            "- Chat: a messaging app such as Discord, Telegram, WhatsApp or Slack. Set app_name to the app.\n" +
            "- Calls: a video or voice call (Zoom, Google Meet, Teams).\n" +
            "- Code: code editor, IDE or terminal. Streaming: YouTube, Twitch, Netflix or a video player.\n" +
            "- Documents: Word, Excel, PowerPoint or PDF. Browser: any other website. Desktop: Windows desktop, File Explorer or Settings.\n" +
            "- Other: anything else.\n" +
            "Use an empty string for game_title and app_name when they do not apply or are unknown. " +
            "confidence is your certainty from 0 to 1.";
        if (hint is not null && hint.ProcessName.Length > 0)
        {
            text += $"\nHint (may be wrong, the screenshot can show another window): the focused app was " +
                    $"\"{hint.ProcessName}\" with window title \"{hint.WindowTitle}\".";
        }
        return text;
    }

    // Some models wrap JSON in ```json fences even with structured output.
    private static string StripCodeFence(string content)
    {
        var s = content.Trim();
        if (!s.StartsWith("```", StringComparison.Ordinal))
            return s;
        var start = s.IndexOf('\n');
        var end = s.LastIndexOf("```", StringComparison.Ordinal);
        return start >= 0 && end > start ? s[(start + 1)..end] : s;
    }

    private static string? NullIfEmpty(JsonNode? node)
    {
        var s = node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>().Trim() : null;
        return string.IsNullOrEmpty(s) || s.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    private static double ReadNumber(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.Number => node.GetValue<double>(),
        JsonValueKind.String => double.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture),
        _ => 0,
    };

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500] + "…";
}
