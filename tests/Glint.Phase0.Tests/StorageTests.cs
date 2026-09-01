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

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
