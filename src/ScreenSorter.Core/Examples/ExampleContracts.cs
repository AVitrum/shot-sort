namespace ScreenSorter.Core.Examples;

/// <summary>Turns a screenshot into a normalized embedding vector.</summary>
public interface IImageEmbedder
{
    /// <summary>Computes the embedding. May load the model; safe to call from several threads (calls are serialized).</summary>
    /// <param name="imagePath">A PNG or JPEG file.</param>
    /// <param name="ct">Cancels the wait for the model.</param>
    /// <returns>A unit-length vector.</returns>
    /// <exception cref="ExamplesUnavailableException">The model is missing or failed to load.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="InvalidDataException">The file is not a decodable image.</exception>
    Task<float[]> EmbedAsync(string imagePath, CancellationToken ct);
}

/// <summary>Vector store of user-confirmed examples.</summary>
public interface IExampleStore
{
    /// <summary>Counts stored examples.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The count; 0 if the collection does not exist yet.</returns>
    /// <exception cref="ExamplesUnavailableException">The store cannot be reached.</exception>
    Task<long> CountAsync(CancellationToken ct);

    /// <summary>Adds an example, or replaces the one with the same <see cref="Example.FileHash"/>. Creates the collection if needed.</summary>
    /// <param name="example">The label and its source.</param>
    /// <param name="vector">The image embedding.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="ExamplesUnavailableException">The store cannot be reached.</exception>
    Task UpsertAsync(Example example, float[] vector, CancellationToken ct);

    /// <summary>Removes the example for a file, if any.</summary>
    /// <param name="fileHash">Lower-case hex SHA-256 of the file.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="ExamplesUnavailableException">The store cannot be reached.</exception>
    Task DeleteAsync(string fileHash, CancellationToken ct);

    /// <summary>Finds the nearest examples.</summary>
    /// <param name="vector">The query embedding.</param>
    /// <param name="limit">Maximum number of hits.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>Hits, most similar first.</returns>
    /// <exception cref="ExamplesUnavailableException">The store cannot be reached.</exception>
    Task<IReadOnlyList<ExampleHit>> SearchAsync(float[] vector, int limit, CancellationToken ct);
}

/// <summary>A confirmed label for one file.</summary>
/// <param name="FileHash">Lower-case hex SHA-256 of the file; the example's identity.</param>
/// <param name="Category">Top-level folder.</param>
/// <param name="SubFolder">Second-level folder, or <see langword="null"/>.</param>
/// <param name="Source">Where the label came from; currently always <see cref="UserSource"/>.</param>
/// <param name="CreatedAt">When it was learned, UTC.</param>
public sealed record Example(string FileHash, string Category, string? SubFolder, string Source, DateTimeOffset CreatedAt)
{
    /// <summary>Source value for labels the user set by moving a file.</summary>
    public const string UserSource = "user";
}

/// <summary>One search result.</summary>
/// <param name="Score">Cosine similarity, -1..1.</param>
/// <param name="Category">The example's top-level folder.</param>
/// <param name="SubFolder">The example's second-level folder, or <see langword="null"/>.</param>
public sealed record ExampleHit(double Score, string Category, string? SubFolder);

/// <summary>The example stage cannot run right now (model missing, Qdrant down). Rules and the vision model still work.</summary>
/// <param name="message">What failed.</param>
/// <param name="inner">The underlying error.</param>
public sealed class ExamplesUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
