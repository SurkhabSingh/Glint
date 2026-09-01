using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Glint.Phase0.Core;

public interface ICaptureEventStore
{
    bool ContainsContentHash(string contentHash);

    void Insert(RawCaptureEvent captureEvent);
}

public sealed class Phase0Database : ICaptureEventStore, IManualScanStore, IDisposable
{
    private static readonly Lock InitializationLock = new();
    private static bool _sqliteInitialized;

    private readonly SqliteConnection _connection;
    private readonly string _path;
    private bool _sqliteVecAvailable;

    private Phase0Database(SqliteConnection connection, string path)
    {
        _connection = connection;
        _path = path;
    }

    public static Phase0Database Open(
        string databasePath,
        DpapiKeyStore keyStore,
        string? sqliteVecExtensionPath = null)
    {
        EnsureSqliteInitialized();

        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Database path has no parent directory."));

        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
        connection.Open();

        var key = keyStore.GetOrCreateKey();
        try
        {
            using var keyCommand = connection.CreateCommand();
            keyCommand.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(key)}'\";";
            keyCommand.ExecuteNonQuery();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        var database = new Phase0Database(connection, fullPath);
        database.AssertEncryptionAvailable();
        database.Configure();
        database.ApplyMigrations();
        database.TryLoadSqliteVec(sqliteVecExtensionPath);
        return database;
    }

    public StorageDiagnostics GetDiagnostics()
    {
        var cipher = ScalarString("PRAGMA cipher_version;");
        var sqlite = ScalarString("SELECT sqlite_version();");
        var fts = ScalarLong("SELECT sqlite_compileoption_used('ENABLE_FTS5');") == 1;
        return new StorageDiagnostics(cipher, sqlite, fts, _sqliteVecAvailable, _path);
    }

