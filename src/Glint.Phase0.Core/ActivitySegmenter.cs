namespace Glint.Phase0.Core;

/// <summary>An activity before it has an id, a label or a summary.</summary>
public sealed class ActivityDraft
{
    public ActivityDraft(PageIdentity identity)
    {
        Identity = identity;
    }

    public PageIdentity Identity { get; }

    public List<ActivitySegment> Segments { get; } = [];

    public List<ActivityGlance> Glances { get; } = [];

    public List<ActivityEvent> Events { get; } = [];

    public List<string> Phases { get; } = [];

    public List<string> ScanIds { get; } = [];

    public long StartedAtMilliseconds => Segments.Count == 0 ? 0 : Segments[0].StartMilliseconds;

    public long EndedAtMilliseconds => Segments.Count == 0 ? 0 : Segments[^1].EndMilliseconds;

    public long ActiveMilliseconds => Segments.Sum(segment => segment.EndMilliseconds - segment.StartMilliseconds);

    /// Time spent in each mode and category, from the looks themselves.
    public Dictionary<(ActivityMode Mode, ActivityCategory Category), long> ModeTime { get; } = [];

    /// <summary>
    /// The identity with the mode the activity spent most of its time in. A
    /// video page read for its first few seconds and then watched for twenty
    /// minutes is a Watch activity.
    /// </summary>
    public PageIdentity DominantIdentity
    {
        get
        {
            if (ModeTime.Count == 0)
            {
                return Identity;
            }

            var (mode, category) = ModeTime.MaxBy(entry => entry.Value).Key;
            return Identity with { Mode = mode, Category = category };
        }
    }
}

/// <summary>
/// Groups a session's looks into activities using identity alone: which app
/// and which thing inside it. Screen content is never consulted, so a game's
/// menus, a canvas being painted, or a feed scrolling can never split an
/// activity.
/// </summary>
/// <remarks>
/// Three rules, applied in order to each stretch of time spent on one thing:
///   - shorter than <see cref="GlanceUnderMilliseconds"/>: a glance, recorded
///     inside whatever activity was running, which keeps running;
///   - the same thing as an activity that ended within
///     <see cref="ResumeWithinMilliseconds"/>: that activity resumes, and the
///     time in between is recorded as an interruption;
///   - otherwise a new activity.
/// </remarks>
public static class ActivitySegmenter
{
    public const long GlanceUnderMilliseconds = 20_000;

    public const long ResumeWithinMilliseconds = 300_000;

    /// <summary>
    /// A gap between two looks shorter than this belongs to the earlier one.
    /// Looks are only stored when something happens, and the slowest capture
    /// interval is a minute, so a quiet page is still "on screen" between them.
    /// </summary>
    public const long FillGapUpToMilliseconds = 90_000;

    /// <summary>
    /// The same allowance for captures stored before activities existed.
    /// Those carry no "last seen" time and were only stored when the screen
    /// changed, so a page read for a few minutes left one capture and then
    /// silence. Matches the old sessionizer's idle gap.
    /// </summary>
    public const long LegacyFillGapUpToMilliseconds = 300_000;

    private const int MaxPhases = 12;

    private sealed record Visit(
        PageIdentity Identity,
        long Start,
        long End,
        IReadOnlyList<ScanFacet> Facets,
        IReadOnlyList<PageIdentity> Identities)
    {
        public long Duration => End - Start;
    }

