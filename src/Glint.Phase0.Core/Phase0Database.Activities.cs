using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Glint.Phase0.Core;

/// <summary>
/// Storage for the activity model: per-look facets on captures, remembered
/// app modes, picture-check state per page, and the activities themselves.
/// Everything lives in the same encrypted store as the captures.
/// </summary>
public sealed partial class Phase0Database
{
    private static readonly JsonSerializerOptions ActivityJson = new(JsonSerializerDefaults.Web);

    private const string SessionColumns =
        """
        id, started_at_ms, ended_at_ms, process_name, window_title,
        scan_ids_json, label, summary, status, important_signals,
        reminder_candidate, head_text, tail_text, is_minor,
        outcome, outcome_source, outcome_at_ms, thread_id
        """;

    private const string FacetColumns =
        """
        id, captured_at_ms, COALESCE(last_seen_ms, captured_at_ms), process_name,
        window_title, page_key, app_name, site, subject, phase, mode, category,
        change_kind, unsaved, dialog_title, event_kind, user_caused,
        (SELECT r.executable_path FROM raw_events AS r WHERE r.content_hash = manual_scans.content_hash),
        session_id
        """;

    private const string ActivityColumns =
        """
        id, session_id, key, app, site, subject, mode, category, started_at_ms,
        ended_at_ms, active_ms, segments_json, glances_json, events_json,
        phases_json, scan_ids_json, label, summary, task, task_status,
        task_by_user, check_state, facts_kept, facts_dropped
        """;

    private void ApplyActivityMigrations()
    {
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

            CREATE INDEX IF NOT EXISTS idx_activities_session
            ON activities(session_id);

            CREATE INDEX IF NOT EXISTS idx_activities_started
            ON activities(started_at_ms DESC);

            CREATE INDEX IF NOT EXISTS idx_activities_check
            ON activities(check_state);

            INSERT OR IGNORE INTO schema_version(version, applied_at_ms)
            VALUES (11, CAST(unixepoch('subsec') * 1000 AS INTEGER));
            """);

        // Version 12: sessions and activities span each record to when it
        // was last seen, not when it started. Rebuild everything built
        // before that once; summaries already written are kept.
        using var version = _connection.CreateCommand();
        version.CommandText = "SELECT EXISTS(SELECT 1 FROM schema_version WHERE version = 12);";
        if (Convert.ToInt64(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            Execute(
                """
                UPDATE activity_sessions SET activities_built = 0 WHERE status = 'Closed';
                INSERT OR IGNORE INTO schema_version(version, applied_at_ms)
                VALUES (12, CAST(unixepoch('subsec') * 1000 AS INTEGER));
                """);
        }
    }

    private void EnsureColumn(string table, string column, string definition)
    {
        // Table and column names come only from the constants above, never
        // from input, so composing them into DDL is safe.
        using var query = _connection.CreateCommand();
        query.CommandText = $"SELECT EXISTS(SELECT 1 FROM pragma_table_info('{table}') WHERE name = $column);";
        query.Parameters.AddWithValue("$column", column);
        if (Convert.ToInt64(query.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1)
        {
            return;
        }

        Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition};");
    }

    // -----------------------------------------------------------------------
    // App profiles
    // -----------------------------------------------------------------------

    public AppProfile? GetAppProfile(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT key, display_name, mode, category, source, samples, sparse_samples, updated_at_ms
            FROM app_profiles WHERE key = $key;
            """;
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProfile(reader) : null;
    }

