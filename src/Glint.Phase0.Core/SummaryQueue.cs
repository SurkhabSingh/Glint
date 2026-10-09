namespace Glint.Phase0.Core;

public sealed record SummaryRunResult(
    int Summarized,
    int Reused,
    int Failed,
    int Verified,
    int Partial,
    int Fallback,
    // Reading activities still waiting after this pass.
    int Waiting);

/// <summary>
/// Describes finished reading activities with the local model, a few at a
/// time, and keeps each description so it is never paid for twice.
/// </summary>
/// <remarks>
/// Only activities that have ended are described: a finished activity's text
/// no longer changes, so its description never goes stale. One whose text
/// matches an activity already described (the same page, rebuilt to start
/// elsewhere) takes that description instead of a model call. Text too short
/// to say anything about keeps its rule label, and that is kept too, so it is
/// not looked at again.
/// </remarks>
public static class SummaryQueue
{
    /// Below this much text there is nothing for a model to add.
    public const int MinimumNarrationCharacters = 160;

    /// How far back a pass looks for activities still waiting.
    public const long LookbackMilliseconds = 14 * 86_400_000L;

    /// <param name="store">Where activities and their text are read from.</param>
    /// <param name="save">Keeps one description; called once per activity described.</param>
    public static async Task<SummaryRunResult> RunAsync(
        IActivityViewStore store,
        Action<StoredSummary> save,
        IActivityNarrator narrator,
        long nowMilliseconds,
        int maxNarrations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(narrator);
        var snapshot = new ActivityView(store, () => nowMilliseconds)
            .Build(nowMilliseconds - LookbackMilliseconds, nowMilliseconds);
        var waiting = snapshot.Activities
            .Where(activity => activity.Check == SummaryCheck.Pending && HasEnded(activity, snapshot, nowMilliseconds))
            .OrderBy(activity => activity.EndedAtMilliseconds)
            .ToList();

        int summarized = 0, reused = 0, failed = 0, verified = 0, partial = 0, fallback = 0, done = 0;
        foreach (var activity in waiting)
        {
            if (summarized >= maxNarrations)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var text = ActivityText.Build(ActivityTexts.For(store, activity));
            var hash = ActivityIds.TextHash(text);
            var same = store.GetSummaries([activity.Key])
                .FirstOrDefault(summary => summary.TextHash == hash && summary.Check != SummaryCheck.Pending);
            if (same is not null)
            {
                save(same with { StartedAtMilliseconds = activity.StartedAtMilliseconds });
                reused++;
                done++;
                continue;
            }

            if (text.Length < MinimumNarrationCharacters)
            {
                save(new StoredSummary(activity.Key, activity.StartedAtMilliseconds, hash, activity.Label, null, null, SummaryCheck.Rule, 0, 0));
                done++;
                continue;
            }

            Narration narration;
            try
            {
                narration = await narrator.NarrateAsync(
                        new NarrationRequest(
                            activity.Category,
                            activity.App,
                            activity.Site,
                            activity.Subject,
                            activity.Phases,
                            DateTimeOffset.FromUnixTimeMilliseconds(activity.StartedAtMilliseconds),
                            text),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidDataException)
            {
                // The model answered, but not in the required shape. The rule
                // label stands; asking again would loop forever.
                save(new StoredSummary(activity.Key, activity.StartedAtMilliseconds, hash, activity.Label, null, null, SummaryCheck.Fallback, 0, 0));
                summarized++;
                fallback++;
                done++;
                continue;
            }
            catch
            {
                // The worker failed outright: everything left waits for the next pass.
                failed++;
                break;
            }

            var others = snapshot.Activities
                .Where(other => other.SessionId == activity.SessionId && other.Key != activity.Key)
                .Select(other => $"{other.Subject} {string.Join(' ', other.Phases)}");
            var metadata = new List<string> { activity.App, activity.Subject };
            if (activity.Site is not null)
            {
                metadata.Add(activity.Site);
                metadata.Add(TitleNormalizer.SiteBrand(activity.Site));
            }

            metadata.AddRange(activity.Phases);
            var checkedNarration = SummaryVerifier.Verify(narration, text, metadata, others);
            save(new StoredSummary(
                activity.Key,
                activity.StartedAtMilliseconds,
                hash,
                checkedNarration.Label ?? activity.Label,
                checkedNarration.Summary,
                checkedNarration.Task,
                checkedNarration.Check,
                checkedNarration.Kept,
                checkedNarration.Dropped));
            summarized++;
            done++;
            switch (checkedNarration.Check)
            {
                case SummaryCheck.Verified:
                    verified++;
                    break;
                case SummaryCheck.Partial:
                    partial++;
                    break;
                case SummaryCheck.Fallback:
                    fallback++;
                    break;
            }
        }

        return new SummaryRunResult(summarized, reused, failed, verified, partial, fallback, waiting.Count - done);
    }

    /// <summary>
    /// Whether an activity can no longer change: its sitting is over, its app
    /// was closed after it, or something else has been going on for longer
    /// than it could still be resumed.
    /// </summary>
    public static bool HasEnded(ActivityRecord activity, ActivitySnapshot snapshot, long nowMilliseconds)
    {
        var session = snapshot.Sessions.FirstOrDefault(candidate => candidate.Id == activity.SessionId);
        if (session is null || session.Status == ActivitySessionStatus.Closed)
        {
            return true;
        }

        var appKey = activity.Key.Split('|')[0];
        var closed = snapshot.Markers.Any(marker => marker.ClosesApp
            && marker.TimestampMilliseconds >= activity.EndedAtMilliseconds - 1_000
            && string.Equals(marker.Detail, appKey, StringComparison.OrdinalIgnoreCase));
        if (closed)
        {
            return true;
        }

        var lastStart = snapshot.Activities
            .Where(other => other.SessionId == activity.SessionId)
            .Max(other => other.StartedAtMilliseconds);
        return activity.StartedAtMilliseconds < lastStart
            && nowMilliseconds - activity.EndedAtMilliseconds > ActivitySegmenter.ResumeWithinMilliseconds;
    }
}

/// <summary>
/// The text an activity's description is written from: its pages' lines that
/// were on screen during it, grouped by the look that first saw them.
/// </summary>
public static class ActivityTexts
{
    public static IReadOnlyList<ScanText> For(IActivityViewStore store, ActivityRecord activity)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(activity);
        var pages = activity.PageKeys ?? [];
        if (pages.Count == 0)
        {
            return [];
        }

