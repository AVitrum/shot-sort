namespace ScreenSorter.Core.Configuration;

/// <summary>LM Studio vision model settings. Bound from <c>LmStudio</c>.</summary>
public sealed class LmStudioOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "LmStudio";

    /// <summary>
    /// Whether a vision model is used at all. When off, nothing needs LM Studio or the GPU's memory: files the rules and
    /// examples cannot place go straight to the review folder instead of waiting for the model.
    /// </summary>
    public bool Enabled { get; set; } = true;

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

    /// <summary>
    /// Token limit for asking once more when the model ran out of tokens before answering. At or below
    /// <see cref="MaxTokens"/> there is no second request, which is the default: these are the images the model finds
    /// hard, and on real screenshots only 9 of 15 late answers matched the user's folder, so review is the safer place.
    /// Above 2048 almost nothing more was answered, at about a minute per image.
    /// </summary>
    public int RetryMaxTokens { get; set; }

    /// <summary>Per-request timeout. The first request may include loading the model.</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>LM Studio's own REST API, used to list and unload models, e.g. <c>http://localhost:1234/api/v1</c>.</summary>
    public string ManagementUrl { get; set; } = "http://localhost:1234/api/v1";

    /// <summary>Unload the model as soon as a batch ends, instead of waiting for <see cref="TtlSeconds"/>.</summary>
    public bool EjectAfterBatch { get; set; } = true;

    /// <summary>Timeout for list and unload requests.</summary>
    public int ManagementTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Idle time after which LM Studio unloads a JIT-loaded model (<c>ttl</c> request field). A safety net for
    /// when <see cref="EjectAfterBatch"/> is off or its request fails.
    /// </summary>
    public int TtlSeconds { get; set; } = 300;

    /// <summary>Wait before retrying after LM Studio was unreachable or returned an error.</summary>
    public int RetryMinutes { get; set; } = 5;

    /// <summary>Start LM Studio's server when files need it and the PC is idle, if it is on this machine and <see cref="CliPath"/> exists.</summary>
    public bool AutoStartServer { get; set; } = true;

    /// <summary>LM Studio's command line tool. Environment variables are expanded.</summary>
    public string CliPath { get; set; } = @"%USERPROFILE%\.lmstudio\bin\lms.exe";

    /// <summary>Longest wait for the tool to finish starting the server.</summary>
    public int StartTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Categories the model may answer (keys, used as the JSON schema enum) mapped to their folder
    /// under the sorted root. Unknown answers go to review.
    /// </summary>
    public Dictionary<string, string> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
