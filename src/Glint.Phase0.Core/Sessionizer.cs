namespace Glint.Phase0.Core;

/// A stored capture as the sessionizer sees it: enough to group by time,
/// nothing else. Grouping never reads captured text, so it is deterministic
/// and costs no model calls.
public sealed record CaptureRow(
    string Id,
    long CapturedAtMilliseconds,
    string ProcessName,
    string WindowTitle,
    long? LastSeenMilliseconds = null)
{
    /// When the record last saw its window: a game or video is one record
    /// extended for as long as it stays in front.
    public long EndMilliseconds => Math.Max(CapturedAtMilliseconds, LastSeenMilliseconds ?? CapturedAtMilliseconds);
}

/// <summary>
/// Evidence of a boundary. Kinds: `user.away`, `user.returned`,
/// `user.locked`, `user.unlocked`, `system.sleep`, `system.resumed`,
/// `system.shutdown`, `run.started`, `run.stopped`, and `app.closed`, whose
/// <see cref="Detail"/> names the app (its lowercase process name).
/// </summary>
public sealed record ActivityMarker(long TimestampMilliseconds, string Kind, string? Detail = null)
{
    /// Kinds the database accepts.
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "run.started", "run.stopped", "user.away", "user.returned",
        "user.locked", "user.unlocked", "system.sleep", "system.resumed",
        "system.shutdown", "app.closed"
    };

    /// Stopping the recording ends the sitting outright, however short the gap after it.
    public bool EndsTheSitting => Kind == "run.stopped";

    /// Marks that end a stretch of work, as opposed to resuming one: the user
    /// left, locked the PC, it slept or shut down, or recording stopped.
    public bool EndsAStretch =>
        Kind is "user.away" or "run.stopped" or "user.locked" or "system.sleep" or "system.shutdown";

    /// An app was closed: whatever was going on in it is over.
    public bool ClosesApp => Kind == "app.closed" && !string.IsNullOrWhiteSpace(Detail);
}

/// A group of captures that belongs together.
public sealed record SessionDraft(
    long StartedAtMilliseconds,
    long EndedAtMilliseconds,
    string ProcessName,
    string WindowTitle,
    IReadOnlyList<string> ScanIds,
    // The newest group, quiet for less than the quiet tail: probably still growing.
    bool StillOpen = false);

/// <summary>
/// Groups captures into activity sessions in a batch, after the fact.
///
/// This replaces the per-scan boundary check that ran inside the capture
/// tick. That version closed a session whenever the window title changed, so
/// every browser tab became its own session, and it compared a 2,000
/// character tail against the new capture's full text, which meant long pages
/// almost never looked similar enough to continue.
///
/// A session is one sitting at the computer, however long it runs: a
/// three-hour game is one session. It ends only when something ends the
/// sitting:
///   - the user left, locked the PC, it slept or shut down, or recording
///     stopped, followed by a gap of at least <see cref="IdleGapMilliseconds"/>
///   - a gap longer than <see cref="UnexplainedGapMilliseconds"/> with no
///     explanation, the fallback for a crash or history without markers
///
/// Neither the window title nor the app is a boundary, so replying in Outlook
/// and then finishing in Gmail stays one session, which is how people
/// describe the work. The most frequent app and title are recorded as the
/// session's own, and the newest group is left alone until it has been quiet
/// for <see cref="QuietTailMilliseconds"/> so a session still in progress is
/// not sealed early.
/// </summary>
public static class Sessionizer
{
    /// <summary>
    /// A gap this long only ends a session when something explains it — the
    /// user went away, or recording stopped.
    /// </summary>
    /// <remarks>
    /// A gap on its own says nothing. Because a capture is only stored when
    /// the screen changes, reading one page for twenty minutes produces a
    /// single capture and then silence, which is indistinguishable by time
    /// alone from having walked out of the room. Splitting on the gap turned
    /// one long read into several sessions.
    /// </remarks>
    public const long IdleGapMilliseconds = 300_000;

    /// <summary>
    /// A gap this long ends a session even with nothing to explain it.
    /// </summary>
    /// <remarks>
    /// The fallback for missing evidence: a crash leaves no stop marker, and
    /// history recorded before markers existed has none at all. Without this,
    /// such a store would collapse into one enormous session.
    /// </remarks>
    public const long UnexplainedGapMilliseconds = 1_800_000;

    /// The newest group is only sealed once nothing has been captured for
    /// this long; otherwise it is probably still growing. Longer than the
    /// window in which a look still extends the previous record, so a record
    /// is never sealed and then extended.
    public const long QuietTailMilliseconds = 200_000;

