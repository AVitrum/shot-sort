namespace ScreenSorter.Core.Configuration;

/// <summary>CLIP image model settings. Bound from <c>Clip</c>. Changing the model or preprocessing invalidates stored examples.</summary>
public sealed class ClipOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Clip";

    /// <summary>Download URL of the ONNX vision model, pinned to one revision.</summary>
    public string ModelUrl { get; set; } = "";

    /// <summary>Expected lower-case hex SHA-256 of the model file.</summary>
    public string ModelSha256 { get; set; } = "";

    /// <summary>Where the model is stored; may contain <c>%VAR%</c> tokens.</summary>
    public string ModelPath { get; set; } = "";

    /// <summary>Wait before retrying a failed model download.</summary>
    public int DownloadRetryMinutes { get; set; } = 30;

    /// <summary><c>DirectML</c> (falls back to CPU if it cannot start) or <c>Cpu</c>.</summary>
    public string Provider { get; set; } = "DirectML";

    /// <summary>Name of the model's image input.</summary>
    public string InputName { get; set; } = "pixel_values";

    /// <summary>Name of the model's projected embedding output.</summary>
    public string OutputName { get; set; } = "image_embeds";

    /// <summary>Length of the embedding vector.</summary>
    public int EmbeddingSize { get; set; } = 512;

    /// <summary>Side of the square image the model takes.</summary>
    public int InputSize { get; set; } = 224;

    /// <summary>Per-channel (RGB) normalization mean, from the model's preprocessor config.</summary>
    public float[] ImageMean { get; set; } = [];

    /// <summary>Per-channel (RGB) normalization standard deviation, from the model's preprocessor config.</summary>
    public float[] ImageStd { get; set; } = [];

    /// <summary>Unload the model after this long without use, so a tray app does not hold memory and VRAM.</summary>
    public int UnloadAfterMinutes { get; set; } = 5;
}
