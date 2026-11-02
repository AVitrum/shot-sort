using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Examples;

namespace ScreenSorter.Infrastructure.Storage;

/// <summary>
/// <see cref="IExampleStore"/> in a table of the app's SQLite file. A search compares the query with every example:
/// only the user's corrections are stored, so an index would add a native dependency for no measurable gain.
/// Each call opens its own connection, so the class is safe to use from several threads.
/// </summary>
public sealed class SqliteExampleStore : IExampleStore
{
    private const string Schema = """
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS examples (
            collection TEXT NOT NULL,
            file_hash  TEXT NOT NULL,
            category   TEXT NOT NULL,
            sub_folder TEXT NULL,
            source     TEXT NOT NULL,
            created_at TEXT NOT NULL,
            vector     BLOB NOT NULL,
            PRIMARY KEY (collection, file_hash)
        );
        """;

    private readonly string _path;
    private readonly string _connectionString;
    private readonly string _collection;
    private readonly int _embeddingSize;
    private volatile bool _ready;

    /// <summary>Creates the store; the database file and folder are created on first use.</summary>
    /// <param name="paths">Provides <see cref="PathsOptions.DatabasePath"/>.</param>
    /// <param name="options">Provides the collection name.</param>
    /// <param name="clip">Provides the vector length.</param>
    public SqliteExampleStore(IOptions<PathsOptions> paths, IOptions<ExamplesOptions> options, IOptions<ClipOptions> clip)
    {
        _path = PathsOptions.Expand(paths.Value.DatabasePath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _path, Pooling = true }.ToString();
        _collection = options.Value.Collection;
        _embeddingSize = clip.Value.EmbeddingSize;
    }

    /// <inheritdoc />
    public Task<long> CountAsync(CancellationToken ct) => RunAsync(async connection =>
    {
        await using var cmd = Command(connection, "SELECT COUNT(*) FROM examples WHERE collection = $collection;");
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }, ct);

    /// <inheritdoc />
    public Task<IReadOnlySet<string>> ListFileHashesAsync(CancellationToken ct) => RunAsync<IReadOnlySet<string>>(async connection =>
    {
        await using var cmd = Command(connection, "SELECT file_hash FROM examples WHERE collection = $collection;");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await r.ReadAsync(ct))
            hashes.Add(r.GetString(0));
        return hashes;
    }, ct);

    /// <inheritdoc />
    public Task UpsertAsync(Example example, float[] vector, CancellationToken ct)
    {
        if (vector.Length != _embeddingSize)
            throw new ExamplesUnavailableException($"Expected a vector of {_embeddingSize} values, got {vector.Length}");

        return RunAsync<object?>(async connection =>
        {
            await using var cmd = Command(connection, """
                INSERT OR REPLACE INTO examples (collection, file_hash, category, sub_folder, source, created_at, vector)
                VALUES ($collection, $hash, $category, $sub, $source, $createdAt, $vector);
                """);
            cmd.Parameters.AddWithValue("$hash", example.FileHash);
            cmd.Parameters.AddWithValue("$category", example.Category);
            cmd.Parameters.AddWithValue("$sub", (object?)example.SubFolder ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source", example.Source);
            cmd.Parameters.AddWithValue("$createdAt", example.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$vector", MemoryMarshal.AsBytes(vector.AsSpan()).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
            return null;
        }, ct);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string fileHash, CancellationToken ct) => RunAsync<object?>(async connection =>
    {
        await using var cmd = Command(connection, "DELETE FROM examples WHERE collection = $collection AND file_hash = $hash;");
        cmd.Parameters.AddWithValue("$hash", fileHash);
        await cmd.ExecuteNonQueryAsync(ct);
        return null;
    }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ExampleHit>> SearchAsync(float[] vector, int limit, CancellationToken ct) => RunAsync<IReadOnlyList<ExampleHit>>(async connection =>
    {
        await using var cmd = Command(connection, "SELECT category, sub_folder, vector FROM examples WHERE collection = $collection;");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var hits = new List<ExampleHit>();
        while (await r.ReadAsync(ct))
        {
            var stored = MemoryMarshal.Cast<byte, float>(r.GetFieldValue<byte[]>(2));
            if (stored.Length == vector.Length)
                hits.Add(new ExampleHit(Cosine(vector, stored), r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
        }
        return hits.OrderByDescending(h => h.Score).Take(limit).ToList();
    }, ct);

    /// <summary>Computes the cosine similarity of two vectors of the same length.</summary>
    /// <param name="a">One vector.</param>
    /// <param name="b">The other vector.</param>
    /// <returns>-1 to 1, or 0 if either vector has no length.</returns>
    internal static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return normA == 0 || normB == 0 ? 0 : dot / Math.Sqrt(normA * normB);
    }

    private SqliteCommand Command(SqliteConnection connection, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$collection", _collection);
        return cmd;
    }

    private async Task<T> RunAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken ct)
    {
        try
        {
            await using var connection = await OpenAsync(ct);
            return await action(connection);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new ExamplesUnavailableException($"Example store failed: {ex.Message}", ex);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        if (!_ready)
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        if (!_ready)
        {
            // Safe to repeat, so a failure here is retried by the next call instead of lasting until a restart.
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = Schema;
            await cmd.ExecuteNonQueryAsync(ct);
            _ready = true;
        }
        return connection;
    }
}