    public bool ContainsContentHash(string contentHash)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM raw_events WHERE content_hash = $hash);";
        command.Parameters.AddWithValue("$hash", contentHash);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public void Insert(RawCaptureEvent captureEvent)
    {
        using var transaction = _connection.BeginTransaction();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO raw_events
                    (id, ts_ms, process_name, executable_path, window_title,
                     content_hash, text_length, redactions)
                VALUES
                    ($id, $ts, $process, $path, $title, $hash, $length, $redactions);
                """;
            command.Parameters.AddWithValue("$id", captureEvent.Id);
            command.Parameters.AddWithValue("$ts", captureEvent.TimestampMilliseconds);
            command.Parameters.AddWithValue("$process", captureEvent.ProcessName);
            command.Parameters.AddWithValue("$path", (object?)captureEvent.ExecutablePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$title", captureEvent.WindowTitle);
            command.Parameters.AddWithValue("$hash", captureEvent.ContentHash);
            command.Parameters.AddWithValue("$length", captureEvent.Text.Length);
            command.Parameters.AddWithValue("$redactions", captureEvent.Redactions);
            command.ExecuteNonQuery();
        }

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO raw_events_fts (event_id, text, content_hash)
                VALUES ($id, $text, $hash);
                """;
            command.Parameters.AddWithValue("$id", captureEvent.Id);
            command.Parameters.AddWithValue("$text", captureEvent.Text);
            command.Parameters.AddWithValue("$hash", captureEvent.ContentHash);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public int CountEvents()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM raw_events;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void SaveManualScan(RawCaptureEvent captureEvent, ManualScanRecord scan)
    {
        using var transaction = _connection.BeginTransaction();
        var inserted = 0;
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT OR IGNORE INTO raw_events
                    (id, ts_ms, process_name, executable_path, window_title,
                     content_hash, text_length, redactions)
                VALUES
                    ($id, $ts, $process, $path, $title, $hash, $length, $redactions);
                """;
            command.Parameters.AddWithValue("$id", captureEvent.Id);
            command.Parameters.AddWithValue("$ts", captureEvent.TimestampMilliseconds);
            command.Parameters.AddWithValue("$process", captureEvent.ProcessName);
            command.Parameters.AddWithValue(
                "$path",
                (object?)captureEvent.ExecutablePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$title", captureEvent.WindowTitle);
            command.Parameters.AddWithValue("$hash", captureEvent.ContentHash);
            command.Parameters.AddWithValue("$length", captureEvent.Text.Length);
            command.Parameters.AddWithValue("$redactions", captureEvent.Redactions);
            inserted = command.ExecuteNonQuery();
        }

        if (inserted > 0)
        {
            using var fts = _connection.CreateCommand();
            fts.Transaction = transaction;
            fts.CommandText =
                """
                INSERT INTO raw_events_fts (event_id, text, content_hash)
                VALUES ($id, $text, $hash);
                """;
            fts.Parameters.AddWithValue("$id", captureEvent.Id);
            fts.Parameters.AddWithValue("$text", captureEvent.Text);
            fts.Parameters.AddWithValue("$hash", captureEvent.ContentHash);
            fts.ExecuteNonQuery();
        }

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO manual_scans
                    (id, captured_at_ms, process_name, window_title, label, summary,
                     status, error, content_hash, model_id, uia_characters,
                     ocr_characters, redactions, capture_ms, ocr_ms, inference_ms,
                     important_signals, reminder_candidate, gemma_context_characters,
                     redacted_uia_text, redacted_ocr_text, ocr_language)
                VALUES
                    ($id, $capturedAt, $process, $title, $label, $summary,
                     $status, $error, $hash, $model, $uiaCharacters,
                     $ocrCharacters, $redactions, $captureMs, $ocrMs, $inferenceMs,
                     $importantSignals, $reminderCandidate, $gemmaContextCharacters,
                     $redactedUiaText, $redactedOcrText, $ocrLanguage);
                """;
            command.Parameters.AddWithValue("$id", scan.Id);
            command.Parameters.AddWithValue("$capturedAt", scan.CapturedAtMilliseconds);
            command.Parameters.AddWithValue("$process", scan.ProcessName);
            command.Parameters.AddWithValue("$title", scan.WindowTitle);
            command.Parameters.AddWithValue("$label", (object?)scan.Label ?? DBNull.Value);
            command.Parameters.AddWithValue("$summary", (object?)scan.Summary ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", scan.Status.ToString());
            command.Parameters.AddWithValue("$error", (object?)scan.Error ?? DBNull.Value);
            command.Parameters.AddWithValue("$hash", scan.ContentHash);
            command.Parameters.AddWithValue("$model", scan.ModelId);
            command.Parameters.AddWithValue("$uiaCharacters", scan.UiAutomationCharacters);
            command.Parameters.AddWithValue("$ocrCharacters", scan.OcrCharacters);
            command.Parameters.AddWithValue("$redactions", scan.Redactions);
            command.Parameters.AddWithValue("$captureMs", scan.CaptureMilliseconds);
            command.Parameters.AddWithValue("$ocrMs", scan.OcrMilliseconds);
            command.Parameters.AddWithValue("$inferenceMs", scan.InferenceMilliseconds);
            command.Parameters.AddWithValue(
                "$importantSignals",
                (object?)scan.ImportantSignals ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$reminderCandidate",
                (object?)scan.ReminderCandidate ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$gemmaContextCharacters",
                scan.GemmaContextCharacters);
            command.Parameters.AddWithValue(
                "$redactedUiaText",
                (object?)scan.RedactedUiAutomationText ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$redactedOcrText",
                (object?)scan.RedactedOcrText ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$ocrLanguage",
                (object?)scan.OcrLanguage ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public bool ContainsManualScanContentHash(string contentHash)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM manual_scans WHERE content_hash = $hash);";
        command.Parameters.AddWithValue("$hash", contentHash);
        return Convert.ToInt64(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public IReadOnlyList<ManualScanRecord> GetRecentManualScans(int limit = 50)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT m.id, m.captured_at_ms, m.process_name, m.window_title,
                   m.label, m.summary, m.status, m.error, m.content_hash,
                   m.model_id, m.uia_characters, m.ocr_characters,
                   m.redactions, m.capture_ms, m.ocr_ms, m.inference_ms,
                   m.important_signals, m.reminder_candidate,
                   m.gemma_context_characters, f.text, m.redacted_uia_text,
                   m.redacted_ocr_text, m.ocr_language
            FROM manual_scans AS m
            LEFT JOIN raw_events_fts AS f
              ON f.content_hash = m.content_hash
            ORDER BY m.captured_at_ms DESC, m.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var scans = new List<ManualScanRecord>();
        while (reader.Read())
        {
            scans.Add(new(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                Enum.Parse<ManualScanStatus>(reader.GetString(6), ignoreCase: false),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetInt32(12),
                reader.GetDouble(13),
                reader.GetDouble(14),
                reader.GetDouble(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.GetInt32(18),
                reader.IsDBNull(19) ? null : reader.GetString(19),
                reader.IsDBNull(20) ? null : reader.GetString(20),
                reader.IsDBNull(21) ? null : reader.GetString(21),
                reader.IsDBNull(22) ? null : reader.GetString(22)));
        }

        return scans;
    }

    public IReadOnlyList<string> Search(string query, int limit = 10)
    {
        var ftsQuery = BuildFtsQuery(query);
        if (ftsQuery.Length == 0)
        {
            return [];
        }

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT snippet(raw_events_fts, 1, '[', ']', '...', 12)
            FROM raw_events_fts
            WHERE raw_events_fts MATCH $query
            ORDER BY rank
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", ftsQuery);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var results = new List<string>();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    public IReadOnlyList<ContextSearchResult> SearchContext(string query, int limit = 30)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var tokens = GetSearchTokens(query);
        if (tokens.Count == 0)
        {
            return [];
        }

        var ftsQuery = BuildFtsQuery(tokens);
        var metadataPredicates = string.Join(
            Environment.NewLine + "                     AND ",
            tokens.Select((_, index) =>
                $"""
                lower(
                    COALESCE(m.label, '') || ' ' ||
                    COALESCE(m.summary, '') || ' ' ||
                    COALESCE(m.important_signals, '') || ' ' ||
                    COALESCE(m.reminder_candidate, '') || ' ' ||
                    m.process_name || ' ' ||
                    m.window_title)
                LIKE $like{index} ESCAPE '\'
                """));
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
            WITH fts_hits AS (
                SELECT content_hash,
                       snippet(raw_events_fts, 1, '[', ']', '...', 24) AS snippet,
                       bm25(raw_events_fts) AS rank
                FROM raw_events_fts
                WHERE raw_events_fts MATCH $ftsQuery
            )
            SELECT m.id, m.captured_at_ms, m.process_name, m.window_title,
                   m.label, m.summary, m.important_signals, m.reminder_candidate,
                   COALESCE(
                       f.snippet,
                       substr(COALESCE(m.summary, m.label, m.window_title), 1, 320))
            FROM manual_scans AS m
            LEFT JOIN fts_hits AS f ON f.content_hash = m.content_hash
            WHERE f.content_hash IS NOT NULL
               OR ({metadataPredicates})
            ORDER BY CASE WHEN f.content_hash IS NULL THEN 1 ELSE 0 END,
                     f.rank,
                     m.captured_at_ms DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$ftsQuery", ftsQuery);
        for (var index = 0; index < tokens.Count; index++)
        {
            command.Parameters.AddWithValue(
                $"$like{index}",
                $"%{EscapeLike(tokens[index].ToLowerInvariant())}%");
        }

        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var results = new List<ContextSearchResult>();
        while (reader.Read())
        {
            results.Add(new(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? string.Empty : reader.GetString(8)));
        }

        return results;
    }

    public void UpsertEmbedding(long rowId, ReadOnlySpan<float> embedding)
    {
        AssertEmbeddingAvailable(embedding);
        using var transaction = _connection.BeginTransaction();
        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM phase0_vec WHERE rowid = $rowid;";
            delete.Parameters.AddWithValue("$rowid", rowId);
            delete.ExecuteNonQuery();
        }

        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO phase0_vec(rowid, embedding)
                VALUES ($rowid, $embedding);
                """;
            insert.Parameters.AddWithValue("$rowid", rowId);
            insert.Parameters.Add("$embedding", SqliteType.Blob).Value =
                MemoryMarshal.AsBytes(embedding).ToArray();
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<VectorSearchResult> SearchEmbeddings(
        ReadOnlySpan<float> embedding,
        int limit = 10)
    {
        AssertEmbeddingAvailable(embedding);
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT rowid, distance
            FROM phase0_vec
            WHERE embedding MATCH $embedding
              AND k = $limit
            ORDER BY distance;
            """;
        command.Parameters.Add("$embedding", SqliteType.Blob).Value =
            MemoryMarshal.AsBytes(embedding).ToArray();
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var results = new List<VectorSearchResult>();
        while (reader.Read())
        {
            results.Add(new(reader.GetInt64(0), reader.GetDouble(1)));
        }

        return results;
    }

    public void Dispose() => _connection.Dispose();

    private static void EnsureSqliteInitialized()
    {
        lock (InitializationLock)
        {
            if (_sqliteInitialized)
            {
                return;
            }

            SQLitePCL.Batteries_V2.Init();
            _sqliteInitialized = true;
        }
    }

    private void AssertEncryptionAvailable()
    {
        var version = ScalarString("PRAGMA cipher_version;");
        if (string.IsNullOrWhiteSpace(version))
        {
            _connection.Dispose();
            throw new InvalidOperationException(
                "SQLCipher is unavailable. Refusing to create a plaintext memory store.");
        }
    }

    private void Configure()
    {
        Execute(
            """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = 5000;
            PRAGMA temp_store = MEMORY;
            """);
    }

    private void ApplyMigrations()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER PRIMARY KEY,
                applied_at_ms INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS raw_events (
                id TEXT PRIMARY KEY,
                ts_ms INTEGER NOT NULL,
                process_name TEXT NOT NULL,
                executable_path TEXT,
                window_title TEXT NOT NULL,
                content_hash TEXT NOT NULL UNIQUE,
                text_length INTEGER NOT NULL,
                redactions INTEGER NOT NULL
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS raw_events_fts USING fts5(
                event_id UNINDEXED,
                text,
                content_hash UNINDEXED
            );

            INSERT OR IGNORE INTO schema_version(version, applied_at_ms)
            VALUES (1, CAST(unixepoch('subsec') * 1000 AS INTEGER));

            CREATE TABLE IF NOT EXISTS manual_scans (
                id TEXT PRIMARY KEY,
                captured_at_ms INTEGER NOT NULL,
                process_name TEXT NOT NULL,
                window_title TEXT NOT NULL,
                label TEXT,
                summary TEXT,
                status TEXT NOT NULL CHECK(status IN ('Completed', 'ModelFailed')),
                error TEXT,
                content_hash TEXT NOT NULL,
                model_id TEXT NOT NULL,
                uia_characters INTEGER NOT NULL,
                ocr_characters INTEGER NOT NULL,
                redactions INTEGER NOT NULL,
                capture_ms REAL NOT NULL,
                ocr_ms REAL NOT NULL,
                inference_ms REAL NOT NULL,
                important_signals TEXT,
                reminder_candidate TEXT,
                gemma_context_characters INTEGER NOT NULL DEFAULT 0,
                redacted_uia_text TEXT,
                redacted_ocr_text TEXT,
                ocr_language TEXT
            ) STRICT;

            CREATE INDEX IF NOT EXISTS idx_manual_scans_captured_at
            ON manual_scans(captured_at_ms DESC);

            INSERT OR IGNORE INTO schema_version(version, applied_at_ms)
            VALUES (2, CAST(unixepoch('subsec') * 1000 AS INTEGER));
            """);

        EnsureManualScanColumn("important_signals");
        EnsureManualScanColumn("reminder_candidate");
        EnsureManualScanColumn("gemma_context_characters");
        EnsureManualScanColumn("redacted_uia_text");
        EnsureManualScanColumn("redacted_ocr_text");
        EnsureManualScanColumn("ocr_language");
        Execute(
            """
            INSERT OR IGNORE INTO schema_version(version, applied_at_ms)
            VALUES (4, CAST(unixepoch('subsec') * 1000 AS INTEGER));

            INSERT OR IGNORE INTO schema_version(version, applied_at_ms)
            VALUES (5, CAST(unixepoch('subsec') * 1000 AS INTEGER));
            """);
    }

    private void EnsureManualScanColumn(string columnName)
    {
        using var query = _connection.CreateCommand();
        query.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM pragma_table_info('manual_scans')
                WHERE name = $column
            );
            """;
        query.Parameters.AddWithValue("$column", columnName);
        var exists = Convert.ToInt64(
            query.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
        if (exists)
        {
            return;
        }

        var sql = columnName switch
        {
            "important_signals" =>
                "ALTER TABLE manual_scans ADD COLUMN important_signals TEXT;",
            "reminder_candidate" =>
                "ALTER TABLE manual_scans ADD COLUMN reminder_candidate TEXT;",
            "gemma_context_characters" =>
                "ALTER TABLE manual_scans ADD COLUMN gemma_context_characters INTEGER NOT NULL DEFAULT 0;",
            "redacted_uia_text" =>
                "ALTER TABLE manual_scans ADD COLUMN redacted_uia_text TEXT;",
            "redacted_ocr_text" =>
                "ALTER TABLE manual_scans ADD COLUMN redacted_ocr_text TEXT;",
            "ocr_language" =>
                "ALTER TABLE manual_scans ADD COLUMN ocr_language TEXT;",
            _ => throw new ArgumentOutOfRangeException(
                nameof(columnName),
                columnName,
                "Unknown manual scan migration column.")
        };
        Execute(sql);
    }

    private void TryLoadSqliteVec(string? extensionPath)
    {
        if (string.IsNullOrWhiteSpace(extensionPath) || !File.Exists(extensionPath))
        {
            _sqliteVecAvailable = false;
            return;
        }

        try
        {
            _connection.EnableExtensions(true);
            _connection.LoadExtension(Path.GetFullPath(extensionPath));
            Execute("CREATE VIRTUAL TABLE IF NOT EXISTS phase0_vec USING vec0(embedding float[768]);");
            _sqliteVecAvailable = true;
        }
        catch (SqliteException)
        {
            _sqliteVecAvailable = false;
        }
        finally
        {
            _connection.EnableExtensions(false);
        }
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void AssertEmbeddingAvailable(ReadOnlySpan<float> embedding)
    {
        if (!_sqliteVecAvailable)
        {
            throw new InvalidOperationException("sqlite-vec is not loaded.");
        }

        if (embedding.Length != 768)
        {
            throw new ArgumentException(
                "Phase 0 embeddings must contain exactly 768 floats.",
                nameof(embedding));
        }
    }

    private string ScalarString(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
                   command.ExecuteScalar(),
                   System.Globalization.CultureInfo.InvariantCulture)
               ?? string.Empty;
    }

    private long ScalarLong(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string BuildFtsQuery(string query) =>
        BuildFtsQuery(GetSearchTokens(query));

    private static string BuildFtsQuery(IReadOnlyList<string> tokens)
    {
        return string.Join(
            ' ',
            tokens.Select(token =>
                $"\"{token.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
    }

    private static IReadOnlyList<string> GetSearchTokens(string query) =>
        query.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string EscapeLike(string value) =>
        value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}
