using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "glint-phase0-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DpapiKeyReopensEncryptedDatabaseAndFtsSearches()
    {
        var keyStore = new DpapiKeyStore(Path.Combine(_directory, "key.bin"));
        var databasePath = Path.Combine(_directory, "memory.db");
        using (var database = Phase0Database.Open(databasePath, keyStore))
        {
            var diagnostics = database.GetDiagnostics();
            Assert.False(string.IsNullOrWhiteSpace(diagnostics.SqlCipherVersion));
            Assert.True(diagnostics.Fts5Available);
            database.Insert(new(
                Guid.NewGuid().ToString(),
                1,
                "Code",
                null,
                "roadmap.md",
                "ABC",
                "Windows capture feasibility roadmap",
                0));
        }

        using var reopened = Phase0Database.Open(databasePath, keyStore);
        Assert.Equal(1, reopened.CountEvents());
        Assert.Single(reopened.Search("capture roadmap"));
    }

    [Fact]
    public void ManualScanHistoryPersistsWithDerivedSummary()
    {
        var keyStore = new DpapiKeyStore(Path.Combine(_directory, "scan-key.bin"));
        var databasePath = Path.Combine(_directory, "scan-memory.db");
        using (var database = Phase0Database.Open(databasePath, keyStore))
        {
            var capture = new RawCaptureEvent(
                "event-1",
                1_000,
                "Code",
                @"C:\Code.exe",
                "roadmap.md",
                "HASH-1",
                "Redacted OCR text",
                2);
            database.SaveManualScan(
                capture,
                new(
                    "scan-1",
                    1_000,
                    "Code",
                    "roadmap.md",
                    "Planning Windows work",
                    "The user planned the Windows implementation.",
                    ManualScanStatus.Completed,
                    null,
                    capture.ContentHash,
                    "gemma-4-e2b",
                    100,
                    50,
                    2,
                    15,
                    25,
                    500,
                    "Release is blocked pending logs.",
                    "Send the logs before the 10 PM meeting tomorrow."));
        }

        using var reopened = Phase0Database.Open(databasePath, keyStore);
        var scan = Assert.Single(reopened.GetRecentManualScans());
        Assert.True(reopened.ContainsManualScanContentHash("HASH-1"));
        Assert.False(reopened.ContainsManualScanContentHash("HASH-2"));
        Assert.Equal("Planning Windows work", scan.Label);
        Assert.Equal("The user planned the Windows implementation.", scan.Summary);
        Assert.Equal("Release is blocked pending logs.", scan.ImportantSignals);
        Assert.Equal(
            "Send the logs before the 10 PM meeting tomorrow.",
            scan.ReminderCandidate);
        Assert.Equal("Redacted OCR text", scan.RedactedInputText);
        Assert.Null(scan.RedactedUiAutomationText);
        Assert.Null(scan.RedactedOcrText);
        Assert.Equal(ManualScanStatus.Completed, scan.Status);
        Assert.Single(reopened.Search("Redacted OCR"));
        Assert.Single(reopened.SearchContext("planned Windows"));
        var rawMatch = Assert.Single(reopened.SearchContext("Redacted OCR"));
        Assert.Contains("[Redacted] [OCR]", rawMatch.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActivitySessionsRoundTripWithOpenSession()
    {
        var keyStore = new DpapiKeyStore(Path.Combine(_directory, "session-key.bin"));
        var databasePath = Path.Combine(_directory, "session-memory.db");
        using (var database = Phase0Database.Open(databasePath, keyStore))
        {
            Assert.Null(database.GetOpenSession());
            database.UpsertSession(new(
                "session-1",
                1_000,
                2_000,
                "Code",
                "mail",
                ["scan-1", "scan-2"],
                null,
                null,
                ActivitySessionStatus.Active,
                null,
                null,
                "reply to the warranty",
                "reply to the warranty email now"));
            Assert.NotNull(database.GetOpenSession());
            var open = database.GetOpenSession()!;
            Assert.Equal("session-1", open.Id);
            Assert.Equal(["scan-1", "scan-2"], open.ScanIds);
            database.UpsertSession(open with
            {
                EndedAtMilliseconds = 3_000,
                Label = "Warranty reply",
                Summary = "Replied to the warranty email.",
                Status = ActivitySessionStatus.Closed
            });
            Assert.Null(database.GetOpenSession());
        }

        using var reopened = Phase0Database.Open(databasePath, keyStore);
        var session = Assert.Single(reopened.GetRecentSessions());
        Assert.Equal("Warranty reply", session.Label);
        Assert.Equal(ActivitySessionStatus.Closed, session.Status);
        Assert.Equal("reply to the warranty", session.HeadText);
    }

    [Fact]
    public void SealingASessionClaimsItsCapturesInOneStep()
    {
        var keyStore = new DpapiKeyStore(Path.Combine(_directory, "seal-key.bin"));
        var databasePath = Path.Combine(_directory, "seal-memory.db");
        using var database = Phase0Database.Open(databasePath, keyStore);

        foreach (var index in Enumerable.Range(1, 3))
        {
            var capture = new RawCaptureEvent(
                $"event-{index}",
                1_000 * index,
                "Code",
                @"C:\Code.exe",
                $"file-{index}.md",
                $"HASH-{index}",
                $"Redacted text {index}",
                0);
            database.SaveManualScan(
                capture,
                new(
                    $"scan-{index}",
                    1_000 * index,
                    "Code",
                    $"file-{index}.md",
                    null,
                    null,
                    ManualScanStatus.Completed,
                    null,
                    capture.ContentHash,
                    "gemma-4-e2b",
                    10,
                    0,
                    0,
                    1,
                    1,
                    0));
        }

        var unassigned = database.GetUnassignedCaptures();
        Assert.Equal(3, unassigned.Count);
        Assert.Equal("scan-1", unassigned[0].Id);

        var drafts = Sessionizer.Cluster(unassigned, 4_000 + Sessionizer.QuietTailMilliseconds);
        var draft = Assert.Single(drafts);
        var session = new ActivitySession(
            "session-1",
            draft.StartedAtMilliseconds,
            draft.EndedAtMilliseconds,
            draft.ProcessName,
            draft.WindowTitle,
            draft.ScanIds,
            null,
            null,
            ActivitySessionStatus.Closed);
        database.SealSession(session, draft.ScanIds);

        // Sealing is what claims the captures, so nothing is left dangling.
        Assert.Empty(database.GetUnassignedCaptures());
        Assert.All(
            database.GetRecentManualScans(),
            scan => Assert.Equal("session-1", scan.SessionId));

        // Sealed but not yet summarized, so a later run can pick it up.
        var pending = Assert.Single(database.GetUnsummarizedSessions());
        Assert.Equal("session-1", pending.Id);
        Assert.Equal(3, pending.ScanIds.Count);

        // Its text comes back oldest first for the summary prompt.
        Assert.Equal(
            ["Redacted text 1", "Redacted text 2", "Redacted text 3"],
            database.GetCaptureTexts(pending.ScanIds));

        database.UpsertSession(pending with { Label = "Editing notes", Summary = "Edited files." });
        Assert.Empty(database.GetUnsummarizedSessions());
    }

    [Fact]
    public void RevisitingEarlierContentIsCapturedAgain()
    {
        var keyStore = new DpapiKeyStore(Path.Combine(_directory, "revisit-key.bin"));
        var databasePath = Path.Combine(_directory, "revisit-memory.db");
        using var database = Phase0Database.Open(databasePath, keyStore);

        void Save(string id, long at, string hash)
        {
            var capture = new RawCaptureEvent(
                $"event-{id}", at, "chrome", null, "Docs", hash, $"text for {hash}", 0);
            database.SaveManualScan(
                capture,
                new(id, at, "chrome", "Docs", "Docs", null, ManualScanStatus.Completed,
                    null, hash, string.Empty, 10, 0, 0, 1, 1, 0));
        }

        Save("scan-a", 1_000, "HASH-A");

        // The same screen immediately after is a repeat.
        Assert.True(database.IsRepeatOfLastCapture("HASH-A"));

        Save("scan-b", 2_000, "HASH-B");

        // Coming back to the earlier screen is new activity, not a repeat.
        // Whole-history dedup used to drop this, which also manufactured a
        // gap that split the session.
        Assert.False(database.IsRepeatOfLastCapture("HASH-A"));
        Assert.True(database.ContainsManualScanContentHash("HASH-A"));
    }

    [Fact]
    public void DedupLookupUsesContentHashIndex()
    {
        var keyStore = new DpapiKeyStore(Path.Combine(_directory, "dedup-key.bin"));
        var databasePath = Path.Combine(_directory, "dedup-memory.db");
        using var database = Phase0Database.Open(databasePath, keyStore);

        var plan = database.ExplainDedupLookup();

        Assert.Contains("idx_manual_scans_content_hash", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("SCAN manual_scans", plan, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