    /// <param name="endings">
    /// When recording stopped or the user went away. The last stretch before
    /// one runs up to it when the gap is short, so stopping a minute into a
    /// look still counts that minute.
    /// </param>
    /// <param name="closings">
    /// When an app was closed, by app key (lowercase process name). What was
    /// going on in it ends there: it runs no later than the close, and opening
    /// the app again starts something new instead of resuming it.
    /// </param>
    public static IReadOnlyList<ActivityDraft> Segment(
        IReadOnlyList<ScanFacet> facets,
        Func<ScanFacet, PageIdentity> identify,
        IReadOnlyList<long>? endings = null,
        IReadOnlyList<(long At, string AppKey)>? closings = null)
    {
        ArgumentNullException.ThrowIfNull(facets);
        ArgumentNullException.ThrowIfNull(identify);
        closings ??= [];
        var visits = Visits(facets, identify, endings ?? [], closings);
        if (visits.Count == 0)
        {
            return [];
        }

        // A glance needs something to be a glance from. A session made only
        // of short visits keeps them as activities.
        var hasLongVisit = visits.Any(visit => visit.Duration >= GlanceUnderMilliseconds);
        var drafts = new List<ActivityDraft>();
        var latestByKey = new Dictionary<string, ActivityDraft>(StringComparer.Ordinal);
        var pendingGlances = new List<ActivityGlance>();
        ActivityDraft? current = null;

        for (var position = 0; position < visits.Count; position++)
        {
            var visit = visits[position];
            // A glance is a look away that something else follows. The last
            // stretch of a session is where the user ended up, never a glance,
            // however short it measured.
            var isLast = position == visits.Count - 1;
            if (hasLongVisit && !isLast && visit.Duration < GlanceUnderMilliseconds && visit.Identity.Key != current?.Identity.Key)
            {
                var glance = new ActivityGlance(visit.Identity.AppName, visit.Identity.Subject, visit.Start, visit.End);
                if (current is null)
                {
                    pendingGlances.Add(glance);
                }
                else
                {
                    // The activity keeps running through a glance: its time
                    // is the activity's, the glance is only noted.
                    current.Glances.Add(glance);
                    var running = current.Segments[^1];
                    current.Segments[^1] = running with
                    {
                        EndMilliseconds = Math.Max(running.EndMilliseconds, visit.End)
                    };
                }

                continue;
            }

            ActivityDraft draft;
            if (latestByKey.TryGetValue(visit.Identity.Key, out var earlier)
                && visit.Start - earlier.EndedAtMilliseconds <= ResumeWithinMilliseconds
                && !ClosedBetween(closings, visit.Identity.AppKey, earlier.EndedAtMilliseconds, visit.Start))
            {
                draft = earlier;
                if (current is not null && !ReferenceEquals(current, earlier))
                {
                    draft.Events.Add(new ActivityEvent(
                        earlier.EndedAtMilliseconds,
                        "interrupted",
                        $"{current.Identity.Subject} · {Minutes(visit.Start - earlier.EndedAtMilliseconds)}"));
                }

                // Continuous with the earlier stretch when nothing else ran in
                // between (only glances): one segment, not two.
                var last = draft.Segments[^1];
                if (ReferenceEquals(current, earlier))
                {
                    draft.Segments[^1] = last with { EndMilliseconds = Math.Max(last.EndMilliseconds, visit.End) };
                }
                else
                {
                    draft.Segments.Add(new ActivitySegment(visit.Start, visit.End));
                }
            }
            else
            {
                draft = new ActivityDraft(visit.Identity);
                // Glances before the first activity (a launcher, a quick look)
                // lead into it, so it starts where they did.
                var start = drafts.Count == 0 && pendingGlances.Count > 0
                    ? Math.Min(visit.Start, pendingGlances[0].StartMilliseconds)
                    : visit.Start;
                draft.Segments.Add(new ActivitySegment(start, visit.End));
                draft.Glances.AddRange(pendingGlances);
                pendingGlances.Clear();
                drafts.Add(draft);
                latestByKey[visit.Identity.Key] = draft;
            }

            AddFacets(draft, visit.Facets);
            AddModeTime(draft, visit);
            current = draft;
        }

        return drafts;
    }

    private static bool ClosedBetween(
        IReadOnlyList<(long At, string AppKey)> closings,
        string appKey,
        long from,
        long to) =>
        closings.Any(close => close.At >= from && close.At <= to
            && string.Equals(close.AppKey, appKey, StringComparison.OrdinalIgnoreCase));

