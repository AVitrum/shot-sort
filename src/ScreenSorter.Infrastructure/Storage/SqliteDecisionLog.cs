using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Models;
using ScreenSorter.Core.Sorting;

namespace ScreenSorter.Infrastructure.Storage;

/// <summary>
/// <see cref="IDecisionLog"/> in a SQLite file. The schema is created on first use.
/// Each call opens its own connection, so the class is safe to use from several threads.
/// </summary>
public sealed class SqliteDecisionLog : IDecisionLog
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS decisions (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            original_path TEXT NOT NULL,
            new_path      TEXT NOT NULL,
            file_hash     TEXT NOT NULL,
            category      TEXT NOT NULL,
            sub_folder    TEXT NULL,
            name          TEXT NOT NULL,
            confidence    REAL NOT NULL,
            stage         TEXT NOT NULL,
            process_name  TEXT NULL,
            exe_path      TEXT NULL,
            window_title  TEXT NULL,
            focus_at      TEXT NULL,
            decided_at    TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_decisions_hash ON decisions (file_hash);
        """;

    private const string Columns = "original_path, new_path, file_hash, category, sub_folder, name, confidence, stage, process_name, exe_path, window_title, focus_at, decided_at";
    private const int ColumnCount = 13;

    private readonly string _connectionString;
    private readonly bool _keepFocusDetails;
    private readonly Lazy<Task> _init;

    /// <summary>Creates the log; the database file and folder are created on first use.</summary>
    /// <param name="options">Provides <see cref="PathsOptions.DatabasePath"/>.</param>
    /// <param name="privacy">Whether window titles and program paths are stored at all.</param>
    public SqliteDecisionLog(IOptions<PathsOptions> options, IOptions<PrivacyOptions> privacy)
    {
        _keepFocusDetails = privacy.Value.WindowTitleDays > 0;
        var path = PathsOptions.Expand(options.Value.DatabasePath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        _init = new Lazy<Task>(() => InitializeAsync(path));
    }

    /// <inheritdoc />
    public async Task RecordAsync(SortRecord record, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO decisions (original_path, new_path, file_hash, category, sub_folder, name, confidence,
                                   stage, process_name, exe_path, window_title, focus_at, decided_at)
            VALUES ($original, $new, $hash, $category, $sub, $name, $confidence,
                    $stage, $process, $exe, $title, $focusAt, $decidedAt);
            """;
        var c = record.Classification;
        var f = record.Focus;
        cmd.Parameters.AddWithValue("$original", record.OriginalPath);
        cmd.Parameters.AddWithValue("$new", record.NewPath);
        cmd.Parameters.AddWithValue("$hash", record.FileHash);
        cmd.Parameters.AddWithValue("$category", c.Category);
        cmd.Parameters.AddWithValue("$sub", (object?)c.SubFolder ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", c.Name);
        cmd.Parameters.AddWithValue("$confidence", c.Confidence);
        cmd.Parameters.AddWithValue("$stage", c.Stage.ToString());
        cmd.Parameters.AddWithValue("$process", (object?)f?.ProcessName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exe", Detail(f?.ExePath));
        cmd.Parameters.AddWithValue("$title", Detail(f?.WindowTitle));
        cmd.Parameters.AddWithValue("$focusAt", f is null ? DBNull.Value : f.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("$decidedAt", record.DecidedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<SortRecord?> FindLatestByHashAsync(string fileHash, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM decisions WHERE file_hash = $hash ORDER BY id DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$hash", fileHash);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadRecord(r, 0) : null;
    }

    /// <inheritdoc />
    public async Task<int> EraseFocusDetailsAsync(DateTimeOffset before, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        // secure_delete overwrites the old text instead of only marking it free. The checkpoint then empties the
        // write-ahead log, which would otherwise still hold it.
        cmd.CommandText = """
            PRAGMA secure_delete = ON;
            UPDATE decisions SET window_title = NULL, exe_path = NULL
            WHERE (window_title IS NOT NULL OR exe_path IS NOT NULL) AND COALESCE(focus_at, decided_at) < $before;
            """;
        // Stored times are UTC in round-trip format, so comparing them as text compares them as times.
        cmd.Parameters.AddWithValue("$before", before.ToUniversalTime().ToString("O"));
        var changed = await cmd.ExecuteNonQueryAsync(ct);
        if (changed > 0)
        {
            await using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync(ct);
        }
        return changed;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileDecisions>> ListFilesAsync(int limit, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Prefixed("latest")}, {Prefixed("first")}
            FROM (SELECT MAX(id) AS last_id, MIN(id) AS first_id FROM decisions GROUP BY file_hash) AS ids
            JOIN decisions AS latest ON latest.id = ids.last_id
            JOIN decisions AS first ON first.id = ids.first_id
            ORDER BY latest.id DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var entries = new List<FileDecisions>();
        while (await r.ReadAsync(ct))
            entries.Add(new FileDecisions(ReadRecord(r, 0), ReadRecord(r, ColumnCount)));
        return entries;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DecisionFact>> ListFactsAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT file_hash, stage, category, sub_folder FROM decisions ORDER BY id;";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var facts = new List<DecisionFact>();
        while (await r.ReadAsync(ct))
            facts.Add(new DecisionFact(r.GetString(0), Enum.Parse<DecisionStage>(r.GetString(1)), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return facts;
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> LatestPathsAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT new_path FROM decisions WHERE id IN (SELECT MAX(id) FROM decisions GROUP BY file_hash);";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await r.ReadAsync(ct))
            paths.Add(r.GetString(0));
        return paths;
    }

    private object Detail(string? value) =>
        _keepFocusDetails && !string.IsNullOrEmpty(value) ? value : DBNull.Value;

    private static string Prefixed(string table) =>
        string.Join(", ", Columns.Split(", ").Select(c => $"{table}.{c}"));

    private static SortRecord ReadRecord(SqliteDataReader r, int at)
    {
        var classification = new Classification(
            r.GetString(at + 3), r.IsDBNull(at + 4) ? null : r.GetString(at + 4), r.GetString(at + 5), r.GetDouble(at + 6),
            Enum.Parse<DecisionStage>(r.GetString(at + 7)));
        var focus = r.IsDBNull(at + 11)
            ? null
            : new FocusSnapshot(ParseTime(r.GetString(at + 11)), Text(at + 8), Text(at + 9), Text(at + 10));
        return new SortRecord(r.GetString(at), r.GetString(at + 1), r.GetString(at + 2), classification, focus,
            ParseTime(r.GetString(at + 12)));

        string Text(int column) => r.IsDBNull(column) ? "" : r.GetString(column);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        await _init.Value;
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private async Task InitializeAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;" + Schema;
        await cmd.ExecuteNonQueryAsync();
    }

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
