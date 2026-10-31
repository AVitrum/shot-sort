namespace ScreenSorter.Core.Configuration;

/// <summary>LM Studio vision model settings. Bound from <c>LmStudio</c>.</summary>
public sealed class LmStudioOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "LmStudio";

    /// <summary>OpenAI-compatible base URL, e.g. <c>http://localhost:1234/v1</c>.</summary>
    public string BaseUrl { get; set; } = "http://localhost:1234/v1";

    /// <summary>Model identifier as listed by <c>/v1/models</c>.</summary>
    public string Model { get; set; } = "";

    /// <summary>Longest image side sent to the model; larger images are downscaled.</summary>
    public int MaxImageSide { get; set; } = 1280;

    /// <summary>JPEG quality (1–100) of the image sent to the model.</summary>
    public int JpegQuality { get; set; } = 90;

    /// <summary>
    /// Completion token limit. Reasoning models (Gemma 4) spend tokens on <c>reasoning_content</c> before the
    /// JSON answer, so this must cover both; too low yields an empty answer.
    /// </summary>
    public int MaxTokens { get; set; } = 1024;

    /// <summary>Per-request timeout. The first request may include loading the model.</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>Idle time after which LM Studio unloads a JIT-loaded model (<c>ttl</c> request field).</summary>
    public int TtlSeconds { get; set; } = 300;

    /// <summary>Wait before retrying after LM Studio was unreachable or returned an error.</summary>
    public int RetryMinutes { get; set; } = 5;

    /// <summary>
    /// Categories the model may answer (keys, used as the JSON schema enum) mapped to their folder
    /// under the sorted root. Unknown answers go to review.
    /// </summary>
    public Dictionary<string, string> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
