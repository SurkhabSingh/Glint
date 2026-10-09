using Glint.Phase0.Core;
using Microsoft.Data.Sqlite;

namespace Glint.Phase0.Tests;

/// <summary>
/// Version 15 replaces everything derived and stored (sessions, activities,
/// early summaries, full-copy and new-line text records) with what it was
/// derived from. An older store must come through with its text, its
/// summaries and the user's own verdicts intact, and a copy of how it was.
/// </summary>
public sealed class MigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "glint-phase0-tests",
        Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "memory.db");

    private DpapiKeyStore KeyStore => new(Path.Combine(_directory, "key.bin"));

    private Phase0Database Open() => Phase0Database.Open(DatabasePath, KeyStore);

    [Fact]
    public void AVersion14StoreKeepsItsTextSummariesAndVerdicts()
    {
        CreateVersion14Store();

        using (var database = Open())
        {
            // Text: every line of the old copies, once per page.
            var chunks = database.GetPageChunks(["chrome|mail.example.com|inbox"], 0, long.MaxValue);
            Assert.Equal(
                ["Meeting moved to Friday", "Please send the deck", "Thanks, will do"],
                chunks.Select(chunk => chunk.Text).OrderBy(text => text, StringComparer.Ordinal));
            Assert.Equal("look-2", chunks.Single(chunk => chunk.Text == "Thanks, will do").ScanId);
            Assert.All(chunks.Where(chunk => chunk.Text != "Thanks, will do"), chunk => Assert.Equal("look-1", chunk.ScanId));

            // A capture from before pages had keys gets one from its window.
            var legacy = database.GetPageChunks([Phase0Database.LegacyPageKey("notepad", "notes.txt")], 0, long.MaxValue);
            Assert.Equal(["Groceries for the weekend"], legacy.Select(chunk => chunk.Text));

            // Summaries and the user's own verdicts survive under the new ids.
            var summary = Assert.Single(database.GetSummaries(["chrome|mail.example.com|inbox"]));
            Assert.Equal(SummaryCheck.Verified, summary.Check);
            Assert.Equal("Read the deck request", summary.Label);
            var activityId = ActivityIds.Activity("chrome|mail.example.com|inbox", 1_000);
            Assert.Equal(ActivityTaskStatus.Done, database.GetTaskStatuses([activityId])[activityId]);
            var sessionId = ActivityIds.Session("look-1");
            Assert.Equal(SessionOutcome.Settled, database.GetSessionOutcomes([sessionId])[sessionId]);
        }

        Assert.True(File.Exists(DatabasePath + ".pre-v15.bak"), "the store is copied before it is upgraded");
        using var raw = RawConnection();
        foreach (var table in new[] { "activities", "activity_sessions", "early_narrations", "raw_events", "raw_events_fts" })
        {
            using var command = raw.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE name = '{table}';";
            Assert.Equal(0L, command.ExecuteScalar());
        }
    }

    [Fact]
    public void ANewStoreStartsAtTheCurrentVersionWithoutABackup()
    {
        using (var database = Open())
        {
            database.RecordMarker("run.started", 1_000);
            Assert.Equal(1_000, database.GetMarkers(0, 2_000).Single().TimestampMilliseconds);
        }

        Assert.False(File.Exists(DatabasePath + ".pre-v15.bak"));
    }

    [Fact]
    public async Task ProcessesOpeningAtOnceAllSucceed()
    {
        CreateVersion14Store();
        var opens = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            using var database = Open();
            return database.GetSummaries(["chrome|mail.example.com|inbox"]).Count;
        }));
        var results = await Task.WhenAll(opens);

        Assert.All(results, count => Assert.Equal(1, count));
    }

    [Fact]
    public void AReaderNeverChangesTheStore()
    {
        using (Open())
        {
        }

        using var reader = Phase0Database.Open(DatabasePath, KeyStore, readOnly: true);
        Assert.Throws<SqliteException>(() => reader.RecordMarker("run.started", 1));
    }

    /// <summary>
    /// A store as version 14 left it: two looks at one inbox (a full copy,
    /// then new lines), a capture from before pages had keys, an activity
    /// with a summary and a task the user marked, and a session they judged.
    /// </summary>
    private void CreateVersion14Store()
    {
        Directory.CreateDirectory(_directory);
        using var connection = RawConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE schema_version (version INTEGER PRIMARY KEY, applied_at_ms INTEGER NOT NULL);
            INSERT INTO schema_version VALUES (1,0),(2,0),(4,0),(5,0),(6,0),(7,0),(8,0),(9,0),(10,0),(11,0),(12,0),(13,0),(14,0);
            CREATE TABLE raw_events (id TEXT PRIMARY KEY, ts_ms INTEGER NOT NULL, process_name TEXT NOT NULL, executable_path TEXT,
                window_title TEXT NOT NULL, content_hash TEXT NOT NULL UNIQUE, text_length INTEGER NOT NULL, redactions INTEGER NOT NULL);
            CREATE VIRTUAL TABLE raw_events_fts USING fts5(event_id UNINDEXED, text, content_hash UNINDEXED);
            CREATE TABLE manual_scans (id TEXT PRIMARY KEY, captured_at_ms INTEGER NOT NULL, process_name TEXT NOT NULL,
                window_title TEXT NOT NULL, label TEXT, summary TEXT, status TEXT NOT NULL, error TEXT, content_hash TEXT NOT NULL,
                model_id TEXT NOT NULL, uia_characters INTEGER NOT NULL, ocr_characters INTEGER NOT NULL, redactions INTEGER NOT NULL,
                capture_ms REAL NOT NULL, ocr_ms REAL NOT NULL, inference_ms REAL NOT NULL, important_signals TEXT,
                reminder_candidate TEXT, gemma_context_characters INTEGER NOT NULL DEFAULT 0, redacted_uia_text TEXT,
                redacted_ocr_text TEXT, ocr_language TEXT, session_id TEXT, page_key TEXT, app_name TEXT, site TEXT, subject TEXT,
                phase TEXT, mode TEXT, category TEXT, change_kind TEXT, last_seen_ms INTEGER, user_caused INTEGER NOT NULL DEFAULT 0,
                unsaved INTEGER NOT NULL DEFAULT 0, dialog_title TEXT, event_kind TEXT);
            CREATE TABLE activity_markers (id INTEGER PRIMARY KEY, ts_ms INTEGER NOT NULL, kind TEXT NOT NULL, detail TEXT);
            CREATE TABLE app_profiles (key TEXT PRIMARY KEY, display_name TEXT NOT NULL, mode TEXT NOT NULL, category TEXT NOT NULL,
                source TEXT NOT NULL, samples INTEGER NOT NULL DEFAULT 0, sparse_samples INTEGER NOT NULL DEFAULT 0, updated_at_ms INTEGER NOT NULL);
            CREATE TABLE page_states (page_key TEXT PRIMARY KEY, signature TEXT, live_counts BLOB, signature_at_ms INTEGER NOT NULL,
                keyframe_at_ms INTEGER NOT NULL, updated_at_ms INTEGER NOT NULL, moving INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE chat_threads (id TEXT PRIMARY KEY, title TEXT NOT NULL, scope TEXT NOT NULL DEFAULT 'all',
                created_at_ms INTEGER NOT NULL, updated_at_ms INTEGER NOT NULL);
            CREATE TABLE chat_messages (id TEXT PRIMARY KEY, thread_id TEXT NOT NULL REFERENCES chat_threads(id) ON DELETE CASCADE,
                role TEXT NOT NULL, text TEXT NOT NULL, citations_json TEXT NOT NULL DEFAULT '[]', scoped_count INTEGER NOT NULL DEFAULT 0,
                created_at_ms INTEGER NOT NULL);
            CREATE TABLE activity_sessions (id TEXT PRIMARY KEY, started_at_ms INTEGER NOT NULL, ended_at_ms INTEGER NOT NULL,
                process_name TEXT NOT NULL, window_title TEXT NOT NULL, scan_ids_json TEXT NOT NULL DEFAULT '[]', label TEXT, summary TEXT,
                status TEXT NOT NULL, important_signals TEXT, reminder_candidate TEXT, head_text TEXT NOT NULL DEFAULT '',
                tail_text TEXT NOT NULL DEFAULT '', is_minor INTEGER NOT NULL DEFAULT 0, outcome TEXT NOT NULL DEFAULT 'Unknown',
                outcome_source TEXT NOT NULL DEFAULT 'None', outcome_at_ms INTEGER, thread_id TEXT, activities_built INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE activities (id TEXT PRIMARY KEY, session_id TEXT NOT NULL, key TEXT NOT NULL, app TEXT NOT NULL, site TEXT,
                subject TEXT NOT NULL, mode TEXT NOT NULL, category TEXT NOT NULL, started_at_ms INTEGER NOT NULL, ended_at_ms INTEGER NOT NULL,
                active_ms INTEGER NOT NULL, segments_json TEXT NOT NULL DEFAULT '[]', glances_json TEXT NOT NULL DEFAULT '[]',
                events_json TEXT NOT NULL DEFAULT '[]', phases_json TEXT NOT NULL DEFAULT '[]', scan_ids_json TEXT NOT NULL DEFAULT '[]',
                label TEXT NOT NULL, summary TEXT, task TEXT, task_status TEXT NOT NULL DEFAULT 'None', task_by_user INTEGER NOT NULL DEFAULT 0,
                check_state TEXT NOT NULL DEFAULT 'Rule', facts_kept INTEGER NOT NULL DEFAULT 0, facts_dropped INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE early_narrations (activity_key TEXT NOT NULL, started_at_ms INTEGER NOT NULL, ended_at_ms INTEGER NOT NULL,
                label TEXT NOT NULL, summary TEXT, task TEXT, task_status TEXT NOT NULL, check_state TEXT NOT NULL,
                facts_kept INTEGER NOT NULL, facts_dropped INTEGER NOT NULL, created_at_ms INTEGER NOT NULL,
                PRIMARY KEY (activity_key, started_at_ms));

            INSERT INTO raw_events VALUES ('e1', 1000, 'chrome', 'C:\Program Files\Google\Chrome\chrome.exe', 'Inbox', 'h1', 40, 0);
            INSERT INTO raw_events VALUES ('e2', 61000, 'chrome', 'C:\Program Files\Google\Chrome\chrome.exe', 'Inbox', 'h2', 15, 0);
            INSERT INTO raw_events VALUES ('e3', 500, 'notepad', NULL, 'notes.txt', 'h3', 25, 0);
            INSERT INTO raw_events_fts VALUES ('e1', 'Meeting moved to Friday' || char(10) || 'Please send the deck', 'h1');
            INSERT INTO raw_events_fts VALUES ('e2', 'Thanks, will do', 'h2');
            INSERT INTO raw_events_fts VALUES ('e3', 'Groceries for the weekend', 'h3');
            INSERT INTO manual_scans (id, captured_at_ms, process_name, window_title, status, content_hash, model_id, uia_characters,
                ocr_characters, redactions, capture_ms, ocr_ms, inference_ms, session_id, page_key, mode, category, change_kind, last_seen_ms)
            VALUES ('look-1', 1000, 'chrome', 'Inbox', 'Completed', 'h1', '', 40, 0, 0, 0, 0, 0, 's-old', 'chrome|mail.example.com|inbox', 'Read', 'Email', 'Keyframe', 30000),
                   ('look-2', 61000, 'chrome', 'Inbox', 'Completed', 'h2', '', 15, 0, 0, 0, 0, 0, 's-old', 'chrome|mail.example.com|inbox', 'Read', 'Email', 'Delta', 90000),
                   ('look-0', 500, 'notepad', 'notes.txt', 'Completed', 'h3', '', 25, 0, 0, 0, 0, 0, NULL, NULL, NULL, NULL, NULL, NULL);
            INSERT INTO activity_sessions (id, started_at_ms, ended_at_ms, process_name, window_title, scan_ids_json, status, outcome, outcome_source, outcome_at_ms)
            VALUES ('s-old', 1000, 90000, 'chrome', 'Inbox', '["look-1","look-2"]', 'Closed', 'Settled', 'User', 95000);
            INSERT INTO activities (id, session_id, key, app, subject, mode, category, started_at_ms, ended_at_ms, active_ms, label,
                summary, task, task_status, task_by_user, check_state, facts_kept, facts_dropped)
            VALUES ('a-old', 's-old', 'chrome|mail.example.com|inbox', 'Chrome', 'Inbox', 'Read', 'Email', 1000, 90000, 89000,
                'Read the deck request', 'Asked to send the deck.', 'Send the deck', 'Done', 1, 'Verified', 2, 0);
            """;
        command.ExecuteNonQuery();
    }

    /// The store opened directly, with its key.
    private SqliteConnection RawConnection()
    {
        Directory.CreateDirectory(_directory);
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var key = connection.CreateCommand();
        key.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(KeyStore.GetOrCreateKey())}'\";";
        key.ExecuteNonQuery();
        return connection;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
