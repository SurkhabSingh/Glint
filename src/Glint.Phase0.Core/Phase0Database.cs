using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Glint.Phase0.Core;

/// <summary>
/// The encrypted store. One process (the backend) owns the only connection
/// that writes; screens and Ask read through connections opened read-only.
/// </summary>
/// <remarks>
/// What is stored, and nothing derived from it:
///   - looks (<c>manual_scans</c>): which app and page was in front, how it was treated, until when
///   - page lines (<c>content_chunks</c>): every distinct line of text a page showed, once
///   - the focus log: every stretch a window spent in front
///   - markers: recording started or stopped, the user left or locked the PC, an app closed
///   - what is expensive or the user's own: summaries, app modes, task and session verdicts, chats
/// Sessions and activities are worked out from these when they are read
/// (<see cref="ActivityView"/>), so there is nothing to seal, rebuild or recover.
/// </remarks>
public sealed partial class Phase0Database : IActivityStore, IActivityViewStore, IChatStore, IDisposable
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

    /// <summary>
    /// Opens the store. The writer applies upgrades; a reader
    /// (<paramref name="readOnly"/>) never changes anything and can be opened
    /// next to the writer at any time.
    /// </summary>
    public static Phase0Database Open(
        string databasePath,
        DpapiKeyStore keyStore,
        string? sqliteVecExtensionPath = null,
        bool readOnly = false)
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
        if (readOnly)
        {
            database.Execute(
                """
                PRAGMA busy_timeout = 5000;
                PRAGMA temp_store = MEMORY;
                PRAGMA query_only = 1;
                """);
        }
        else
        {
            database.Configure();
            database.ApplyMigrations();
        }

        database.TryLoadSqliteVec(sqliteVecExtensionPath, createTable: !readOnly);
        return database;
    }

    public string DatabasePath => _path;

    public StorageDiagnostics GetDiagnostics()
    {
        var cipher = ScalarString("PRAGMA cipher_version;");
        var sqlite = ScalarString("SELECT sqlite_version();");
        var fts = ScalarLong("SELECT sqlite_compileoption_used('ENABLE_FTS5');") == 1;
        return new StorageDiagnostics(cipher, sqlite, fts, _sqliteVecAvailable, _path);
    }

    /// How many distinct page lines are stored.
    public int CountChunks()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM content_chunks;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    // -----------------------------------------------------------------------
    // Markers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Records that the user went away or came back, or that recording
    /// started or stopped. These are what let the sessionizer tell a real
    /// break from a screen that simply did not change.
    /// </summary>
    public void RecordMarker(string kind, long timestampMilliseconds, string? detail = null)
    {
        if (!ActivityMarker.Kinds.Contains(kind))
        {
            throw new ArgumentException($"Unknown marker kind: {kind}.", nameof(kind));
        }

        using var command = _connection.CreateCommand();
        command.CommandText =
            "INSERT INTO activity_markers (ts_ms, kind, detail) VALUES ($ts, $kind, $detail);";
        command.Parameters.AddWithValue("$ts", timestampMilliseconds);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$detail", (object?)detail?.Trim().ToLowerInvariant() ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// Markers within a window, oldest first.
    public IReadOnlyList<ActivityMarker> GetMarkers(
        long fromMilliseconds,
        long toMilliseconds)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT ts_ms, kind, detail
            FROM activity_markers
            WHERE ts_ms >= $from AND ts_ms <= $to
            ORDER BY ts_ms ASC, id ASC;
            """;
        command.Parameters.AddWithValue("$from", fromMilliseconds);
        command.Parameters.AddWithValue("$to", toMilliseconds);
        using var reader = command.ExecuteReader();
        var markers = new List<ActivityMarker>();
        while (reader.Read())
        {
            markers.Add(new(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return markers;
    }

    public long GetLatestBreakMilliseconds()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT COALESCE(MAX(ts_ms), 0) FROM activity_markers
            WHERE kind IN ('user.away', 'run.stopped', 'user.locked', 'system.sleep', 'system.shutdown');
            """;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// After a crash or a shutdown the last recording never got its stop. If
    /// the newest run marker is a start (or a shutdown) with no stop after it,
    /// the run is closed at the last moment anything was seen. Returns when,
    /// or null when nothing was left open.
    /// </summary>
    public long? CloseUnfinishedRun(long nowMilliseconds)
    {
        string? lastKind = null;
        long lastAt = 0;
        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT kind, ts_ms FROM activity_markers
                WHERE kind IN ('run.started', 'run.stopped', 'system.shutdown')
                ORDER BY ts_ms DESC, id DESC LIMIT 1;
                """;
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                lastKind = reader.GetString(0);
                lastAt = reader.GetInt64(1);
            }
        }

        if (lastKind is not ("run.started" or "system.shutdown"))
        {
            return null;
        }

        using var seen = _connection.CreateCommand();
        seen.CommandText =
            """
            SELECT MAX(at) FROM (
                SELECT MAX(COALESCE(last_seen_ms, captured_at_ms)) AS at FROM manual_scans WHERE captured_at_ms >= $from
                UNION ALL
                SELECT MAX(last_seen_ms) FROM focus_log WHERE started_at_ms >= $from);
            """;
        seen.Parameters.AddWithValue("$from", lastAt);
        var lastSeen = seen.ExecuteScalar() is long value ? value : lastAt;
        var closedAt = Math.Min(Math.Max(lastSeen, lastAt), nowMilliseconds);
        RecordMarker("run.stopped", closedAt);
        return closedAt;
    }

    // -----------------------------------------------------------------------
    // Chat
    // -----------------------------------------------------------------------

    /// <summary>
    /// Agent chat history. Threads order by last activity so the panel reads
    /// like any messenger: the active conversation stays on top. Message text
    /// lives only here, inside the encrypted store.
    /// </summary>
    public ChatThread CreateChatThread(string title, string scope, long atMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var thread = new ChatThread(
            Guid.NewGuid().ToString("N"),
            title.Trim(),
            string.IsNullOrWhiteSpace(scope) ? "all" : scope.Trim(),
            atMilliseconds,
            atMilliseconds);
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO chat_threads (id, title, scope, created_at_ms, updated_at_ms)
            VALUES ($id, $title, $scope, $at, $at);
            """;
        command.Parameters.AddWithValue("$id", thread.Id);
        command.Parameters.AddWithValue("$title", thread.Title);
        command.Parameters.AddWithValue("$scope", thread.Scope);
        command.Parameters.AddWithValue("$at", atMilliseconds);
        command.ExecuteNonQuery();
        return thread;
    }

    public IReadOnlyList<ChatThread> GetRecentChatThreads(int limit = 50)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, title, scope, created_at_ms, updated_at_ms
            FROM chat_threads
            ORDER BY updated_at_ms DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var threads = new List<ChatThread>();
        while (reader.Read())
        {
            threads.Add(ReadChatThread(reader));
        }

        return threads;
    }

    public ChatThread? GetChatThread(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, title, scope, created_at_ms, updated_at_ms
            FROM chat_threads
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadChatThread(reader) : null;
    }

    public void RenameChatThread(string id, string title, long atMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE chat_threads
            SET title = $title,
                updated_at_ms = $at
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$title", title.Trim());
        command.Parameters.AddWithValue("$at", atMilliseconds);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void DeleteChatThread(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM chat_threads WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public ChatMessage AppendChatMessage(
        string threadId,
        string role,
        string text,
        string citationsJson,
        int scopedCount,
        long atMilliseconds)
    {
        if (!ChatRoles.IsValid(role))
        {
            throw new ArgumentException(
                $"role must be one of: {ChatRoles.User}, {ChatRoles.Agent}, {ChatRoles.Error}.",
                nameof(role));
        }

        ArgumentNullException.ThrowIfNull(text);
        var message = new ChatMessage(
            Guid.NewGuid().ToString("N"),
            threadId,
            role,
            text,
            string.IsNullOrWhiteSpace(citationsJson) ? "[]" : citationsJson,
            Math.Max(0, scopedCount),
            atMilliseconds);
        using var transaction = _connection.BeginTransaction();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO chat_messages
                    (id, thread_id, role, text, citations_json, scoped_count, created_at_ms)
                VALUES
                    ($id, $thread, $role, $text, $citations, $scoped, $at);
                """;
            command.Parameters.AddWithValue("$id", message.Id);
            command.Parameters.AddWithValue("$thread", threadId);
            command.Parameters.AddWithValue("$role", role);
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$citations", message.CitationsJson);
            command.Parameters.AddWithValue("$scoped", message.ScopedCount);
            command.Parameters.AddWithValue("$at", atMilliseconds);
            command.ExecuteNonQuery();
        }

        // The thread sorts by last activity, so every message bumps it.
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "UPDATE chat_threads SET updated_at_ms = $at WHERE id = $id;";
            command.Parameters.AddWithValue("$at", atMilliseconds);
            command.Parameters.AddWithValue("$id", threadId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return message;
    }

    public IReadOnlyList<ChatMessage> GetChatMessages(string threadId, int limit = 200)
    {
        if (limit is < 1 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, thread_id, role, text, citations_json, scoped_count, created_at_ms
            FROM chat_messages
            WHERE thread_id = $thread
            ORDER BY created_at_ms ASC, id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$thread", threadId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var messages = new List<ChatMessage>();
        while (reader.Read())
        {
            messages.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? "[]" : reader.GetString(4),
                reader.IsDBNull(5) ? 0 : checked((int)reader.GetInt64(5)),
                reader.GetInt64(6)));
        }

        return messages;
    }

    private static ChatThread ReadChatThread(System.Data.Common.DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? "all" : reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4));

    // -----------------------------------------------------------------------
    // Search
    // -----------------------------------------------------------------------

    /// <summary>
    /// Page lines matching every word of the query, best first, with the look
    /// that first saw each one; then looks whose app, title or label match.
    /// </summary>
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

        var results = new List<ContextSearchResult>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT COALESCE(c.scan_id, 'chunk-' || c.id), c.first_seen_ms,
                       COALESCE(m.process_name, ''), COALESCE(m.window_title, ''), m.label,
                       snippet(content_chunks_fts, 0, '[', ']', '...', 24)
                FROM content_chunks_fts
                JOIN content_chunks AS c ON c.id = content_chunks_fts.rowid
                LEFT JOIN manual_scans AS m ON m.id = c.scan_id
                WHERE content_chunks_fts MATCH $query
                ORDER BY bm25(content_chunks_fts), c.last_seen_ms DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$query", BuildFtsQuery(tokens));
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    null,
                    null,
                    null,
                    reader.IsDBNull(5) ? string.Empty : reader.GetString(5)));
            }
        }

        if (results.Count >= limit)
        {
            return results;
        }

        // Nothing on screen said it, but the window did: an app, a title, a page.
        using (var command = _connection.CreateCommand())
        {
            var predicates = string.Join(
                " AND ",
                tokens.Select((_, index) =>
                    $"lower(COALESCE(label, '') || ' ' || process_name || ' ' || window_title || ' ' || COALESCE(site, '')) LIKE $like{index} ESCAPE '\\'"));
            command.CommandText =
                $"""
                SELECT id, captured_at_ms, process_name, window_title, label
                FROM manual_scans
                WHERE {predicates}
                ORDER BY captured_at_ms DESC
                LIMIT $limit;
                """;
            for (var index = 0; index < tokens.Count; index++)
            {
                command.Parameters.AddWithValue($"$like{index}", $"%{EscapeLike(tokens[index].ToLowerInvariant())}%");
            }

            command.Parameters.AddWithValue("$limit", limit - results.Count);
            using var reader = command.ExecuteReader();
            var seen = results.Select(result => result.Id).ToHashSet(StringComparer.Ordinal);
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (!seen.Add(id))
                {
                    continue;
                }

                var title = reader.GetString(3);
                var label = reader.IsDBNull(4) ? null : reader.GetString(4);
                results.Add(new(
                    id,
                    reader.GetInt64(1),
                    reader.GetString(2),
                    title,
                    label,
                    null,
                    null,
                    null,
                    label ?? title));
            }
        }

        return results;
    }

    // -----------------------------------------------------------------------
    // Embeddings (sqlite-vec)
    // -----------------------------------------------------------------------

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

    // -----------------------------------------------------------------------
    // Plumbing
    // -----------------------------------------------------------------------

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

    private void TryLoadSqliteVec(string? extensionPath, bool createTable)
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
            if (createTable)
            {
                Execute("CREATE VIRTUAL TABLE IF NOT EXISTS phase0_vec USING vec0(embedding float[768]);");
            }

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

    private static string BindIds(SqliteCommand command, IEnumerable<string> ids, string prefix = "id")
    {
        var names = new List<string>();
        var index = 0;
        foreach (var id in ids)
        {
            var name = $"${prefix}{index++}";
            names.Add(name);
            command.Parameters.AddWithValue(name, id);
        }

        return names.Count == 0 ? "NULL" : string.Join(", ", names);
    }
}