    public IReadOnlyList<AppProfile> GetAppProfiles()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT key, display_name, mode, category, source, samples, sparse_samples, updated_at_ms
            FROM app_profiles ORDER BY updated_at_ms DESC;
            """;
        using var reader = command.ExecuteReader();
        var profiles = new List<AppProfile>();
        while (reader.Read())
        {
            profiles.Add(ReadProfile(reader));
        }

        return profiles;
    }

    public void UpsertAppProfile(AppProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO app_profiles
                (key, display_name, mode, category, source, samples, sparse_samples, updated_at_ms)
            VALUES ($key, $name, $mode, $category, $source, $samples, $sparse, $at)
            ON CONFLICT(key) DO UPDATE SET
                display_name = excluded.display_name,
                mode = excluded.mode,
                category = excluded.category,
                source = excluded.source,
                samples = excluded.samples,
                sparse_samples = excluded.sparse_samples,
                updated_at_ms = excluded.updated_at_ms;
            """;
        command.Parameters.AddWithValue("$key", profile.Key);
        command.Parameters.AddWithValue("$name", profile.DisplayName);
        command.Parameters.AddWithValue("$mode", profile.Mode.ToString());
        command.Parameters.AddWithValue("$category", profile.Category.ToString());
        command.Parameters.AddWithValue("$source", profile.Source.ToString());
        command.Parameters.AddWithValue("$samples", profile.Samples);
        command.Parameters.AddWithValue("$sparse", profile.SparseSamples);
        command.Parameters.AddWithValue("$at", profile.UpdatedAtMilliseconds);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The user's correction of how an app or site is treated. Absolute: the
    /// catalog and learning never overwrite it. Sessions already built are
    /// rebuilt so the correction shows on the timeline straight away.
    /// </summary>
    public AppProfile SetAppModeByUser(string key, ActivityMode mode, ActivityCategory? category, long atMilliseconds)
    {
        var existing = GetAppProfile(key);
        var profile = new AppProfile(
            key,
            existing?.DisplayName ?? key[(key.IndexOf(':') + 1)..],
            mode,
            category ?? existing?.Category ?? ActivityCategory.Other,
            ModeSource.User,
            existing?.Samples ?? 0,
            existing?.SparseSamples ?? 0,
            atMilliseconds);
        UpsertAppProfile(profile);
        Execute("UPDATE activity_sessions SET activities_built = 0 WHERE status = 'Closed';");
        return profile;
    }

    private static AppProfile ReadProfile(System.Data.Common.DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            Enum.TryParse<ActivityMode>(reader.GetString(2), out var mode) ? mode : ActivityMode.Read,
            Enum.TryParse<ActivityCategory>(reader.GetString(3), out var category) ? category : ActivityCategory.Other,
            Enum.TryParse<ModeSource>(reader.GetString(4), out var source) ? source : ModeSource.Provisional,
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt64(7));

    // -----------------------------------------------------------------------
    // Picture-check state
    // -----------------------------------------------------------------------