        var segments = activity.Segments.Count > 0
            ? activity.Segments
            : [new ActivitySegment(activity.StartedAtMilliseconds, activity.EndedAtMilliseconds)];
        var chunks = store.GetPageChunks(pages, activity.StartedAtMilliseconds, activity.EndedAtMilliseconds)
            .Where(chunk => segments.Any(segment =>
                chunk.FirstSeenMilliseconds <= segment.EndMilliseconds
                && chunk.LastSeenMilliseconds >= segment.StartMilliseconds))
            .ToList();
        return Group(chunks, activity.StartedAtMilliseconds);
    }

    /// <summary>
    /// Lines already on a page when the activity began read as that page's
    /// screen; lines that appeared during it read as what was new, look by look.
    /// </summary>
    internal static IReadOnlyList<ScanText> Group(IReadOnlyList<PageChunk> chunks, long startedAtMilliseconds)
    {
        var texts = new List<ScanText>();
        var pagesWithScreen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in chunks
                     .Where(chunk => chunk.FirstSeenMilliseconds < startedAtMilliseconds)
                     .GroupBy(chunk => chunk.PageKey))
        {
            pagesWithScreen.Add(page.Key);
            texts.Add(new ScanText(
                $"screen:{page.Key}",
                startedAtMilliseconds,
                CaptureChange.Keyframe,
                false,
                string.Join(Environment.NewLine, page.Select(chunk => chunk.Text))));
        }

        foreach (var look in chunks
                     .Where(chunk => chunk.FirstSeenMilliseconds >= startedAtMilliseconds)
                     .GroupBy(chunk => chunk.ScanId ?? $"chunk:{chunk.Id}")
                     .OrderBy(group => group.Min(chunk => chunk.FirstSeenMilliseconds)))
        {
            var page = look.First().PageKey;
            // The first look at a page holds its screen; later ones what changed.
            var change = pagesWithScreen.Add(page) ? CaptureChange.Keyframe : CaptureChange.Delta;
            texts.Add(new ScanText(
                look.Key,
                look.Min(chunk => chunk.FirstSeenMilliseconds),
                change,
                look.Any(chunk => chunk.UserCaused),
                string.Join(Environment.NewLine, look.Select(chunk => chunk.Text))));
        }

        return texts;
    }
}