    private static List<Visit> Visits(
        IReadOnlyList<ScanFacet> facets,
        Func<ScanFacet, PageIdentity> identify,
        IReadOnlyList<long> endings,
        IReadOnlyList<(long At, string AppKey)> closings)
    {
        var ordered = facets
            .OrderBy(facet => facet.CapturedAtMilliseconds)
            .ThenBy(facet => facet.Id, StringComparer.Ordinal)
            .Select(facet => (Facet: facet, Identity: identify(facet)))
            .ToList();
        var runs = new List<(PageIdentity Identity, List<ScanFacet> Facets, List<PageIdentity> Identities)>();
        foreach (var (facet, identity) in ordered)
        {
            if (runs.Count > 0 && runs[^1].Identity.Key == identity.Key)
            {
                runs[^1].Facets.Add(facet);
                runs[^1].Identities.Add(identity);
            }
            else
            {
                runs.Add((identity, [facet], [identity]));
            }
        }

        var visits = new List<Visit>(runs.Count);
        for (var index = 0; index < runs.Count; index++)
        {
            var (identity, run, identities) = runs[index];
            var start = run[0].CapturedAtMilliseconds;
            var lastSeen = run.Max(facet => Math.Max(facet.LastSeenMilliseconds, facet.CapturedAtMilliseconds));
            var end = lastSeen;
            if (index + 1 < runs.Count)
            {
                var nextStart = runs[index + 1].Facets[0].CapturedAtMilliseconds;
                var allowance = run.Any(facet => facet.Change is null)
                    ? LegacyFillGapUpToMilliseconds
                    : FillGapUpToMilliseconds;
                end = nextStart - lastSeen <= allowance ? nextStart : lastSeen;
            }
            else
            {
                var ending = endings
                    .Where(at => at >= lastSeen)
                    .DefaultIfEmpty(long.MaxValue)
                    .Min();
                if (ending - lastSeen <= FillGapUpToMilliseconds)
                {
                    end = ending;
                }
            }

            // Closed before the fill-in time ran out: it ended at the close.
            var closedAt = closings
                .Where(close => close.At >= lastSeen && close.At < end
                    && string.Equals(close.AppKey, identity.AppKey, StringComparison.OrdinalIgnoreCase))
                .Select(close => close.At)
                .DefaultIfEmpty(long.MaxValue)
                .Min();
            if (closedAt != long.MaxValue)
            {
                end = closedAt;
            }

            visits.Add(new Visit(identity, start, Math.Max(start, end), run, identities));
        }

        return visits;
    }

    /// Each look holds until the next look in the same visit, or the visit's end.
    private static void AddModeTime(ActivityDraft draft, Visit visit)
    {
        for (var index = 0; index < visit.Facets.Count; index++)
        {
            var until = index + 1 < visit.Facets.Count
                ? visit.Facets[index + 1].CapturedAtMilliseconds
                : visit.End;
            var held = Math.Max(1_000, until - visit.Facets[index].CapturedAtMilliseconds);
            var identity = visit.Identities[index];
            var slot = (identity.Mode, identity.Category);
            draft.ModeTime[slot] = draft.ModeTime.GetValueOrDefault(slot) + held;
        }
    }

    private static void AddFacets(ActivityDraft draft, IReadOnlyList<ScanFacet> facets)
    {
        foreach (var facet in facets)
        {
            draft.ScanIds.Add(facet.Id);
            if (!string.IsNullOrWhiteSpace(facet.Phase)
                && draft.Phases.Count < MaxPhases
                && !draft.Phases.Contains(facet.Phase, StringComparer.OrdinalIgnoreCase))
            {
                draft.Phases.Add(facet.Phase);
            }

            if (facet.EventKind is { } kind)
            {
                var repeat = draft.Events.Any(existing =>
                    existing.Kind == kind
                    && Math.Abs(existing.AtMilliseconds - facet.CapturedAtMilliseconds) < 60_000);
                if (!repeat)
                {
                    draft.Events.Add(new ActivityEvent(
                        facet.CapturedAtMilliseconds,
                        kind,
                        kind is "confirmed" ? null : facet.DialogTitle));
                }
            }
        }
    }

    private static string Minutes(long milliseconds)
    {
        var minutes = (int)Math.Round(milliseconds / 60_000.0);
        return minutes < 1 ? "under a minute" : $"{minutes} min";
    }
}
