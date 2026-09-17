using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class SessionBuilderTests
{
    private const long Start = 1_700_000_000_000;

    [Fact]
    public async Task ASessionWithAlmostNothingOnScreenIsNotWorthAModelCall()
    {
        var store = new FakeSessionStore();
        store.AddCapture("scan-1", Start, "Glint.Phase0.WpfTarget", "Target", new string('x', 200));
        var summarizer = new CountingSummarizer();

        var result = await new SessionBuilder(store, summarizer)
            .RunAsync(Start + Sessionizer.QuietTailMilliseconds);

        Assert.Equal(1, result.Sealed);
        Assert.Equal(1, result.Minor);
        Assert.Equal(0, result.Summarized);
        Assert.Equal(0, summarizer.Calls);

        var session = Assert.Single(store.Sessions.Values);
        Assert.True(session.IsMinor);
        Assert.Null(session.Summary);
        // Labelled from the window, so the timeline still reads sensibly.
        Assert.Equal("Target", session.Label);
    }

    [Fact]
    public async Task AMinorSessionIsNeverPickedUpAgain()
    {
        var store = new FakeSessionStore();
        store.AddCapture("scan-1", Start, "app", "Window", new string('x', 100));
        var summarizer = new CountingSummarizer();
        var builder = new SessionBuilder(store, summarizer);
        var now = Start + Sessionizer.QuietTailMilliseconds;

        await builder.RunAsync(now);
        var second = await builder.RunAsync(now);

        // A decision, not a backlog: it must not sit in the queue forever.
        Assert.Equal(0, second.Minor);
        Assert.Equal(0, second.Summarized);
        Assert.Equal(0, summarizer.Calls);
    }

    [Fact]
    public async Task ARealSessionStillGetsExactlyOneModelCall()
    {
        var store = new FakeSessionStore();
        // Three captures well past the threshold, one stretch.
        store.AddCapture("scan-1", Start, "chrome", "Docs", new string('a', 900));
        store.AddCapture("scan-2", Start + 30_000, "chrome", "Docs", new string('b', 900));
        store.AddCapture("scan-3", Start + 60_000, "chrome", "Docs", new string('c', 900));
        var summarizer = new CountingSummarizer();

        var result = await new SessionBuilder(store, summarizer)
            .RunAsync(Start + 60_000 + Sessionizer.QuietTailMilliseconds);

        Assert.Equal(1, result.Sealed);
        Assert.Equal(1, result.Summarized);
        Assert.Equal(0, result.Minor);
        Assert.Equal(1, summarizer.Calls);

        var session = Assert.Single(store.Sessions.Values);
        Assert.False(session.IsMinor);
        Assert.Equal("a summary", session.Summary);
        Assert.Equal(3, session.ScanIds.Count);
    }

    [Fact]
    public async Task AFailedSummaryIsLeftForTheNextRun()
    {
        var store = new FakeSessionStore();
        store.AddCapture("scan-1", Start, "chrome", "Docs", new string('a', 900));
        var summarizer = new CountingSummarizer { Throw = true };
        var builder = new SessionBuilder(store, summarizer);
        var now = Start + Sessionizer.QuietTailMilliseconds;

        var first = await builder.RunAsync(now);
        Assert.Equal(1, first.Failed);
        Assert.False(Assert.Single(store.Sessions.Values).IsMinor);

        // Retried rather than marked minor, because the text was there.
        summarizer.Throw = false;
        var second = await builder.RunAsync(now);
        Assert.Equal(1, second.Summarized);
    }

    [Fact]
    public async Task ASessionThatLeftAReminderIsMarkedUnfinished()
    {
        var store = new FakeSessionStore();
        store.AddCapture("scan-1", Start, "Discord", "Chat", new string('a', 900));
        var summarizer = new CountingSummarizer
        {
            Reminder = "Send Alex the logs before the 10 PM meeting"
        };

        var result = await new SessionBuilder(store, summarizer)
            .RunAsync(Start + Sessionizer.QuietTailMilliseconds);

        var session = Assert.Single(store.Sessions.Values);
        Assert.Equal(SessionOutcome.Open, session.Outcome);
        Assert.Equal(SessionOutcomeSource.Rule, session.OutcomeSource);
        Assert.NotNull(session.OutcomeAtMilliseconds);
        // The verdict came from the summary already paid for.
        Assert.Equal(1, summarizer.Calls);
        Assert.Equal(0, result.Decided);
    }

    [Fact]
    public async Task ASessionWithNoReminderIsLeftUnknownRatherThanDone()
    {
        var store = new FakeSessionStore();
        store.AddCapture("scan-1", Start, "chrome", "Docs", new string('a', 900));

        await new SessionBuilder(store, new CountingSummarizer())
            .RunAsync(Start + Sessionizer.QuietTailMilliseconds);

        var session = Assert.Single(store.Sessions.Values);
        Assert.Equal(SessionOutcome.Unknown, session.Outcome);
        Assert.NotEqual(SessionOutcome.Settled, session.Outcome);
    }

    [Fact]
    public async Task SessionsSummarizedBeforeOutcomesExistedAreSweptUp()
    {
        var store = new FakeSessionStore();
        // A session from before outcomes: summarized, never judged.
        store.UpsertSession(new ActivitySession(
            "old-1", Start, Start + 1_000, "Discord", "Chat", ["scan-x"],
            "Chatting", "The user chatted.", ActivitySessionStatus.Closed,
            null, "Send the logs tonight"));

        var summarizer = new CountingSummarizer();
        var result = await new SessionBuilder(store, summarizer)
            .RunAsync(Start + Sessionizer.QuietTailMilliseconds);

        Assert.Equal(1, result.Decided);
        Assert.Equal(SessionOutcome.Open, store.Sessions["old-1"].Outcome);
        // Swept up without re-running the model over it.
        Assert.Equal(0, summarizer.Calls);

        // And not judged twice.
        Assert.Equal(0, (await new SessionBuilder(store, summarizer)
            .RunAsync(Start + Sessionizer.QuietTailMilliseconds)).Decided);
    }

    private static ActivitySession Outstanding(
        string id,
        long at,
        string process,
        string reminder,
        SessionOutcome outcome = SessionOutcome.Unknown,
        SessionOutcomeSource source = SessionOutcomeSource.None) =>
        new(
            id, at, at + 60_000, process, "Chat", [$"scan-{id}"],
            "Chatting", "The user chatted.", ActivitySessionStatus.Closed,
            null, reminder, Outcome: outcome, OutcomeSource: source);

    [Fact]
    public async Task TheSameCommitmentMentionedTwiceIsListedOnce()
    {
        // The real pair: two Discord sittings the same day about one meeting.
        var store = new FakeSessionStore();
        store.UpsertSession(Outstanding(
            "a", Start, "Discord", "Job meeting tomorrow at 9 pm with unemployed job meeting"));
        store.UpsertSession(Outstanding(
            "b", Start + (14 * 3_600_000), "Discord",
            "Jobless meeting tomorrow at 10 pm with kiyopon"));

        await new SessionBuilder(store, new CountingSummarizer())
            .RunAsync(Start + (20 * 3_600_000));

        // The later mention stands; the earlier one is replaced, not "done".
        Assert.Equal(SessionOutcome.Open, store.Sessions["b"].Outcome);
        Assert.Equal(SessionOutcome.Superseded, store.Sessions["a"].Outcome);
        Assert.Equal(SessionOutcomeSource.Recurrence, store.Sessions["a"].OutcomeSource);
        Assert.NotEqual(SessionOutcome.Settled, store.Sessions["a"].Outcome);

        // Both sides of one thread.
        Assert.NotNull(store.Sessions["a"].ThreadId);
        Assert.Equal(store.Sessions["a"].ThreadId, store.Sessions["b"].ThreadId);
    }

    [Fact]
    public async Task UnrelatedCommitmentsAreNotFoldedTogether()
    {
        var store = new FakeSessionStore();
        store.UpsertSession(Outstanding(
            "a", Start, "Discord", "Send logs tonight before our meeting at 10pm tomorrow"));
        store.UpsertSession(Outstanding(
            "b", Start + 3_600_000, "Discord",
            "Jobless meeting tomorrow at 10 pm with kiyopon"));

        await new SessionBuilder(store, new CountingSummarizer())
            .RunAsync(Start + (5 * 3_600_000));

        // Both still outstanding, in threads of their own.
        Assert.Equal(SessionOutcome.Open, store.Sessions["a"].Outcome);
        Assert.Equal(SessionOutcome.Open, store.Sessions["b"].Outcome);
        Assert.NotEqual(store.Sessions["a"].ThreadId, store.Sessions["b"].ThreadId);
    }

    [Fact]
    public async Task AVerdictTheUserGaveIsNotRewrittenBySupersession()
    {
        var store = new FakeSessionStore();
        store.UpsertSession(Outstanding(
            "a", Start, "Discord", "Job meeting tomorrow at 9 pm with unemployed job meeting",
            SessionOutcome.Open, SessionOutcomeSource.User));
        store.UpsertSession(Outstanding(
            "b", Start + (14 * 3_600_000), "Discord",
            "Jobless meeting tomorrow at 10 pm with kiyopon"));

        await new SessionBuilder(store, new CountingSummarizer())
            .RunAsync(Start + (20 * 3_600_000));

        // Still theirs, and still open.
        Assert.Equal(SessionOutcome.Open, store.Sessions["a"].Outcome);
        Assert.Equal(SessionOutcomeSource.User, store.Sessions["a"].OutcomeSource);
        // They share the thread even so.
        Assert.Equal(store.Sessions["a"].ThreadId, store.Sessions["b"].ThreadId);
    }

    private sealed class CountingSummarizer : IActivitySummarizer
    {
        public int Calls { get; private set; }

        public bool Throw { get; set; }

        public string? Reminder { get; set; }

        public string ModelId => "fake";

        public Task<ActivitySummary> SummarizeAsync(
            DateTimeOffset capturedAt,
            string processName,
            string windowTitle,
            string redactedText,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Throw)
            {
                throw new InvalidOperationException("model unavailable");
            }

            return Task.FromResult(new ActivitySummary(
                "a label",
                "a summary",
                ModelId,
                TimeSpan.FromSeconds(1),
                ReminderCandidate: Reminder));
        }
    }

    private sealed class FakeSessionStore : ISessionWorkStore
    {
        private readonly List<CaptureRow> _captures = [];
        private readonly Dictionary<string, string> _texts = [];
        private readonly HashSet<string> _claimed = [];

        public Dictionary<string, ActivitySession> Sessions { get; } = [];

        public void AddCapture(string id, long at, string process, string title, string text)
        {
            _captures.Add(new(id, at, process, title));
            _texts[id] = text;
        }

        public IReadOnlyList<CaptureRow> GetUnassignedCaptures(int limit = 1_000) =>
            _captures.Where(capture => !_claimed.Contains(capture.Id)).Take(limit).ToList();

        public IReadOnlyList<string> GetCaptureTexts(IReadOnlyList<string> scanIds) =>
            scanIds.Where(_texts.ContainsKey).Select(id => _texts[id]).ToList();

        public void SealSession(ActivitySession session, IReadOnlyList<string> scanIds)
        {
            Sessions[session.Id] = session;
            foreach (var id in scanIds)
            {
                _claimed.Add(id);
            }
        }

        public IReadOnlyList<ActivitySession> GetUnsummarizedSessions(int limit = 20) =>
            Sessions.Values
                .Where(session => session.Summary is null && !session.IsMinor)
                .Take(limit)
                .ToList();

        public List<ActivityMarker> Markers { get; } = [];

        public IReadOnlyList<ActivityMarker> GetMarkers(long fromMilliseconds, long toMilliseconds) =>
            Markers
                .Where(marker => marker.TimestampMilliseconds >= fromMilliseconds
                    && marker.TimestampMilliseconds <= toMilliseconds)
                .ToList();

        public IReadOnlyList<ActivitySession> GetSessionsWithoutOutcome(int limit = 200) =>
            Sessions.Values
                .Where(session => session.OutcomeSource == SessionOutcomeSource.None
                    && session.Summary is not null)
                .Take(limit)
                .ToList();

        public IReadOnlyList<ActivitySession> GetRecentOpenSessions(
            long sinceMilliseconds,
            int limit = 200) =>
            Sessions.Values
                .Where(session => session.Outcome == SessionOutcome.Open
                    && session.StartedAtMilliseconds >= sinceMilliseconds)
                .OrderByDescending(session => session.StartedAtMilliseconds)
                .Take(limit)
                .ToList();

        public IReadOnlyList<ActivitySession> GetOpenSessionsWithoutThread(int limit = 200) =>
            Sessions.Values
                .Where(session => session.Outcome == SessionOutcome.Open
                    && session.ThreadId is null)
                .OrderBy(session => session.StartedAtMilliseconds)
                .Take(limit)
                .ToList();

        public ActivitySession? GetOpenSession() => null;

        public void UpsertSession(ActivitySession session) => Sessions[session.Id] = session;

        public IReadOnlyList<ActivitySession> GetRecentSessions(int limit = 50) =>
            Sessions.Values.Take(limit).ToList();
    }
}
