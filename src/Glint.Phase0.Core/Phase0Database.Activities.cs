using Microsoft.Data.Sqlite;

namespace Glint.Phase0.Core;

/// <summary>
/// Storage for the activity model: looks and the page lines they read, the
/// focus log, remembered app modes, picture-check state per page, and what
/// cannot be worked out again for free (summaries, the user's verdicts).
/// </summary>
public sealed partial class Phase0Database
{
    private const string FacetColumns =
        """
        id, captured_at_ms, COALESCE(last_seen_ms, captured_at_ms), process_name,
        window_title, page_key, app_name, site, subject, phase, mode, category,
        change_kind, unsaved, dialog_title, event_kind, user_caused, executable_path
        """;

    /// The longest a single look is ever extended (a game left running all
    /// day). Bounds the lookback when finding looks that reach into a window.
    private const long LongestLookMilliseconds = 43_200_000;

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
    /// catalog and learning never overwrite it. Activities are worked out when
    /// read, so it applies to all history at once.
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
            SELECT page_key, signature, live_counts, signature_at_ms, text_read_at_ms, updated_at_ms, moving
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
                (page_key, signature, live_counts, signature_at_ms, keyframe_at_ms, text_read_at_ms, updated_at_ms, moving)
            VALUES ($key, $signature, $counts, $signatureAt, 0, $textReadAt, $updatedAt, $moving)
            ON CONFLICT(page_key) DO UPDATE SET
                signature = excluded.signature,
                live_counts = excluded.live_counts,
                signature_at_ms = excluded.signature_at_ms,
                text_read_at_ms = excluded.text_read_at_ms,
                updated_at_ms = excluded.updated_at_ms,
                moving = excluded.moving;
            """;
        command.Parameters.AddWithValue("$key", state.PageKey);
        command.Parameters.AddWithValue("$signature", (object?)state.Signature?.ToBase64() ?? DBNull.Value);
        command.Parameters.AddWithValue("$counts", state.LiveCounts);
        command.Parameters.AddWithValue("$signatureAt", state.SignatureAtMilliseconds);
        command.Parameters.AddWithValue("$textReadAt", state.TextReadAtMilliseconds);
        command.Parameters.AddWithValue("$updatedAt", state.UpdatedAtMilliseconds);
        command.Parameters.AddWithValue("$moving", state.Moving ? 1 : 0);
        command.ExecuteNonQuery();
    }

    // -----------------------------------------------------------------------
    // Looks
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

    public void SaveLook(ManualScanRecord look, IReadOnlyList<PageLine>? newLines = null)
    {
        ArgumentNullException.ThrowIfNull(look);
        using var transaction = _connection.BeginTransaction();
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
                     dialog_title, event_kind, executable_path)
                VALUES
                    ($id, $capturedAt, $process, $title, $label, NULL,
                     $status, NULL, $hash, $model, $uia,
                     $ocr, $redactions, $captureMs, $ocrMs, 0,
                     $ocrLanguage, $pageKey, $appName, $site, $subject, $phase, $mode,
                     $category, $change, $lastSeen, $userCaused, $unsaved,
                     $dialogTitle, $eventKind, $executablePath);
                """;
            command.Parameters.AddWithValue("$id", look.Id);
            command.Parameters.AddWithValue("$capturedAt", look.CapturedAtMilliseconds);
            command.Parameters.AddWithValue("$process", look.ProcessName);
            command.Parameters.AddWithValue("$title", look.WindowTitle);
            command.Parameters.AddWithValue("$label", (object?)look.Label ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", look.Status.ToString());
            command.Parameters.AddWithValue("$hash", look.ContentHash);
            command.Parameters.AddWithValue("$model", look.ModelId);
            command.Parameters.AddWithValue("$uia", look.UiAutomationCharacters);
            command.Parameters.AddWithValue("$ocr", look.OcrCharacters);
            command.Parameters.AddWithValue("$redactions", look.Redactions);
            command.Parameters.AddWithValue("$captureMs", look.CaptureMilliseconds);
            command.Parameters.AddWithValue("$ocrMs", look.OcrMilliseconds);
            command.Parameters.AddWithValue("$ocrLanguage", (object?)look.OcrLanguage ?? DBNull.Value);
            command.Parameters.AddWithValue("$pageKey", (object?)look.PageKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$appName", (object?)look.AppName ?? DBNull.Value);
            command.Parameters.AddWithValue("$site", (object?)look.Site ?? DBNull.Value);
            command.Parameters.AddWithValue("$subject", (object?)look.Subject ?? DBNull.Value);
            command.Parameters.AddWithValue("$phase", (object?)look.Phase ?? DBNull.Value);
            command.Parameters.AddWithValue("$mode", (object?)look.Mode?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$category", (object?)look.Category?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$change", (object?)look.Change?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$lastSeen", (object?)look.LastSeenMilliseconds ?? DBNull.Value);
            command.Parameters.AddWithValue("$userCaused", look.UserCaused ? 1 : 0);
            command.Parameters.AddWithValue("$unsaved", look.Unsaved ? 1 : 0);
            command.Parameters.AddWithValue("$dialogTitle", (object?)look.DialogTitle ?? DBNull.Value);
            command.Parameters.AddWithValue("$eventKind", (object?)look.EventKind ?? DBNull.Value);
            command.Parameters.AddWithValue("$executablePath", (object?)look.ExecutablePath ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        if (newLines is { Count: > 0 } && look.PageKey is { } pageKey)
        {
            foreach (var line in newLines)
            {
                using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO content_chunks (page_key, line_key, text, first_seen_ms, last_seen_ms, scan_id, typed)
                    VALUES ($page, $key, $text, $at, $at, $scan, $typed)
                    ON CONFLICT(page_key, line_key) DO UPDATE SET last_seen_ms = MAX(content_chunks.last_seen_ms, excluded.last_seen_ms);
                    """;
                insert.Parameters.AddWithValue("$page", pageKey);
                insert.Parameters.AddWithValue("$key", line.Key);
                insert.Parameters.AddWithValue("$text", line.Text);
                insert.Parameters.AddWithValue("$at", look.CapturedAtMilliseconds);
                insert.Parameters.AddWithValue("$scan", look.Id);
                insert.Parameters.AddWithValue("$typed", line.Typed ? 1 : 0);
                insert.ExecuteNonQuery();
            }
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

    public IReadOnlyList<ScanFacet> GetFacetsBetween(long fromMilliseconds, long toMilliseconds)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {FacetColumns}
            FROM manual_scans
            WHERE captured_at_ms < $to
              AND captured_at_ms >= $earliest
              AND COALESCE(last_seen_ms, captured_at_ms) >= $from
            ORDER BY captured_at_ms ASC, id ASC;
            """;
        command.Parameters.AddWithValue("$from", fromMilliseconds);
        command.Parameters.AddWithValue("$to", toMilliseconds);
        command.Parameters.AddWithValue("$earliest", fromMilliseconds - LongestLookMilliseconds);
        using var reader = command.ExecuteReader();
        var facets = new List<ScanFacet>();
        while (reader.Read())
        {
            facets.Add(ReadFacet(reader));
        }

        return facets;
    }

    public long? GetLastLookEndBefore(long beforeMilliseconds)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT MAX(COALESCE(last_seen_ms, captured_at_ms))
            FROM manual_scans
            WHERE captured_at_ms < $before AND captured_at_ms >= $earliest
              AND COALESCE(last_seen_ms, captured_at_ms) < $before;
            """;
        command.Parameters.AddWithValue("$before", beforeMilliseconds);
        command.Parameters.AddWithValue("$earliest", beforeMilliseconds - 7 * 86_400_000L);
        return command.ExecuteScalar() is long value ? value : null;
    }

    /// <summary>
    /// The newest looks, newest first, each with the page lines it was the
    /// first to see as its text.
    /// </summary>
    public IReadOnlyList<ManualScanRecord> GetRecentLooks(int limit = 50)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var looks = new List<ManualScanRecord>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT id, captured_at_ms, process_name, window_title, label, summary, status, error,
                       content_hash, model_id, uia_characters, ocr_characters, redactions, capture_ms,
                       ocr_ms, inference_ms, ocr_language, page_key, app_name, site, subject, phase,
                       mode, category, change_kind, last_seen_ms, user_caused, unsaved, dialog_title,
                       event_kind, executable_path
                FROM manual_scans
                ORDER BY captured_at_ms DESC, id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
                looks.Add(new ManualScanRecord(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    Text(4),
                    Text(5),
                    Enum.TryParse<ManualScanStatus>(reader.GetString(6), out var status) ? status : ManualScanStatus.Completed,
                    Text(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetInt32(10),
                    reader.GetInt32(11),
                    reader.GetInt32(12),
                    reader.GetDouble(13),
                    reader.GetDouble(14),
                    reader.GetDouble(15),
                    OcrLanguage: Text(16),
                    PageKey: Text(17),
                    AppName: Text(18),
                    Site: Text(19),
                    Subject: Text(20),
                    Phase: Text(21),
                    Mode: Enum.TryParse<ActivityMode>(Text(22), out var mode) ? mode : null,
                    Category: Enum.TryParse<ActivityCategory>(Text(23), out var category) ? category : null,
                    Change: Enum.TryParse<CaptureChange>(Text(24), out var change) ? change : null,
                    LastSeenMilliseconds: reader.IsDBNull(25) ? null : reader.GetInt64(25),
                    UserCaused: !reader.IsDBNull(26) && reader.GetInt64(26) != 0,
                    Unsaved: !reader.IsDBNull(27) && reader.GetInt64(27) != 0,
                    DialogTitle: Text(28),
                    EventKind: Text(29),
                    ExecutablePath: Text(30)));
            }
        }

        if (looks.Count == 0)
        {
            return looks;
        }

        var texts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                $"SELECT scan_id, text FROM content_chunks WHERE scan_id IN ({BindIds(command, looks.Select(look => look.Id))}) ORDER BY id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (!texts.TryGetValue(id, out var lines))
                {
                    texts[id] = lines = [];
                }

                lines.Add(reader.GetString(1));
            }
        }

        return looks
            .Select(look => texts.TryGetValue(look.Id, out var lines)
                ? look with { RedactedInputText = string.Join(Environment.NewLine, lines) }
                : look)
            .ToList();
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
            Text(17));
    }

    // -----------------------------------------------------------------------
    // Page lines
    // -----------------------------------------------------------------------

    public IReadOnlySet<string> GetKnownLines(string pageKey, IReadOnlyCollection<string> lineKeys)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in lineKeys.Chunk(400))
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                $"SELECT line_key FROM content_chunks WHERE page_key = $page AND line_key IN ({BindIds(command, batch, "k")});";
            command.Parameters.AddWithValue("$page", pageKey);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                known.Add(reader.GetString(0));
            }
        }

        return known;
    }

    public void TouchLines(string pageKey, IReadOnlyCollection<string> lineKeys, long nowMilliseconds)
    {
        foreach (var batch in lineKeys.Chunk(400))
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                $"""
                UPDATE content_chunks SET last_seen_ms = MAX(last_seen_ms, $now)
                WHERE page_key = $page AND line_key IN ({BindIds(command, batch, "k")});
                """;
            command.Parameters.AddWithValue("$page", pageKey);
            command.Parameters.AddWithValue("$now", nowMilliseconds);
            command.ExecuteNonQuery();
        }
    }

    public void TouchLinesSeenAt(string pageKey, long readAtMilliseconds, long nowMilliseconds)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE content_chunks SET last_seen_ms = $now
            WHERE page_key = $page AND last_seen_ms = $readAt;
            """;
        command.Parameters.AddWithValue("$page", pageKey);
        command.Parameters.AddWithValue("$readAt", readAtMilliseconds);
        command.Parameters.AddWithValue("$now", nowMilliseconds);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<PageChunk> GetPageChunks(IReadOnlyCollection<string> pageKeys, long fromMilliseconds, long toMilliseconds)
    {
        if (pageKeys.Count == 0)
        {
            return [];
        }

        using var command = _connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT c.id, c.page_key, c.text, c.first_seen_ms, c.last_seen_ms, c.scan_id, c.typed,
                   COALESCE(m.user_caused, 0)
            FROM content_chunks AS c
            LEFT JOIN manual_scans AS m ON m.id = c.scan_id
            WHERE c.page_key IN ({BindIds(command, pageKeys, "p")})
              AND c.first_seen_ms <= $to AND c.last_seen_ms >= $from
            ORDER BY c.first_seen_ms ASC, c.id ASC;
            """;
        command.Parameters.AddWithValue("$from", fromMilliseconds);
        command.Parameters.AddWithValue("$to", toMilliseconds);
        using var reader = command.ExecuteReader();
        var chunks = new List<PageChunk>();
        while (reader.Read())
        {
            chunks.Add(new PageChunk(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt64(6) != 0,
                reader.GetInt64(7) != 0));
        }

        return chunks;
    }

    // -----------------------------------------------------------------------
    // Focus log
    // -----------------------------------------------------------------------

    public FocusRow? GetOpenFocus()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, started_at_ms, ended_at_ms, last_seen_ms, process_name, title, suppressed, suppressed_detail
            FROM focus_log WHERE ended_at_ms IS NULL
            ORDER BY started_at_ms DESC, id DESC LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadFocus(reader) : null;
    }

    /// <summary>
    /// A window came to the front at <paramref name="atMilliseconds"/>: the
    /// stretch before it ends there and this one begins.
    /// </summary>
    public FocusRow OpenFocus(long atMilliseconds, string processName, string? title, string? suppressed, string? suppressedDetail)
    {
        using var transaction = _connection.BeginTransaction();
        using (var close = _connection.CreateCommand())
        {
            close.Transaction = transaction;
            close.CommandText =
                """
                UPDATE focus_log SET ended_at_ms = MAX(started_at_ms, $at), last_seen_ms = MAX(last_seen_ms, $at)
                WHERE ended_at_ms IS NULL;
                """;
            close.Parameters.AddWithValue("$at", atMilliseconds);
            close.ExecuteNonQuery();
        }

        long id;
        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO focus_log (started_at_ms, ended_at_ms, last_seen_ms, process_name, title, suppressed, suppressed_detail)
                VALUES ($at, NULL, $at, $process, $title, $suppressed, $detail)
                RETURNING id;
                """;
            insert.Parameters.AddWithValue("$at", atMilliseconds);
            insert.Parameters.AddWithValue("$process", processName);
            insert.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
            insert.Parameters.AddWithValue("$suppressed", (object?)suppressed ?? DBNull.Value);
            insert.Parameters.AddWithValue("$detail", (object?)suppressedDetail ?? DBNull.Value);
            id = Convert.ToInt64(insert.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        transaction.Commit();
        return new FocusRow(id, atMilliseconds, null, atMilliseconds, processName, title, suppressed, suppressedDetail);
    }

    /// Ends whatever is in front at <paramref name="atMilliseconds"/>: the user left, locked, or recording stopped.
    public void CloseFocus(long atMilliseconds)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            UPDATE focus_log SET ended_at_ms = MAX(started_at_ms, $at), last_seen_ms = MAX(last_seen_ms, $at)
            WHERE ended_at_ms IS NULL;
            """;
        command.Parameters.AddWithValue("$at", atMilliseconds);
        command.ExecuteNonQuery();
    }

    /// The window in front is still in front.
    public void TouchFocus(long atMilliseconds)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "UPDATE focus_log SET last_seen_ms = MAX(last_seen_ms, $at) WHERE ended_at_ms IS NULL;";
        command.Parameters.AddWithValue("$at", atMilliseconds);
        command.ExecuteNonQuery();
    }

    /// After a crash nothing closed the last stretch: it ends where it was last seen.
    public void CloseDanglingFocus()
    {
        Execute("UPDATE focus_log SET ended_at_ms = last_seen_ms WHERE ended_at_ms IS NULL;");
    }

    /// Stretches in front at some point in [from, to), oldest first.
    public IReadOnlyList<FocusRow> GetFocusBetween(long fromMilliseconds, long toMilliseconds)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, started_at_ms, ended_at_ms, last_seen_ms, process_name, title, suppressed, suppressed_detail
            FROM focus_log
            WHERE started_at_ms < $to AND COALESCE(ended_at_ms, last_seen_ms) >= $from
            ORDER BY started_at_ms ASC, id ASC;
            """;
        command.Parameters.AddWithValue("$from", fromMilliseconds);
        command.Parameters.AddWithValue("$to", toMilliseconds);
        using var reader = command.ExecuteReader();
        var rows = new List<FocusRow>();
        while (reader.Read())
        {
            rows.Add(ReadFocus(reader));
        }

        return rows;
    }

    /// Adds stretches recorded elsewhere (the timeline files of earlier versions).
    public void ImportFocus(IReadOnlyList<FocusRow> rows)
    {
        using var transaction = _connection.BeginTransaction();
        foreach (var row in rows)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO focus_log (started_at_ms, ended_at_ms, last_seen_ms, process_name, title, suppressed, suppressed_detail)
                VALUES ($start, $end, $seen, $process, $title, $suppressed, $detail);
                """;
            insert.Parameters.AddWithValue("$start", row.StartedAtMilliseconds);
            insert.Parameters.AddWithValue("$end", (object?)row.EndedAtMilliseconds ?? row.LastSeenMilliseconds);
            insert.Parameters.AddWithValue("$seen", row.LastSeenMilliseconds);
            insert.Parameters.AddWithValue("$process", row.ProcessName);
            insert.Parameters.AddWithValue("$title", (object?)row.Title ?? DBNull.Value);
            insert.Parameters.AddWithValue("$suppressed", (object?)row.Suppressed ?? DBNull.Value);
            insert.Parameters.AddWithValue("$detail", (object?)row.SuppressedDetail ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static FocusRow ReadFocus(System.Data.Common.DbDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));

    // -----------------------------------------------------------------------
    // What cannot be worked out again: summaries and the user's verdicts
    // -----------------------------------------------------------------------

    public IReadOnlyList<StoredSummary> GetSummaries(IReadOnlyCollection<string> activityKeys)
    {
        var summaries = new List<StoredSummary>();
        foreach (var batch in activityKeys.Distinct(StringComparer.Ordinal).Chunk(400))
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT activity_key, started_at_ms, text_hash, label, summary, task, check_state, facts_kept, facts_dropped
                FROM activity_summaries
                WHERE activity_key IN ({BindIds(command, batch, "k")});
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                summaries.Add(new StoredSummary(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    Enum.TryParse<SummaryCheck>(reader.GetString(6), out var check) ? check : SummaryCheck.Rule,
                    reader.GetInt32(7),
                    reader.GetInt32(8)));
            }
        }

        return summaries;
    }

    public void SaveSummary(StoredSummary summary, long nowMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(summary);
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR REPLACE INTO activity_summaries
                (activity_key, started_at_ms, text_hash, label, summary, task, check_state, facts_kept, facts_dropped, created_at_ms)
            VALUES ($key, $start, $hash, $label, $summary, $task, $check, $kept, $dropped, $now);
            """;
        command.Parameters.AddWithValue("$key", summary.ActivityKey);
        command.Parameters.AddWithValue("$start", summary.StartedAtMilliseconds);
        command.Parameters.AddWithValue("$hash", (object?)summary.TextHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$label", summary.Label);
        command.Parameters.AddWithValue("$summary", (object?)summary.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$task", (object?)summary.Task ?? DBNull.Value);
        command.Parameters.AddWithValue("$check", summary.Check.ToString());
        command.Parameters.AddWithValue("$kept", summary.FactsKept);
        command.Parameters.AddWithValue("$dropped", summary.FactsDropped);
        command.Parameters.AddWithValue("$now", nowMilliseconds);
        command.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, ActivityTaskStatus> GetTaskStatuses(IReadOnlyCollection<string> activityIds)
    {
        var statuses = new Dictionary<string, ActivityTaskStatus>(StringComparer.Ordinal);
        foreach (var batch in activityIds.Chunk(400))
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                $"SELECT activity_id, status FROM activity_tasks WHERE activity_id IN ({BindIds(command, batch)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (Enum.TryParse<ActivityTaskStatus>(reader.GetString(1), out var status))
                {
                    statuses[reader.GetString(0)] = status;
                }
            }
        }

        return statuses;
    }

    /// The user's verdict on an activity's task. Absolute: no rule rewrites it.
    public void SetActivityTaskStatus(string activityId, ActivityTaskStatus status, long atMilliseconds)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "INSERT OR REPLACE INTO activity_tasks (activity_id, status, set_at_ms) VALUES ($id, $status, $at);";
        command.Parameters.AddWithValue("$id", activityId);
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$at", atMilliseconds);
        command.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, SessionOutcome> GetSessionOutcomes(IReadOnlyCollection<string> sessionIds)
    {
        var outcomes = new Dictionary<string, SessionOutcome>(StringComparer.Ordinal);
        foreach (var batch in sessionIds.Chunk(400))
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                $"SELECT session_id, outcome FROM session_outcomes WHERE session_id IN ({BindIds(command, batch)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (Enum.TryParse<SessionOutcome>(reader.GetString(1), out var outcome))
                {
                    outcomes[reader.GetString(0)] = outcome;
                }
            }
        }

        return outcomes;
    }

    /// The user's own verdict on a session. Absolute: no rule overwrites it.
    public void SetSessionOutcome(string sessionId, SessionOutcome outcome, long atMilliseconds)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "INSERT OR REPLACE INTO session_outcomes (session_id, outcome, set_at_ms) VALUES ($id, $outcome, $at);";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$outcome", outcome.ToString());
        command.Parameters.AddWithValue("$at", atMilliseconds);
        command.ExecuteNonQuery();
    }
}
