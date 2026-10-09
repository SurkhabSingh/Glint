using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "glint-phase0-tests",
        Guid.NewGuid().ToString("N"));

    private Phase0Database Open(string name = "memory") =>
        Phase0Database.Open(
            Path.Combine(_directory, $"{name}.db"),
            new DpapiKeyStore(Path.Combine(_directory, $"{name}.key")));

    private static ManualScanRecord Look(string id, long at, string pageKey, string title = "Inbox") =>
        new(
            id,
            at,
            "chrome",
            title,
            title,
            null,
            ManualScanStatus.Completed,
            null,
            $"hash-{id}",
            string.Empty,
            10,
            0,
            0,
            1,
            1,
            0,
            PageKey: pageKey,
            AppName: "Chrome",
            Subject: title,
            Mode: ActivityMode.Read,
            Category: ActivityCategory.Email,
            Change: CaptureChange.Delta,
            LastSeenMilliseconds: at);

    [Fact]
    public void TheStoreIsEncryptedAndItsLinesAreSearchable()
    {
        using (var database = Open())
        {
            var diagnostics = database.GetDiagnostics();
            Assert.False(string.IsNullOrWhiteSpace(diagnostics.SqlCipherVersion));
            Assert.True(diagnostics.Fts5Available);
            database.SaveLook(Look("look-1", 1_000, "chrome|mail|inbox"), PageLines.Of("Windows capture feasibility roadmap"));
        }

        using var reopened = Open();
        Assert.Equal(1, reopened.CountChunks());
        var hit = Assert.Single(reopened.SearchContext("capture roadmap"));
        Assert.Equal("look-1", hit.Id);
        Assert.Contains("[capture]", hit.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ALookShowsTheLinesItWasFirstToSee()
    {
        using var database = Open();
        database.SaveLook(Look("look-1", 1_000, "chrome|mail|inbox"), PageLines.Of("Meeting moved to Friday\nPlease send the deck"));
        database.SaveLook(Look("look-2", 2_000, "chrome|mail|inbox"), PageLines.Of("Thanks, will do"));

        var looks = database.GetRecentLooks();

        Assert.Equal(["look-2", "look-1"], looks.Select(look => look.Id));
        Assert.Equal("Thanks, will do", looks[0].RedactedInputText);
        Assert.Equal($"Meeting moved to Friday{Environment.NewLine}Please send the deck", looks[1].RedactedInputText);
    }

    [Fact]
    public void APageKeepsEachLineOnceWithWhenItWasOnScreen()
    {
        using var database = Open();
        const string page = "chrome|mail|inbox";
        var lines = PageLines.Of("Meeting moved to Friday\nPlease send the deck");
        database.SaveLook(Look("look-1", 1_000, page), lines);

        // Read again later: known lines are not stored again, only seen again.
        var keys = lines.Select(line => line.Key).ToList();
        Assert.Equal(keys.ToHashSet(), database.GetKnownLines(page, keys));
        database.TouchLines(page, keys, 5_000);

        // A picture check later finds nothing changed: still on screen.
        database.TouchLinesSeenAt(page, 5_000, 9_000);

        var chunks = database.GetPageChunks([page], 0, 10_000);
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, chunk =>
        {
            Assert.Equal(1_000, chunk.FirstSeenMilliseconds);
            Assert.Equal(9_000, chunk.LastSeenMilliseconds);
        });

        // Not on screen before it was first seen.
        Assert.Empty(database.GetPageChunks([page], 0, 999));
    }

    [Fact]
    public void EachWindowSwitchEndsTheStretchBeforeIt()
    {
        using var database = Open();
        database.OpenFocus(1_000, "Code", "roadmap.md", null, null);
        database.OpenFocus(5_000, "chrome", null, "PrivateBrowsing", "private window");
        database.TouchFocus(8_000);

        var open = database.GetOpenFocus();
        Assert.NotNull(open);
        Assert.Equal("chrome", open.ProcessName);
        Assert.Null(open.Title);
        Assert.Equal(8_000, open.LastSeenMilliseconds);

        database.CloseFocus(9_000);
        Assert.Null(database.GetOpenFocus());

        var rows = database.GetFocusBetween(0, 10_000);
        Assert.Equal([(1_000L, (long?)5_000L), (5_000L, (long?)9_000L)], rows.Select(row => (row.StartedAtMilliseconds, row.EndedAtMilliseconds)));
        Assert.Equal("PrivateBrowsing", rows[1].Suppressed);
    }

    [Fact]
    public void AStretchACrashLeftOpenEndsWhereItWasLastSeen()
    {
        using var database = Open();
        database.OpenFocus(1_000, "Code", "roadmap.md", null, null);
        database.TouchFocus(4_000);

        database.CloseDanglingFocus();

        var row = Assert.Single(database.GetFocusBetween(0, 10_000));
        Assert.Equal(4_000, row.EndedAtMilliseconds);
    }

    [Fact]
    public void SummariesAndTheUsersVerdictsAreKept()
    {
        using (var database = Open())
        {
            database.SaveSummary(new StoredSummary("chrome|mail|inbox", 1_000, "abc", "Read the deck request", "Asked for the deck.", "Send the deck", SummaryCheck.Verified, 2, 0), 5_000);
            database.SetActivityTaskStatus("a1", ActivityTaskStatus.Done, 6_000);
            database.SetSessionOutcome("s1", SessionOutcome.Settled, 7_000);
        }

        using var reopened = Open();
        var summary = Assert.Single(reopened.GetSummaries(["chrome|mail|inbox"]));
        Assert.Equal(("Read the deck request", SummaryCheck.Verified, "abc"), (summary.Label, summary.Check, summary.TextHash));
        Assert.Equal(ActivityTaskStatus.Done, reopened.GetTaskStatuses(["a1", "a2"])["a1"]);
        Assert.Equal(SessionOutcome.Settled, Assert.Single(reopened.GetSessionOutcomes(["s1"])).Value);
    }

    [Fact]
    public void AStopEndsTheLookItInterrupts()
    {
        using var database = Open();
        database.RecordMarker("user.away", 3_000);
        database.RecordMarker("app.closed", 4_000, "Chrome");

        // Closing an app is not a break in the sitting.
        Assert.Equal(3_000, database.GetLatestBreakMilliseconds());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