    /// <summary>
    /// Groups captures into finished sessions. Input need not be sorted.
    /// The newest group is left out while it may still be growing.
    /// </summary>
    public static IReadOnlyList<SessionDraft> Cluster(
        IReadOnlyList<CaptureRow> captures,
        long nowMilliseconds,
        IReadOnlyList<ActivityMarker>? markers = null) =>
        Group(captures, nowMilliseconds, markers).Where(draft => !draft.StillOpen).ToList();

    /// <summary>
    /// Groups captures into sessions, the one still in progress included and
    /// flagged <see cref="SessionDraft.StillOpen"/>. Input need not be sorted.
    /// </summary>
    public static IReadOnlyList<SessionDraft> Group(
        IReadOnlyList<CaptureRow> captures,
        long nowMilliseconds,
        IReadOnlyList<ActivityMarker>? markers = null)
    {
        ArgumentNullException.ThrowIfNull(captures);
        if (captures.Count == 0)
        {
            return [];
        }

        var breaks = (markers ?? [])
            .Where(marker => marker.EndsAStretch)
            .Select(marker => marker.TimestampMilliseconds)
            .OrderBy(timestamp => timestamp)
            .ToList();
        // Stopping the recording ends the sitting outright, however soon the
        // next one starts.
        var stops = (markers ?? [])
            .Where(marker => marker.EndsTheSitting)
            .Select(marker => marker.TimestampMilliseconds)
            .ToList();

        var ordered = captures
            .OrderBy(capture => capture.CapturedAtMilliseconds)
            .ThenBy(capture => capture.Id, StringComparer.Ordinal)
            .ToList();

        var groups = new List<List<CaptureRow>>();
        var current = new List<CaptureRow> { ordered[0] };

        foreach (var capture in ordered.Skip(1))
        {
            var previous = current.Max(row => row.EndMilliseconds);
            var gap = capture.CapturedAtMilliseconds - previous;
            if (IsBoundary(previous, capture.CapturedAtMilliseconds, breaks, stops))
            {
                groups.Add(current);
                current = [capture];
                continue;
            }

            current.Add(capture);
        }

        groups.Add(current);

        // The last group is the only one that can still be growing: every
        // earlier group is already followed by a boundary.
        var last = groups[^1];
        var lastOpen = nowMilliseconds - last.Max(row => row.EndMilliseconds) < QuietTailMilliseconds;
        return groups
            .Select((group, index) => ToDraft(group, lastOpen && index == groups.Count - 1))
            .ToList();
    }

    /// <summary>
    /// Whether a sitting ends between a capture last seen at
    /// <paramref name="previousEnd"/> and one starting at <paramref name="nextStart"/>.
    /// </summary>
    public static bool IsBoundary(long previousEnd, long nextStart, IReadOnlyList<ActivityMarker> markers) =>
        IsBoundary(
            previousEnd,
            nextStart,
            markers.Where(marker => marker.EndsAStretch).Select(marker => marker.TimestampMilliseconds).ToList(),
            markers.Where(marker => marker.EndsTheSitting).Select(marker => marker.TimestampMilliseconds).ToList());

    private static bool IsBoundary(long previousEnd, long nextStart, IReadOnlyList<long> breaks, IReadOnlyList<long> stops)
    {
        var gap = nextStart - previousEnd;
        var explained = gap >= IdleGapMilliseconds
            && breaks.Any(at => at > previousEnd && at < nextStart);
        var stopped = stops.Any(at => at >= previousEnd && at <= nextStart);
        return explained || stopped || gap > UnexplainedGapMilliseconds;
    }

    private static SessionDraft ToDraft(List<CaptureRow> group, bool stillOpen) =>
        new(
            group[0].CapturedAtMilliseconds,
            group.Max(row => row.EndMilliseconds),
            Dominant(group.Select(capture => capture.ProcessName)),
            Dominant(group.Select(capture => capture.WindowTitle)),
            group.Select(capture => capture.Id).ToList(),
            stillOpen);

    /// Most frequent value, breaking ties toward the one seen last so a
    /// session that drifted is named after where it ended up.
    private static string Dominant(IEnumerable<string> values)
    {
        var ranked = new Dictionary<string, (int Count, int LastIndex)>(StringComparer.Ordinal);
        var index = 0;
        foreach (var value in values)
        {
            var key = value ?? string.Empty;
            ranked[key] = ranked.TryGetValue(key, out var seen)
                ? (seen.Count + 1, index)
                : (1, index);
            index++;
        }

        return ranked
            .OrderByDescending(entry => entry.Value.Count)
            .ThenByDescending(entry => entry.Value.LastIndex)
            .First()
            .Key;
    }
}
