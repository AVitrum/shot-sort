using ScreenSorter.Core.Models;

namespace ScreenSorter.Core.Vision;

/// <summary>Classifies a screenshot by its image content.</summary>
public interface IVisionClassifier
{
    /// <summary>Asks the model what the screenshot shows.</summary>
    /// <param name="imagePath">The screenshot file.</param>
    /// <param name="hint">The focused window at capture time, if known; passed to the model as a hint that may be wrong.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The model's answer, or <see langword="null"/> if it answered but the answer was unusable.</returns>
    /// <exception cref="VisionUnavailableException">The model could not be reached or failed; retry later.</exception>
    Task<VisionResult?> ClassifyAsync(string imagePath, FocusSnapshot? hint, CancellationToken ct);

    /// <summary>
    /// Frees the model's memory after a batch, if <see cref="ClassifyAsync"/> caused it to load. A model that was
    /// already loaded before the batch is left alone. Does nothing if no classification happened since the last
    /// release. Best effort: failures are logged, never thrown.
    /// </summary>
    /// <param name="ct">Cancels the request.</param>
    Task ReleaseModelAsync(CancellationToken ct);
}

/// <summary>A parsed vision-model answer.</summary>
/// <param name="Category">One of the configured category keys, e.g. <c>Game</c>.</param>
/// <param name="GameTitle">Game title for <c>Game</c>, otherwise <see langword="null"/>.</param>
/// <param name="AppName">Chat app for <c>Chat</c>, otherwise <see langword="null"/>.</param>
/// <param name="Confidence">Model's self-reported confidence, clamped to 0..1.</param>
public sealed record VisionResult(string Category, string? GameTitle, string? AppName, double Confidence);

/// <summary>The vision model is temporarily unavailable (server down, model missing, timeout).</summary>
/// <param name="message">What failed.</param>
/// <param name="inner">The underlying error.</param>
public sealed class VisionUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
