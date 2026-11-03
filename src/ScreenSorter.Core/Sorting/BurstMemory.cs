using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Models;

namespace ScreenSorter.Core.Sorting;

/// <summary>A file the model placed, kept so near-identical files can copy its folder.</summary>
/// <param name="Vector">The file's normalized embedding.</param>
/// <param name="Captured">When the screenshot was taken, local time.</param>
/// <param name="ProcessName">The app that had focus, or <see langword="null"/> for backlog.</param>
/// <param name="FileHash">SHA-256 of the file, to see later whether the user moved it.</param>
/// <param name="Classification">The model's decision.</param>
public sealed record BurstAnchor(float[] Vector, DateTime Captured, string? ProcessName, string FileHash, Classification Classification);

/// <summary>The closest usable anchor for a file.</summary>
/// <param name="Anchor">The anchor.</param>
/// <param name="Similarity">Cosine similarity between the file and the anchor.</param>
public sealed record BurstMatch(BurstAnchor Anchor, double Similarity);

/// <summary>
/// Remembers recent model answers for this run of the app. Only real model answers are kept, never copies: otherwise a
/// long session of similar shots could carry one answer forward indefinitely. Thread-safe.
/// </summary>
public sealed class BurstMemory(IOptions<BurstOptions> options)
{
    private readonly Lock _gate = new();
    private readonly List<BurstAnchor> _anchors = [];

    /// <summary>Finds the most similar anchor taken within the window, with the same app in focus.</summary>
    /// <param name="vector">The file's normalized embedding.</param>
    /// <param name="captured">When the file was taken, local time.</param>
    /// <param name="processName">The app that had focus, or <see langword="null"/> for backlog.</param>
    /// <returns>The best match at or above the similarity threshold, or <see langword="null"/>.</returns>
    public BurstMatch? Find(float[] vector, DateTime captured, string? processName)
    {
        var o = options.Value;
        var window = TimeSpan.FromSeconds(o.WindowSeconds);
        lock (_gate)
        {
            BurstMatch? best = null;
            foreach (var anchor in _anchors)
            {
                if ((captured - anchor.Captured).Duration() > window
                    || !string.Equals(processName, anchor.ProcessName, StringComparison.OrdinalIgnoreCase)
                    || vector.Length != anchor.Vector.Length)
                    continue;

                var similarity = Dot(vector, anchor.Vector);
                if (similarity >= o.Similarity && similarity > (best?.Similarity ?? double.MinValue))
                    best = new BurstMatch(anchor, similarity);
            }
            return best;
        }
    }

    /// <summary>Remembers a model answer, dropping the oldest once <see cref="BurstOptions.MaxAnchors"/> is reached.</summary>
    /// <param name="anchor">The answer.</param>
    public void Remember(BurstAnchor anchor)
    {
        lock (_gate)
        {
            _anchors.Add(anchor);
            var excess = _anchors.Count - Math.Max(1, options.Value.MaxAnchors);
            if (excess > 0)
                _anchors.RemoveRange(0, excess);
        }
    }

    /// <summary>Stops using an answer, because the user moved that file.</summary>
    /// <param name="anchor">The answer to drop. Unknown anchors are ignored.</param>
    public void Forget(BurstAnchor anchor)
    {
        lock (_gate)
            _anchors.Remove(anchor);
    }

    private static double Dot(float[] a, float[] b)
    {
        var sum = 0d;
        for (var i = 0; i < a.Length; i++)
            sum += (double)a[i] * b[i];
        return sum;
    }
}
