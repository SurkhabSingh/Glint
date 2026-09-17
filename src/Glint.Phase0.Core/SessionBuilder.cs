namespace Glint.Phase0.Core;

/// Store operations the session builder needs beyond plain reads and writes.
public interface ISessionWorkStore : ISessionStore
{
    IReadOnlyList<CaptureRow> GetUnassignedCaptures(int limit = 1_000);

    IReadOnlyList<string> GetCaptureTexts(IReadOnlyList<string> scanIds);

    void SealSession(ActivitySession session, IReadOnlyList<string> scanIds);

    IReadOnlyList<ActivitySession> GetUnsummarizedSessions(int limit = 20);
}

public sealed record SessionBuildResult(int Sealed, int Summarized, int Failed, int Minor);

/// <summary>
/// Turns ungrouped captures into summarized sessions: group by time, seal in
/// one transaction, then make exactly one model call per session.
///
/// This is where the cost of the product actually sits. A real capture was
/// measured at ~9.9 s of inference, and the old pipeline ran one such call
/// for every capture whose pixels changed, so typing in a window cost a model
/// call per tick. Summarizing per session instead makes that one call per
/// stretch of work.
///
/// Summaries are retried on a later run rather than inline: a session is
/// sealed first and carries a null summary until a run succeeds, so a model
/// failure loses the text but never the session.
/// </summary>
public sealed class SessionBuilder
{
    /// Roughly what one summary prompt should carry before sampling kicks in.
    internal const int SessionTextBudget = 6_000;

    /// Below this, a session is not worth a model call. Measured against real
    /// sessions, the split is stark: the throwaway ones (an empty test window,
    /// a glance at a chat) held 234-658 characters, while the next real
    /// session held 10,859. The threshold sits deliberately near the bottom of
    /// that gap, because a short chat is exactly where a commitment hides and
    /// skipping it would lose that signal to save ten seconds.
    internal const int MinimumSummaryCharacters = 400;

    /// Most samples taken from a long session, spread across its span.
    internal const int MaxSamples = 12;

    private readonly ISessionWorkStore _store;
    private readonly IActivitySummarizer _summarizer;
    private readonly int _maxSummariesPerRun;

    public SessionBuilder(
        ISessionWorkStore store,
        IActivitySummarizer summarizer,
        int maxSummariesPerRun = 5)
    {
        _store = store;
        _summarizer = summarizer;
        _maxSummariesPerRun = maxSummariesPerRun;
    }

    public async Task<SessionBuildResult> RunAsync(
        long nowMilliseconds,
        CancellationToken cancellationToken = default)
    {
        var sealedCount = 0;
        foreach (var draft in Sessionizer.Cluster(
                     _store.GetUnassignedCaptures(),
                     nowMilliseconds))
        {
            var session = new ActivitySession(
                Guid.NewGuid().ToString("N"),
                draft.StartedAtMilliseconds,
                draft.EndedAtMilliseconds,
                draft.ProcessName,
                draft.WindowTitle,
                draft.ScanIds,
                null,
                null,
                ActivitySessionStatus.Closed);
            _store.SealSession(session, draft.ScanIds);
            sealedCount++;
        }

        var summarized = 0;
        var failed = 0;
        var minor = 0;
        foreach (var session in _store.GetUnsummarizedSessions(_maxSummariesPerRun))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = BuildSessionText(_store.GetCaptureTexts(session.ScanIds));
            if (text.Length < MinimumSummaryCharacters)
            {
                // Too little on screen to say anything real about. Keep the
                // session as a record of the time, labelled from the window,
                // and mark it so it is never picked up again.
                _store.UpsertSession(session with
                {
                    Label = string.IsNullOrWhiteSpace(session.WindowTitle)
                        ? session.ProcessName
                        : session.WindowTitle,
                    IsMinor = true
                });
                minor++;
                continue;
            }

            try
            {
                var summary = await _summarizer.SummarizeAsync(
                        DateTimeOffset.FromUnixTimeMilliseconds(session.StartedAtMilliseconds),
                        session.ProcessName,
                        session.WindowTitle,
                        text,
                        cancellationToken)
                    .ConfigureAwait(false);
                _store.UpsertSession(session with
                {
                    Label = summary.Label,
                    Summary = summary.Summary,
                    ImportantSignals = summary.ImportantSignals,
                    ReminderCandidate = summary.ReminderCandidate
                });
                summarized++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Left unsummarized on purpose so the next run retries it.
                failed++;
            }
        }

        return new(sealedCount, summarized, failed, minor);
    }

    /// <summary>
    /// Builds one prompt from a session's captures: drop consecutive repeats
    /// (a window whose text barely changed), then sample evenly across the
    /// span so a long session is represented start to end rather than
    /// truncated to its beginning.
    /// </summary>
    internal static string BuildSessionText(
        IReadOnlyList<string> texts,
        int budget = SessionTextBudget)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var distinct = new List<string>();
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (distinct.Count == 0 || !string.Equals(distinct[^1], text, StringComparison.Ordinal))
            {
                distinct.Add(text);
            }
        }

        if (distinct.Count == 0)
        {
            return string.Empty;
        }

        var joined = string.Join("\n---\n", distinct);
        if (joined.Length <= budget)
        {
            return joined;
        }

        var step = Math.Max(1, (int)Math.Ceiling(distinct.Count / (double)MaxSamples));
        var sampled = distinct.Where((_, index) => index % step == 0).ToList();
        joined = string.Join("\n---\n", sampled);
        return joined.Length <= budget ? joined : joined[..budget];
    }
}
