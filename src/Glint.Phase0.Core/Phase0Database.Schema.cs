namespace Glint.Phase0.Core;

/// <summary>
/// Upgrades. Versions 1–14 are the history of the store, kept so any older
/// store can still be brought forward; they only run on a store below
/// <see cref="CurrentVersion"/>. Version 15 replaces everything that was
/// derived and stored (sessions, activities, early summaries, full-copy and
/// new-line text records) with what it was derived from.
/// </summary>
public sealed partial class Phase0Database
{
    public const int CurrentVersion = 15;

    private void ApplyMigrations()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER PRIMARY KEY,
                applied_at_ms INTEGER NOT NULL
            );
            """);
        if (HasVersion(CurrentVersion))
        {
            return;
        }

        // A store with history is copied before it is changed, so an upgrade
        // can never cost what was already recorded. (Stores before version 15
        // may have no version rows at all: they were written with a time
        // function this SQLite lacks, and silently skipped.)
        if (ScalarLong("SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'manual_scans');") == 1)
        {
            BackUpBeforeUpgrade();
        }

        ApplyLegacyMigrations();
        if (MigrateToVersion15())
        {
            // The tables version 15 dropped leave their pages behind.
            Execute("VACUUM;");
        }
    }

    private bool HasVersion(int version) =>
        ScalarLong($"SELECT EXISTS(SELECT 1 FROM schema_version WHERE version = {version});") == 1;

    /// <summary>
    /// Copies the store before its first upgrade. Openers take turns through
    /// a lock named after the file, so the copy is made once, before anyone
    /// has changed anything, and never read while another opener writes it.
    /// </summary>
    private void BackUpBeforeUpgrade()
    {
        var backup = _path + $".pre-v{CurrentVersion}.bak";
        var name = "Glint.Upgrade." + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_path.ToUpperInvariant())))[..32];
        using var turn = new Mutex(false, name);
        try
        {
            turn.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // The opener before crashed mid-copy; this one takes over.
        }

        try
        {
            if (File.Exists(backup) || HasVersion(CurrentVersion))
            {
                return;
            }

            // Everything into the main file first, so the copy is whole. The
            // file is open here, so it is read through a handle that shares it.
            Execute("PRAGMA wal_checkpoint(TRUNCATE);");
            var partial = backup + ".partial";
            using (var source = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(target);
            }

            File.Move(partial, backup, overwrite: false);
        }
        finally
        {
            turn.ReleaseMutex();
        }
    }

    // -----------------------------------------------------------------------
    // Version 15
    // -----------------------------------------------------------------------

    /// <summary>
    /// One transaction, taken up front: either the whole change lands or none
    /// of it. Text is moved into page lines, summaries and the user's own
    /// verdicts into the tables that keep them, and the derived tables go.
    /// </summary>
    /// <returns>Whether this opener made the change (false when another already had).</returns>
    private bool MigrateToVersion15()
    {
        EnsureColumn("manual_scans", "executable_path", "TEXT");
        EnsureColumn("page_states", "text_read_at_ms", "INTEGER NOT NULL DEFAULT 0");

        using var transaction = _connection.BeginTransaction(deferred: false);

        void Run(string sql)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        bool TableExists(string name)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
            command.Parameters.AddWithValue("$name", name);
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
        }

        using (var check = _connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = $"SELECT EXISTS(SELECT 1 FROM schema_version WHERE version = {CurrentVersion});";
            if (Convert.ToInt64(check.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1)
            {
                transaction.Commit();
                return false;
            }
        }

        Run(
            """
            CREATE TABLE IF NOT EXISTS content_chunks (
                id INTEGER PRIMARY KEY,
                page_key TEXT NOT NULL,
                line_key TEXT NOT NULL,
                text TEXT NOT NULL,
                first_seen_ms INTEGER NOT NULL,
                last_seen_ms INTEGER NOT NULL,
                scan_id TEXT,
                typed INTEGER NOT NULL DEFAULT 0,
                UNIQUE (page_key, line_key)
            ) STRICT;

            CREATE INDEX IF NOT EXISTS idx_content_chunks_page_seen
            ON content_chunks(page_key, last_seen_ms);

            CREATE INDEX IF NOT EXISTS idx_content_chunks_scan
            ON content_chunks(scan_id);

            CREATE VIRTUAL TABLE IF NOT EXISTS content_chunks_fts USING fts5(
                text,
                content = 'content_chunks',
                content_rowid = 'id'
            );

            CREATE TRIGGER IF NOT EXISTS content_chunks_ai AFTER INSERT ON content_chunks BEGIN
                INSERT INTO content_chunks_fts(rowid, text) VALUES (new.id, new.text);
            END;

            CREATE TRIGGER IF NOT EXISTS content_chunks_ad AFTER DELETE ON content_chunks BEGIN
                INSERT INTO content_chunks_fts(content_chunks_fts, rowid, text) VALUES ('delete', old.id, old.text);
            END;

            CREATE TABLE IF NOT EXISTS focus_log (
                id INTEGER PRIMARY KEY,
                started_at_ms INTEGER NOT NULL,
                ended_at_ms INTEGER,
                last_seen_ms INTEGER NOT NULL,
                process_name TEXT NOT NULL,
                title TEXT,
                suppressed TEXT,
                suppressed_detail TEXT
            ) STRICT;

            CREATE INDEX IF NOT EXISTS idx_focus_log_started
            ON focus_log(started_at_ms);

            CREATE TABLE IF NOT EXISTS activity_summaries (
                activity_key TEXT NOT NULL,
                started_at_ms INTEGER NOT NULL,
                text_hash TEXT,
                label TEXT NOT NULL,
                summary TEXT,
                task TEXT,
                check_state TEXT NOT NULL,
                facts_kept INTEGER NOT NULL DEFAULT 0,
                facts_dropped INTEGER NOT NULL DEFAULT 0,
                created_at_ms INTEGER NOT NULL,
                PRIMARY KEY (activity_key, started_at_ms)
            ) STRICT;

            CREATE INDEX IF NOT EXISTS idx_activity_summaries_text
            ON activity_summaries(activity_key, text_hash);

            CREATE TABLE IF NOT EXISTS activity_tasks (
                activity_id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                set_at_ms INTEGER NOT NULL
            ) STRICT;

            CREATE TABLE IF NOT EXISTS session_outcomes (
                session_id TEXT PRIMARY KEY,
                outcome TEXT NOT NULL,
                set_at_ms INTEGER NOT NULL
            ) STRICT;

            CREATE TABLE IF NOT EXISTS app_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            ) STRICT;
            """);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (TableExists("raw_events"))
        {
            Run(
                """
                UPDATE manual_scans
                SET executable_path = (SELECT r.executable_path FROM raw_events AS r WHERE r.content_hash = manual_scans.content_hash)
                WHERE executable_path IS NULL;
                """);
        }

        if (TableExists("raw_events_fts"))
        {
            MoveTextIntoPageLines(transaction);
        }

        if (TableExists("activities"))
        {
            Run(
                $"""
                INSERT OR IGNORE INTO activity_summaries
                    (activity_key, started_at_ms, text_hash, label, summary, task, check_state, facts_kept, facts_dropped, created_at_ms)
                SELECT key, started_at_ms, NULL, label, summary, task, check_state, facts_kept, facts_dropped, {now}
                FROM activities
                WHERE check_state IN ('Verified', 'Partial', 'Fallback');
                """);
            KeepUserTasks(transaction, now);
        }

        if (TableExists("early_narrations"))
        {
            Run(
                $"""
                INSERT OR IGNORE INTO activity_summaries
                    (activity_key, started_at_ms, text_hash, label, summary, task, check_state, facts_kept, facts_dropped, created_at_ms)
                SELECT activity_key, started_at_ms, NULL, label, summary, task, check_state, facts_kept, facts_dropped, {now}
                FROM early_narrations
                WHERE check_state IN ('Verified', 'Partial', 'Fallback');
                """);
        }

        if (TableExists("activity_sessions"))
        {
            KeepUserOutcomes(transaction, now);
        }

        Run(
            $"""
            DROP TABLE IF EXISTS activities;
            DROP TABLE IF EXISTS early_narrations;
            DROP TABLE IF EXISTS activity_sessions;
            DROP TABLE IF EXISTS raw_events_fts;
            DROP TABLE IF EXISTS raw_events;
            DROP INDEX IF EXISTS idx_manual_scans_session_id;
            DROP INDEX IF EXISTS idx_manual_scans_content_hash;

            INSERT OR IGNORE INTO schema_version(version, applied_at_ms)
            VALUES ({CurrentVersion}, {now});
            """);
        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Every stored copy of a page becomes that page's lines, each kept once
    /// with when it was first and last seen. Captures from before pages had
    /// keys get one from their app and title.
    /// </summary>
    private void MoveTextIntoPageLines(Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        var copies = new List<(string Id, long At, long LastSeen, string PageKey, bool NeedsKey, bool UserCaused, string Text)>();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT m.id, m.captured_at_ms, COALESCE(m.last_seen_ms, m.captured_at_ms),
                       m.page_key, m.process_name, m.window_title, m.user_caused, f.text
                FROM manual_scans AS m
                JOIN raw_events_fts AS f ON f.content_hash = m.content_hash
                WHERE m.change_kind IS NULL OR m.change_kind IN ('Keyframe', 'Delta')
                ORDER BY m.captured_at_ms ASC, m.id ASC;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(7))
                {
                    continue;
                }

                var pageKey = reader.IsDBNull(3) ? null : reader.GetString(3);
                copies.Add((
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    pageKey ?? LegacyPageKey(reader.GetString(4), reader.GetString(5)),
                    pageKey is null,
                    !reader.IsDBNull(6) && reader.GetInt64(6) != 0,
                    reader.GetString(7)));
            }
        }

        foreach (var copy in copies)
        {
            if (copy.NeedsKey)
            {
                using var key = _connection.CreateCommand();
                key.Transaction = transaction;
                key.CommandText = "UPDATE manual_scans SET page_key = $key WHERE id = $id;";
                key.Parameters.AddWithValue("$key", copy.PageKey);
                key.Parameters.AddWithValue("$id", copy.Id);
                key.ExecuteNonQuery();
            }

            foreach (var line in PageLines.Of(copy.Text))
            {
                using var upsert = _connection.CreateCommand();
                upsert.Transaction = transaction;
                upsert.CommandText =
                    """
                    INSERT INTO content_chunks (page_key, line_key, text, first_seen_ms, last_seen_ms, scan_id, typed)
                    VALUES ($page, $key, $text, $at, $seen, $scan, $typed)
                    ON CONFLICT(page_key, line_key) DO UPDATE SET
                        last_seen_ms = MAX(content_chunks.last_seen_ms, excluded.last_seen_ms);
                    """;
                upsert.Parameters.AddWithValue("$page", copy.PageKey);
                upsert.Parameters.AddWithValue("$key", line.Key);
                upsert.Parameters.AddWithValue("$text", line.Text);
                upsert.Parameters.AddWithValue("$at", copy.At);
                upsert.Parameters.AddWithValue("$seen", Math.Max(copy.At, copy.LastSeen));
                upsert.Parameters.AddWithValue("$scan", copy.Id);
                upsert.Parameters.AddWithValue("$typed", line.Typed ? 1 : 0);
                upsert.ExecuteNonQuery();
            }
        }
    }

    internal static string LegacyPageKey(string processName, string windowTitle) =>
        $"legacy|{processName.Trim().ToLowerInvariant()}|{windowTitle.Trim().ToLowerInvariant()}";

    /// A task the user marked keeps its mark under the activity's new id.
    private void KeepUserTasks(Microsoft.Data.Sqlite.SqliteTransaction transaction, long now)
    {
        var marked = new List<(string Key, long Start, string Status)>();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT key, started_at_ms, task_status FROM activities WHERE task_by_user = 1;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                marked.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
            }
        }

        foreach (var (key, start, status) in marked)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT OR REPLACE INTO activity_tasks (activity_id, status, set_at_ms) VALUES ($id, $status, $at);";
            insert.Parameters.AddWithValue("$id", ActivityIds.Activity(key, start));
            insert.Parameters.AddWithValue("$status", status);
            insert.Parameters.AddWithValue("$at", now);
            insert.ExecuteNonQuery();
        }
    }

    /// A verdict the user gave a session keeps it under the session's new id.
    private void KeepUserOutcomes(Microsoft.Data.Sqlite.SqliteTransaction transaction, long now)
    {
        var marked = new List<(string FirstLook, string Outcome, long At)>();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT scan_ids_json, outcome, COALESCE(outcome_at_ms, 0) FROM activity_sessions WHERE outcome_source = 'User';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var first = SessionScanIds.Deserialize(reader.GetString(0)).FirstOrDefault();
                if (first is not null)
                {
                    marked.Add((first, reader.GetString(1), reader.GetInt64(2)));
                }
            }
        }

        foreach (var (firstLook, outcome, at) in marked)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT OR REPLACE INTO session_outcomes (session_id, outcome, set_at_ms) VALUES ($id, $outcome, $at);";
            insert.Parameters.AddWithValue("$id", ActivityIds.Session(firstLook));
            insert.Parameters.AddWithValue("$outcome", outcome);
            insert.Parameters.AddWithValue("$at", at == 0 ? now : at);
            insert.ExecuteNonQuery();
        }
    }

    // -----------------------------------------------------------------------
    // Versions 1–14: how older stores were built. Run only to bring an older
    // store (or a new, empty one) up to the point version 15 starts from.
    // -----------------------------------------------------------------------

    private void ApplyLegacyMigrations()
    {
        Execute(
            """
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
            VALUES (1, CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER));

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
            VALUES (2, CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER));
            """);

        EnsureColumn("manual_scans", "important_signals", "TEXT");
        EnsureColumn("manual_scans", "reminder_candidate", "TEXT");
        EnsureColumn("manual_scans", "gemma_context_characters", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("manual_scans", "redacted_uia_text", "TEXT");
        EnsureColumn("manual_scans", "redacted_ocr_text", "TEXT");
        EnsureColumn("manual_scans", "ocr_language", "TEXT");
        EnsureColumn("manual_scans", "session_id", "TEXT");
        Execute(
            """
            INSERT OR IGNORE INTO schema_version(version, applied_at_ms)
            VALUES (4, CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER));

            INSERT OR IGNORE INTO schema_version(version, applied_at_ms)
            VALUES (5, CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER));

            CREATE TABLE IF NOT EXISTS activity_sessions (
                id TEXT PRIMARY KEY,
                started_at_ms INTEGER NOT NULL,
                ended_at_ms INTEGER NOT NULL,
                process_name TEXT NOT NULL,
                window_title TEXT NOT NULL,
                scan_ids_json TEXT NOT NULL DEFAULT '[]',
                label TEXT,
                summary TEXT,
                status TEXT NOT NULL CHECK(status IN ('Active', 'Closed', 'OpenLoop')),
                important_signals TEXT,
                reminder_candidate TEXT,
                head_text TEXT NOT NULL DEFAULT '',
                tail_text TEXT NOT NULL DEFAULT '',
                is_minor INTEGER NOT NULL DEFAULT 0,
                outcome TEXT NOT NULL DEFAULT 'Unknown',
                outcome_source TEXT NOT NULL DEFAULT 'None',
                outcome_at_ms INTEGER,
                thread_id TEXT
            ) STRICT;

            INSERT OR IGNORE INTO schema_version(version, applied_at_ms)
            VALUES (6, CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER));

            INSERT OR IGNORE INTO schema_version(version, applied_at_ms)
            VALUES (7, CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER));
            """);

        EnsureColumn("activity_sessions", "is_minor", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("activity_sessions", "outcome", "TEXT NOT NULL DEFAULT 'Unknown'");
        EnsureColumn("activity_sessions", "outcome_source", "TEXT NOT NULL DEFAULT 'None'");
        EnsureColumn("activity_sessions", "outcome_at_ms", "INTEGER");
        EnsureColumn("activity_sessions", "thread_id", "TEXT");
        Execute(
            """
            INSERT OR IGNORE INTO schema_version(version, applied_at_ms)
            VALUES (8, CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER));

            CREATE TABLE IF NOT EXISTS activity_markers (
                id INTEGER PRIMARY KEY,
                ts_ms INTEGER NOT NULL,
                kind TEXT NOT NULL CHECK(kind IN (
                    'run.started', 'run.stopped', 'user.away', 'user.returned'))
            ) STRICT;

            CREATE INDEX IF NOT EXISTS idx_activity_markers_ts
            ON activity_markers(ts_ms);

            INSERT OR IGNORE INTO schema_version(version, applied_at_ms)
            VALUES (9, CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER));

            CREATE TABLE IF NOT EXISTS chat_threads (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                scope TEXT NOT NULL DEFAULT 'all',
                created_at_ms INTEGER NOT NULL,
                updated_at_ms INTEGER NOT NULL
            ) STRICT;

            CREATE INDEX IF NOT EXISTS idx_chat_threads_updated_at
            ON chat_threads(updated_at_ms DESC);

            CREATE TABLE IF NOT EXISTS chat_messages (
                id TEXT PRIMARY KEY,
                thread_id TEXT NOT NULL REFERENCES chat_threads(id) ON DELETE CASCADE,
                role TEXT NOT NULL CHECK(role IN ('user', 'agent', 'error')),
                text TEXT NOT NULL,
                citations_json TEXT NOT NULL DEFAULT '[]',
                scoped_count INTEGER NOT NULL DEFAULT 0,
                created_at_ms INTEGER NOT NULL
            ) STRICT;

            CREATE INDEX IF NOT EXISTS idx_chat_messages_thread
            ON chat_messages(thread_id, created_at_ms);

            INSERT OR IGNORE INTO schema_version(version, applied_at_ms)
            VALUES (10, CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER));
            """);

        EnsureColumn("manual_scans", "page_key", "TEXT");
        EnsureColumn("manual_scans", "app_name", "TEXT");
        EnsureColumn("manual_scans", "site", "TEXT");
        EnsureColumn("manual_scans", "subject", "TEXT");
        EnsureColumn("manual_scans", "phase", "TEXT");
        EnsureColumn("manual_scans", "mode", "TEXT");
        EnsureColumn("manual_scans", "category", "TEXT");
        EnsureColumn("manual_scans", "change_kind", "TEXT");
        EnsureColumn("manual_scans", "last_seen_ms", "INTEGER");
        EnsureColumn("manual_scans", "user_caused", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("manual_scans", "unsaved", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("manual_scans", "dialog_title", "TEXT");
        EnsureColumn("manual_scans", "event_kind", "TEXT");
        EnsureColumn("activity_sessions", "activities_built", "INTEGER NOT NULL DEFAULT 0");
        Execute(
            """
            CREATE INDEX IF NOT EXISTS idx_manual_scans_page_key
            ON manual_scans(page_key, captured_at_ms);

            -- Every look learns its app's title noise from recent titles.
            CREATE INDEX IF NOT EXISTS idx_manual_scans_process
            ON manual_scans(process_name, captured_at_ms);

            CREATE TABLE IF NOT EXISTS app_profiles (
                key TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                mode TEXT NOT NULL,
                category TEXT NOT NULL,
                source TEXT NOT NULL,
                samples INTEGER NOT NULL DEFAULT 0,
                sparse_samples INTEGER NOT NULL DEFAULT 0,
                updated_at_ms INTEGER NOT NULL
            ) STRICT;

            CREATE TABLE IF NOT EXISTS page_states (
                page_key TEXT PRIMARY KEY,
                signature TEXT,
                live_counts BLOB,
                signature_at_ms INTEGER NOT NULL,
                keyframe_at_ms INTEGER NOT NULL,
                updated_at_ms INTEGER NOT NULL,
                moving INTEGER NOT NULL DEFAULT 0
            ) STRICT;

            CREATE TABLE IF NOT EXISTS activities (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                key TEXT NOT NULL,
                app TEXT NOT NULL,
                site TEXT,
                subject TEXT NOT NULL,
                mode TEXT NOT NULL,
                category TEXT NOT NULL,
                started_at_ms INTEGER NOT NULL,
                ended_at_ms INTEGER NOT NULL,
                active_ms INTEGER NOT NULL,
                segments_json TEXT NOT NULL DEFAULT '[]',
                glances_json TEXT NOT NULL DEFAULT '[]',
                events_json TEXT NOT NULL DEFAULT '[]',
                phases_json TEXT NOT NULL DEFAULT '[]',
                scan_ids_json TEXT NOT NULL DEFAULT '[]',
                label TEXT NOT NULL,
                summary TEXT,
                task TEXT,
                task_status TEXT NOT NULL DEFAULT 'None',
                task_by_user INTEGER NOT NULL DEFAULT 0,
                check_state TEXT NOT NULL DEFAULT 'Rule',
                facts_kept INTEGER NOT NULL DEFAULT 0,
                facts_dropped INTEGER NOT NULL DEFAULT 0
            ) STRICT;

            INSERT OR IGNORE INTO schema_version(version, applied_at_ms)
            VALUES (11, CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER));
            INSERT OR IGNORE INTO schema_version(version, applied_at_ms)
            VALUES (12, CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER));
            INSERT OR IGNORE INTO schema_version(version, applied_at_ms)
            VALUES (13, CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER));
            """);

        if (!HasVersion(14))
        {
            MigrateToVersion14();
        }
    }

    /// <summary>
    /// Version 14: markers for locking, sleep, shutdown and closed apps (the
    /// old table only allowed four kinds), and summaries written while
    /// recording. Repairs a store left half migrated by an earlier attempt.
    /// </summary>
    private void MigrateToVersion14()
    {
        using var transaction = _connection.BeginTransaction(deferred: false);

        void Run(string sql)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        long Scalar(string sql)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        if (Scalar("SELECT EXISTS(SELECT 1 FROM schema_version WHERE version = 14);") != 0)
        {
            transaction.Commit();
            return;
        }

        var hasDetail = Scalar("SELECT COUNT(*) FROM pragma_table_info('activity_markers') WHERE name = 'detail';") != 0;
        if (!hasDetail)
        {
            Run(
                """
                DROP TABLE IF EXISTS activity_markers_v14;
                CREATE TABLE activity_markers_v14 (
                    id INTEGER PRIMARY KEY,
                    ts_ms INTEGER NOT NULL,
                    kind TEXT NOT NULL,
                    detail TEXT
                ) STRICT;
                INSERT INTO activity_markers_v14 (id, ts_ms, kind)
                    SELECT id, ts_ms, kind FROM activity_markers;
                DROP TABLE activity_markers;
                ALTER TABLE activity_markers_v14 RENAME TO activity_markers;
                """);
        }
        else
        {
            Run("DROP TABLE IF EXISTS activity_markers_v14;");
        }

        Run(
            """
            CREATE INDEX IF NOT EXISTS idx_activity_markers_ts ON activity_markers(ts_ms);

            CREATE TABLE IF NOT EXISTS early_narrations (
                activity_key TEXT NOT NULL,
                started_at_ms INTEGER NOT NULL,
                ended_at_ms INTEGER NOT NULL,
                label TEXT NOT NULL,
                summary TEXT,
                task TEXT,
                task_status TEXT NOT NULL,
                check_state TEXT NOT NULL,
                facts_kept INTEGER NOT NULL,
                facts_dropped INTEGER NOT NULL,
                created_at_ms INTEGER NOT NULL,
                PRIMARY KEY (activity_key, started_at_ms)
            ) STRICT;

            INSERT OR IGNORE INTO schema_version(version, applied_at_ms)
            VALUES (14, CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER));
            """);
        transaction.Commit();
    }

    private void EnsureColumn(string table, string column, string definition)
    {
        // Table and column names come only from the constants in this file,
        // never from input, so composing them into DDL is safe.
        using var query = _connection.CreateCommand();
        query.CommandText = $"SELECT EXISTS(SELECT 1 FROM pragma_table_info('{table}') WHERE name = $column);";
        query.Parameters.AddWithValue("$column", column);
        if (Convert.ToInt64(query.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1)
        {
            return;
        }

        try
        {
            Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition};");
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
            when (error.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Another opener added it between the check and the change.
        }
    }

    // -----------------------------------------------------------------------
    // Small key-value facts about the store itself.
    // -----------------------------------------------------------------------

    public string? GetMeta(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO app_meta (key, value) VALUES ($key, $value);";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }
}
