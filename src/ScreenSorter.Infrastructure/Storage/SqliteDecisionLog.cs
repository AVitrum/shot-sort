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

    private readonly string _connectionString;
    private readonly Lazy<Task> _init;

    /// <summary>Creates the log; the database file and folder are created on first use.</summary>
    /// <param name="options">Provides <see cref="PathsOptions.DatabasePath"/>.</param>
    public SqliteDecisionLog(IOptions<PathsOptions> options)
    {
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
        cmd.Parameters.AddWithValue("$exe", (object?)f?.ExePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", (object?)f?.WindowTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$focusAt", f is null ? DBNull.Value : f.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("$decidedAt", record.DecidedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<SortRecord?> FindLatestByHashAsync(string fileHash, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT original_path, new_path, file_hash, category, sub_folder, name, confidence, stage,
                   process_name, exe_path, window_title, focus_at, decided_at
            FROM decisions WHERE file_hash = $hash ORDER BY id DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$hash", fileHash);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;

        var classification = new Classification(
            r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetDouble(6),
            Enum.Parse<DecisionStage>(r.GetString(7)));
        var focus = r.IsDBNull(11)
            ? null
            : new FocusSnapshot(ParseTime(r.GetString(11)), r.GetString(8), r.GetString(9), r.GetString(10));
        return new SortRecord(r.GetString(0), r.GetString(1), r.GetString(2), classification, focus,
            ParseTime(r.GetString(12)));
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
