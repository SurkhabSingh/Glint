using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class SessionManagerTests
{
    [Fact]
    public void SimilarityScoresIdenticalTextAtOne()
    {
        Assert.Equal(1, SessionManager.Similarity("Reply to the warranty email", "reply to the WARRANTY email"));
    }

    [Fact]
    public void SimilarityScoresDisjointTextAtZero()
    {
        Assert.Equal(0, SessionManager.Similarity("alpha beta gamma", "delta epsilon zeta"));
        Assert.Equal(0, SessionManager.Similarity(string.Empty, "something"));
    }

    [Fact]
    public async Task FirstScanCreatesActiveSessionWithoutClosing()
    {
        var store = new FakeSessionStore();
        var summarizer = new FakeSessionSummarizer();
        var manager = new SessionManager(store, summarizer);

        var tracked = await manager.TrackScanAsync(
            "scan-1", "Code", "mail", 1_000, "reply to the warranty email");

        Assert.NotNull(tracked.SessionId);
        Assert.Null(tracked.ClosedSession);
        Assert.Equal(0, summarizer.CallCount);
        var open = Assert.Single(
            store.Sessions.Values,
            s => s.Status == ActivitySessionStatus.Active);
        Assert.Equal(["scan-1"], open.ScanIds);
    }

    [Fact]
    public async Task SameWindowAppendsWithoutSummarizing()
    {
        var store = new FakeSessionStore();
        var summarizer = new FakeSessionSummarizer();
        var manager = new SessionManager(store, summarizer);

        var first = await manager.TrackScanAsync(
            "scan-1", "Code", "mail", 1_000, "reply to the warranty email now");
        var second = await manager.TrackScanAsync(
            "scan-2", "Code", "mail", 20_000, "reply to the warranty email now please");

        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Null(second.ClosedSession);
        Assert.Equal(0, summarizer.CallCount);
        Assert.Equal(2, store.Sessions[first.SessionId].ScanIds.Count);
    }

    [Fact]
    public async Task WindowSwitchClosesPreviousSessionWithSummary()
    {
        var store = new FakeSessionStore();
        var summarizer = new FakeSessionSummarizer();
        var manager = new SessionManager(store, summarizer);

        var first = await manager.TrackScanAsync(
            "scan-1", "Code", "mail", 1_000, "reply to the warranty email now");
        var second = await manager.TrackScanAsync(
            "scan-2", "chrome", "news", 5_000, "completely different sports scores");

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.NotNull(second.ClosedSession);
        var closed = second.ClosedSession!;
        Assert.Equal(first.SessionId, closed.Id);
        Assert.Equal(ActivitySessionStatus.Closed, closed.Status);
        Assert.Equal("Closed session", closed.Label);
        Assert.Equal(1, summarizer.CallCount);
        Assert.Equal(ActivitySessionStatus.Closed, store.Sessions[first.SessionId].Status);
    }

    [Fact]
    public async Task IdleGapBeyondThresholdStartsNewSession()
    {
        var store = new FakeSessionStore();
        var summarizer = new FakeSessionSummarizer();
        var manager = new SessionManager(store, summarizer);

        var first = await manager.TrackScanAsync(
            "scan-1", "Code", "mail", 1_000, "reply to the warranty email now");
        var second = await manager.TrackScanAsync(
            "scan-2", "Code", "mail", 1_000 + SessionManager.MaxGapMilliseconds + 1,
            "reply to the warranty email now");

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.NotNull(second.ClosedSession);
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        public Dictionary<string, ActivitySession> Sessions { get; } = new();

        public ActivitySession? GetOpenSession() =>
            Sessions.Values
                .Where(session => session.Status == ActivitySessionStatus.Active)
                .OrderByDescending(session => session.StartedAtMilliseconds)
                .FirstOrDefault();

        public void UpsertSession(ActivitySession session) =>
            Sessions[session.Id] = session;

        public IReadOnlyList<ActivitySession> GetRecentSessions(int limit = 50) =>
            Sessions.Values
                .OrderByDescending(session => session.StartedAtMilliseconds)
                .Take(limit)
                .ToArray();
    }

    private sealed class FakeSessionSummarizer : IActivitySummarizer
    {
        public string ModelId => "test-gemma";

        public int CallCount { get; private set; }

        public Task<ActivitySummary> SummarizeAsync(
            DateTimeOffset capturedAt,
            string processName,
            string windowTitle,
            string redactedText,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(
                new ActivitySummary(
                    "Closed session",
                    "Session summary.",
                    ModelId,
                    TimeSpan.FromMilliseconds(5)));
        }
    }
}
