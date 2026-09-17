namespace Glint.Phase0.Core;

/// A stored capture as the sessionizer sees it: enough to group by time,
/// nothing else. Grouping never reads captured text, so it is deterministic
/// and costs no model calls.
public sealed record CaptureRow(
    string Id,
    long CapturedAtMilliseconds,
    string ProcessName,
    string WindowTitle);

/// A group of captures that belongs together, before it is written.
public sealed record SessionDraft(
    long StartedAtMilliseconds,
    long EndedAtMilliseconds,
    string ProcessName,
    string WindowTitle,
    IReadOnlyList<string> ScanIds);

/// <summary>
/// Groups captures into activity sessions in a batch, after the fact.
///
/// This replaces the per-scan boundary check that ran inside the capture
/// tick. That version closed a session whenever the window title changed, so
/// every browser tab became its own session, and it compared a 2,000
/// character tail against the new capture's full text, which meant long pages
/// almost never looked similar enough to continue.
///
/// Boundaries here are time only:
///   - a gap longer than <see cref="IdleGapMilliseconds"/>
///   - a session running longer than <see cref="MaxSessionMilliseconds"/>
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
    /// Silence longer than this ends a session.
    public const long IdleGapMilliseconds = 300_000;

    /// Hard cap so one long stretch does not become an unsummarizable blob.
    public const long MaxSessionMilliseconds = 2_700_000;

    /// The newest group is only sealed once nothing has been captured for
    /// this long; otherwise it is probably still growing.
    public const long QuietTailMilliseconds = 120_000;

    /// <summary>
    /// Groups captures into sealable sessions. Input need not be sorted.
    /// A group is only returned once it is finished; anything still in
    /// progress is left for a later pass.
    /// </summary>
    public static IReadOnlyList<SessionDraft> Cluster(
        IReadOnlyList<CaptureRow> captures,
        long nowMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(captures);
        if (captures.Count == 0)
        {
            return [];
        }

        var ordered = captures
            .OrderBy(capture => capture.CapturedAtMilliseconds)
            .ThenBy(capture => capture.Id, StringComparer.Ordinal)
            .ToList();

        var groups = new List<List<CaptureRow>>();
        var current = new List<CaptureRow> { ordered[0] };

        foreach (var capture in ordered.Skip(1))
        {
            var previous = current[^1];
            var gap = capture.CapturedAtMilliseconds - previous.CapturedAtMilliseconds;
            var span = capture.CapturedAtMilliseconds - current[0].CapturedAtMilliseconds;
            if (gap > IdleGapMilliseconds || span > MaxSessionMilliseconds)
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
        if (nowMilliseconds - last[^1].CapturedAtMilliseconds < QuietTailMilliseconds)
        {
            groups.RemoveAt(groups.Count - 1);
        }

        return groups.Select(ToDraft).ToList();
    }

    private static SessionDraft ToDraft(List<CaptureRow> group) =>
        new(
            group[0].CapturedAtMilliseconds,
            group[^1].CapturedAtMilliseconds,
            Dominant(group.Select(capture => capture.ProcessName)),
            Dominant(group.Select(capture => capture.WindowTitle)),
            group.Select(capture => capture.Id).ToList());

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