    public PageState? GetPageState(string pageKey)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT page_key, signature, live_counts, signature_at_ms, keyframe_at_ms, updated_at_ms, moving
            FROM page_states WHERE page_key = $key;
            """;
        command.Parameters.AddWithValue("$key", pageKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var counts = reader.IsDBNull(2) ? new byte[FrameSignature.CellCount] : (byte[])reader.GetValue(2);
        return new PageState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : FrameSignature.TryParse(reader.GetString(1)),
            counts.Length == FrameSignature.CellCount ? counts : new byte[FrameSignature.CellCount],
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6) != 0);
    }

    public void UpsertPageState(PageState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO page_states
                (page_key, signature, live_counts, signature_at_ms, keyframe_at_ms, updated_at_ms, moving)
            VALUES ($key, $signature, $counts, $signatureAt, $keyframeAt, $updatedAt, $moving)
            ON CONFLICT(page_key) DO UPDATE SET
                signature = excluded.signature,
                live_counts = excluded.live_counts,
                signature_at_ms = excluded.signature_at_ms,
                keyframe_at_ms = excluded.keyframe_at_ms,
                updated_at_ms = excluded.updated_at_ms,
                moving = excluded.moving;
            """;
        command.Parameters.AddWithValue("$key", state.PageKey);
        command.Parameters.AddWithValue("$signature", (object?)state.Signature?.ToBase64() ?? DBNull.Value);
        command.Parameters.AddWithValue("$counts", state.LiveCounts);
        command.Parameters.AddWithValue("$signatureAt", state.SignatureAtMilliseconds);
        command.Parameters.AddWithValue("$keyframeAt", state.KeyframeAtMilliseconds);
        command.Parameters.AddWithValue("$updatedAt", state.UpdatedAtMilliseconds);
        command.Parameters.AddWithValue("$moving", state.Moving ? 1 : 0);
        command.ExecuteNonQuery();
    }

    // -----------------------------------------------------------------------
    // Facets on captures
    // -----------------------------------------------------------------------

    public ScanFacet? GetLatestFacet()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {FacetColumns}
            FROM manual_scans
            ORDER BY captured_at_ms DESC, id DESC
            LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadFacet(reader) : null;
    }

    public IReadOnlyList<string> GetRecentTitles(string processName, int limit = 40)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT window_title
            FROM manual_scans
            WHERE process_name = $process AND window_title <> ''
            GROUP BY window_title
            ORDER BY MAX(captured_at_ms) DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$process", processName);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        using var reader = command.ExecuteReader();
        var titles = new List<string>();
        while (reader.Read())
        {
            titles.Add(reader.GetString(0));
        }

        return titles;
    }

    public IReadOnlyList<string> GetPageBasis(string pageKey, int limit = 60)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.text
            FROM manual_scans AS m
            JOIN raw_events_fts AS f ON f.content_hash = m.content_hash
            WHERE m.page_key = $key
              AND m.session_id IS NULL
              AND m.change_kind IN ('Keyframe', 'Delta')
              AND m.captured_at_ms >= COALESCE((
                    SELECT MAX(captured_at_ms) FROM manual_scans
                    WHERE page_key = $key AND change_kind = 'Keyframe' AND session_id IS NULL), 0)
            ORDER BY m.captured_at_ms ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$key", pageKey);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        using var reader = command.ExecuteReader();
        var texts = new List<string>();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                texts.Add(reader.GetString(0));
            }
        }

        return texts;
    }

    public void SaveFacetScan(RawCaptureEvent? captureEvent, ManualScanRecord scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        using var transaction = _connection.BeginTransaction();
        if (captureEvent is not null)
        {
            int inserted;
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
                command.Parameters.AddWithValue("$path", (object?)captureEvent.ExecutablePath ?? DBNull.Value);
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
                     ocr_language, page_key, app_name, site, subject, phase, mode,
                     category, change_kind, last_seen_ms, user_caused, unsaved,
                     dialog_title, event_kind)
                VALUES
                    ($id, $capturedAt, $process, $title, $label, NULL,
                     $status, NULL, $hash, $model, $uia,
                     $ocr, $redactions, $captureMs, $ocrMs, 0,
                     $ocrLanguage, $pageKey, $appName, $site, $subject, $phase, $mode,
                     $category, $change, $lastSeen, $userCaused, $unsaved,
                     $dialogTitle, $eventKind);
                """;
            command.Parameters.AddWithValue("$id", scan.Id);
            command.Parameters.AddWithValue("$capturedAt", scan.CapturedAtMilliseconds);
            command.Parameters.AddWithValue("$process", scan.ProcessName);
            command.Parameters.AddWithValue("$title", scan.WindowTitle);
            command.Parameters.AddWithValue("$label", (object?)scan.Label ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", scan.Status.ToString());
            command.Parameters.AddWithValue("$hash", scan.ContentHash);
            command.Parameters.AddWithValue("$model", scan.ModelId);
            command.Parameters.AddWithValue("$uia", scan.UiAutomationCharacters);
            command.Parameters.AddWithValue("$ocr", scan.OcrCharacters);
            command.Parameters.AddWithValue("$redactions", scan.Redactions);
            command.Parameters.AddWithValue("$captureMs", scan.CaptureMilliseconds);
            command.Parameters.AddWithValue("$ocrMs", scan.OcrMilliseconds);
            command.Parameters.AddWithValue("$ocrLanguage", (object?)scan.OcrLanguage ?? DBNull.Value);
            command.Parameters.AddWithValue("$pageKey", (object?)scan.PageKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$appName", (object?)scan.AppName ?? DBNull.Value);
            command.Parameters.AddWithValue("$site", (object?)scan.Site ?? DBNull.Value);
            command.Parameters.AddWithValue("$subject", (object?)scan.Subject ?? DBNull.Value);
            command.Parameters.AddWithValue("$phase", (object?)scan.Phase ?? DBNull.Value);
            command.Parameters.AddWithValue("$mode", (object?)scan.Mode?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$category", (object?)scan.Category?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$change", (object?)scan.Change?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$lastSeen", (object?)scan.LastSeenMilliseconds ?? DBNull.Value);
            command.Parameters.AddWithValue("$userCaused", scan.UserCaused ? 1 : 0);
            command.Parameters.AddWithValue("$unsaved", scan.Unsaved ? 1 : 0);
            command.Parameters.AddWithValue("$dialogTitle", (object?)scan.DialogTitle ?? DBNull.Value);
            command.Parameters.AddWithValue("$eventKind", (object?)scan.EventKind ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void TouchScan(string scanId, long lastSeenMilliseconds)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE manual_scans
            SET last_seen_ms = MAX(COALESCE(last_seen_ms, captured_at_ms), $at)
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", scanId);
        command.Parameters.AddWithValue("$at", lastSeenMilliseconds);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ScanFacet> GetScanFacets(IReadOnlyList<string> scanIds)
    {
        ArgumentNullException.ThrowIfNull(scanIds);
        var facets = new List<ScanFacet>();
        foreach (var chunk in scanIds.Chunk(400))
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT {FacetColumns}
                FROM manual_scans
                WHERE id IN ({BindIds(command, chunk)})
                ORDER BY captured_at_ms ASC, id ASC;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                facets.Add(ReadFacet(reader));
            }
        }

        return facets.OrderBy(facet => facet.CapturedAtMilliseconds).ThenBy(facet => facet.Id, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<ScanText> GetScanTexts(IReadOnlyList<string> scanIds)
    {
        ArgumentNullException.ThrowIfNull(scanIds);
        var texts = new List<ScanText>();
        foreach (var chunk in scanIds.Chunk(400))
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT m.id, m.captured_at_ms, m.change_kind, m.user_caused, f.text
                FROM manual_scans AS m
                JOIN raw_events_fts AS f ON f.content_hash = m.content_hash
                WHERE m.id IN ({BindIds(command, chunk)})
                  AND (m.change_kind IS NULL OR m.change_kind IN ('Keyframe', 'Delta'));
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(4))
                {
                    continue;
                }

                texts.Add(new ScanText(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    !reader.IsDBNull(2) && Enum.TryParse<CaptureChange>(reader.GetString(2), out var change)
                        ? change
                        : CaptureChange.Keyframe,
                    !reader.IsDBNull(3) && reader.GetInt64(3) != 0,
                    reader.GetString(4)));
            }
        }

        return texts.OrderBy(text => text.CapturedAtMilliseconds).ToList();
    }

    private static string BindIds(SqliteCommand command, IReadOnlyList<string> ids)
    {
        var names = new List<string>(ids.Count);
        for (var index = 0; index < ids.Count; index++)
        {
            var name = $"$id{index}";
            names.Add(name);
            command.Parameters.AddWithValue(name, ids[index]);
        }

        return string.Join(", ", names);
    }

    private static ScanFacet ReadFacet(System.Data.Common.DbDataReader reader)
    {
        string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        return new ScanFacet(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetString(4),
            Text(5),
            Text(6),
            Text(7),
            Text(8),
            Text(9),
            Enum.TryParse<ActivityMode>(Text(10), out var mode) ? mode : null,
            Enum.TryParse<ActivityCategory>(Text(11), out var category) ? category : null,
            Enum.TryParse<CaptureChange>(Text(12), out var change) ? change : null,
            !reader.IsDBNull(13) && reader.GetInt64(13) != 0,
            Text(14),
            Text(15),
            !reader.IsDBNull(16) && reader.GetInt64(16) != 0,
            Text(17),
            Text(18));
    }

    // -----------------------------------------------------------------------
    // Activities
    // -----------------------------------------------------------------------

    public IReadOnlyList<ActivitySession> GetSessionsWithoutActivities(int limit = 50)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {SessionColumns}
            FROM activity_sessions
            WHERE activities_built = 0 AND status = 'Closed'
            ORDER BY started_at_ms ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        using var reader = command.ExecuteReader();
        var sessions = new List<ActivitySession>();
        while (reader.Read())
        {
            sessions.Add(ReadSession(reader));
        }

        return sessions;
    }

    public ActivitySession? GetSession(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT {SessionColumns} FROM activity_sessions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    /// <summary>
    /// Writes a session's activities and its rolled-up label and summary in
    /// one transaction. A user's verdict on an activity survives a rebuild
    /// when the same activity comes back out of the segmenter.
    /// </summary>
    public void ReplaceSessionActivities(ActivitySession session, IReadOnlyList<ActivityRecord> activities)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(activities);
        // Segmentation is deterministic, so a rebuild yields the same key and
        // start for the same activity; anything else is genuinely new.
        var previous = new Dictionary<string, ActivityRecord>(StringComparer.Ordinal);
        foreach (var old in GetSessionActivities(session.Id))
        {
            previous.TryAdd($"{old.Key}@{old.StartedAtMilliseconds}", old);
        }

        using var transaction = _connection.BeginTransaction();
        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM activities WHERE session_id = $session;";
            delete.Parameters.AddWithValue("$session", session.Id);
            delete.ExecuteNonQuery();
        }

        foreach (var activity in activities)
        {
            var kept = activity;
            if (previous.TryGetValue($"{activity.Key}@{activity.StartedAtMilliseconds}", out var old))
            {
                kept = kept with { Id = old.Id };
                // Keep a summary already paid for, and anything the user said.
                if (old.Check is SummaryCheck.Verified or SummaryCheck.Partial && kept.Check == SummaryCheck.Pending)
                {
                    kept = kept with
                    {
                        Label = old.Label,
                        Summary = old.Summary,
                        Task = old.Task,
                        TaskStatus = old.TaskStatus,
                        Check = old.Check,
                        FactsKept = old.FactsKept,
                        FactsDropped = old.FactsDropped
                    };
                }

                if (old.TaskSetByUser)
                {
                    kept = kept with { Task = old.Task, TaskStatus = old.TaskStatus, TaskSetByUser = true };
                }
            }

            WriteActivity(kept, transaction);
        }

        UpsertSession(session, transaction);
        using (var mark = _connection.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = "UPDATE activity_sessions SET activities_built = 1 WHERE id = $id;";
            mark.Parameters.AddWithValue("$id", session.Id);
            mark.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<ActivityRecord> GetActivitiesPendingNarration(int limit = 10)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {ActivityColumns}
            FROM activities
            WHERE check_state = 'Pending'
            ORDER BY started_at_ms DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        return ReadActivities(command);
    }

    public IReadOnlyList<ActivityRecord> GetSessionActivities(string sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {ActivityColumns}
            FROM activities
            WHERE session_id = $session
            ORDER BY started_at_ms ASC;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        return ReadActivities(command);
    }

    public IReadOnlyList<ActivityRecord> GetRecentActivities(int limit = 200)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {ActivityColumns}
            FROM activities
            ORDER BY started_at_ms DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1_000));
        return ReadActivities(command);
    }

    /// Activities overlapping [from, to), oldest first.
    public IReadOnlyList<ActivityRecord> GetActivitiesBetween(long fromMilliseconds, long toMilliseconds)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {ActivityColumns}
            FROM activities
            WHERE started_at_ms < $to AND ended_at_ms >= $from
            ORDER BY started_at_ms ASC
            LIMIT 3000;
            """;
        command.Parameters.AddWithValue("$from", fromMilliseconds);
        command.Parameters.AddWithValue("$to", toMilliseconds);
        return ReadActivities(command);
    }

    public ActivityRecord? GetActivity(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT {ActivityColumns} FROM activities WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return ReadActivities(command).FirstOrDefault();
    }

    public void UpdateActivity(ActivityRecord activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        WriteActivity(activity, null);
    }

    /// The user's verdict on an activity's task. Absolute: no rule rewrites it.
    public ActivityRecord? SetActivityTaskStatus(string id, ActivityTaskStatus status)
    {
        var activity = GetActivity(id);
        if (activity is null)
        {
            return null;
        }

        var updated = activity with { TaskStatus = status, TaskSetByUser = true };
        WriteActivity(updated, null);
        return updated;
    }

    private void WriteActivity(ActivityRecord activity, SqliteTransaction? transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            INSERT OR REPLACE INTO activities ({ActivityColumns})
            VALUES ($id, $session, $key, $app, $site, $subject, $mode, $category, $started,
                    $ended, $active, $segments, $glances, $events, $phases, $scans, $label,
                    $summary, $task, $taskStatus, $taskByUser, $check, $kept, $dropped);
            """;
        command.Parameters.AddWithValue("$id", activity.Id);
        command.Parameters.AddWithValue("$session", activity.SessionId);
        command.Parameters.AddWithValue("$key", activity.Key);
        command.Parameters.AddWithValue("$app", activity.App);
        command.Parameters.AddWithValue("$site", (object?)activity.Site ?? DBNull.Value);
        command.Parameters.AddWithValue("$subject", activity.Subject);
        command.Parameters.AddWithValue("$mode", activity.Mode.ToString());
        command.Parameters.AddWithValue("$category", activity.Category.ToString());
        command.Parameters.AddWithValue("$started", activity.StartedAtMilliseconds);
        command.Parameters.AddWithValue("$ended", activity.EndedAtMilliseconds);
        command.Parameters.AddWithValue("$active", activity.ActiveMilliseconds);
        command.Parameters.AddWithValue("$segments", JsonSerializer.Serialize(activity.Segments, ActivityJson));
        command.Parameters.AddWithValue("$glances", JsonSerializer.Serialize(activity.Glances, ActivityJson));
        command.Parameters.AddWithValue("$events", JsonSerializer.Serialize(activity.Events, ActivityJson));
        command.Parameters.AddWithValue("$phases", JsonSerializer.Serialize(activity.Phases, ActivityJson));
        command.Parameters.AddWithValue("$scans", JsonSerializer.Serialize(activity.ScanIds, ActivityJson));
        command.Parameters.AddWithValue("$label", activity.Label);
        command.Parameters.AddWithValue("$summary", (object?)activity.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$task", (object?)activity.Task ?? DBNull.Value);
        command.Parameters.AddWithValue("$taskStatus", activity.TaskStatus.ToString());
        command.Parameters.AddWithValue("$taskByUser", activity.TaskSetByUser ? 1 : 0);
        command.Parameters.AddWithValue("$check", activity.Check.ToString());
        command.Parameters.AddWithValue("$kept", activity.FactsKept);
        command.Parameters.AddWithValue("$dropped", activity.FactsDropped);
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<ActivityRecord> ReadActivities(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var activities = new List<ActivityRecord>();
        while (reader.Read())
        {
            string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
            T Json<T>(int index) where T : class =>
                JsonSerializer.Deserialize<T>(Text(index) ?? "[]", ActivityJson)
                ?? throw new InvalidDataException("Activity column is not valid JSON.");
            activities.Add(new ActivityRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                Text(4),
                reader.GetString(5),
                Enum.TryParse<ActivityMode>(reader.GetString(6), out var mode) ? mode : ActivityMode.Read,
                Enum.TryParse<ActivityCategory>(reader.GetString(7), out var category) ? category : ActivityCategory.Other,
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                Json<List<ActivitySegment>>(11),
                Json<List<ActivityGlance>>(12),
                Json<List<ActivityEvent>>(13),
                Json<List<string>>(14),
                Json<List<string>>(15),
                reader.GetString(16),
                Text(17),
                Text(18),
                Enum.TryParse<ActivityTaskStatus>(reader.GetString(19), out var taskStatus) ? taskStatus : ActivityTaskStatus.None,
                reader.GetInt64(20) != 0,
                Enum.TryParse<SummaryCheck>(reader.GetString(21), out var check) ? check : SummaryCheck.Rule,
                reader.GetInt32(22),
                reader.GetInt32(23)));
        }

        return activities;
    }
}
