using System.Linq;
using System.Text.Json;

namespace Glint.Phase0.Core;

/// <summary>
/// Groups consecutive scans into activity sessions. Stateless by design:
/// the open session lives in the database (not memory) so one-shot CLI
/// invocations and the 1s scan loop share the same session state.
/// A boundary fires on window/process switch, content drift, or idle gap.
/// Closing a session produces exactly one summary call for the whole span.
/// </summary>
public sealed class SessionManager
{
    /// Maximum silence between two scans of the same session.
    public const long MaxGapMilliseconds = 60_000;

    /// Jaccard similarity floor for "still the same activity".
    public const double MinSimilarity = 0.3;

    private const int HeadTailCharacters = 2_000;

    private static readonly JsonSerializerOptions SessionJsonOptions = new();

    private readonly ISessionStore _sessions;
    private readonly IActivitySummarizer _summarizer;

    public SessionManager(ISessionStore sessions, IActivitySummarizer summarizer)
    {
        _sessions = sessions;
        _summarizer = summarizer;
    }

    public sealed record SessionTrackResult(string SessionId, ActivitySession? ClosedSession);

    /// <summary>
    /// Assigns a freshly summarized scan to its session, closing the previous
    /// session (with one summary call) when a boundary is crossed.
    /// </summary>
    public async Task<SessionTrackResult> TrackScanAsync(
        string scanId,
        string processName,
        string windowTitle,
        long capturedAtMilliseconds,
        string redactedText,
        CancellationToken cancellationToken = default)
    {
        var open = _sessions.GetOpenSession();
        if (open is not null
            && Continues(open, processName, windowTitle, capturedAtMilliseconds, redactedText))
        {
            var appended = open with
            {
                EndedAtMilliseconds = capturedAtMilliseconds,
                ScanIds = [.. open.ScanIds, scanId],
                TailText = Tail(redactedText)
            };
            _sessions.UpsertSession(appended);
            return new(appended.Id, null);
        }

        ActivitySession? closed = null;
        if (open is not null)
        {
            closed = await CloseAsync(open, cancellationToken).ConfigureAwait(false);
        }

        var created = new ActivitySession(
            Guid.NewGuid().ToString("N"),
            capturedAtMilliseconds,
            capturedAtMilliseconds,
            processName,
            windowTitle,
            [scanId],
            null,
            null,
            ActivitySessionStatus.Active,
            null,
            null,
            Head(redactedText),
            Tail(redactedText));
        _sessions.UpsertSession(created);
        return new(created.Id, closed);
    }

    private async Task<ActivitySession> CloseAsync(
        ActivitySession open,
        CancellationToken cancellationToken)
    {
        var combined = string.IsNullOrEmpty(open.TailText) || open.TailText == open.HeadText
            ? open.HeadText
            : open.HeadText + "\n...[continued]...\n" + open.TailText;

        // A failed close-summary must never fail the tick that triggered it;
        // the session still closes, just without model text. Cancellation
        // (Pause) propagates so in-flight work stays cancellable.
        ActivitySummary? summary = null;
        try
        {
            summary = await _summarizer.SummarizeAsync(
                    DateTimeOffset.FromUnixTimeMilliseconds(open.StartedAtMilliseconds),
                    open.ProcessName,
                    open.WindowTitle,
                    combined,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            summary = null;
        }

        var closed = open with
        {
            Label = summary?.Label,
            Summary = summary?.Summary,
            Status = ActivitySessionStatus.Closed,
            ImportantSignals = summary?.ImportantSignals,
            ReminderCandidate = summary?.ReminderCandidate
        };
        _sessions.UpsertSession(closed);
        return closed;
    }

    /// <summary>
    /// Boundary rule: same process and title, small time gap, similar words.
    /// Public and static for unit tests.
    /// </summary>
    public static bool Continues(
        ActivitySession open,
        string processName,
        string windowTitle,
        long capturedAtMilliseconds,
        string redactedText) =>
        string.Equals(open.ProcessName, processName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(open.WindowTitle, windowTitle, StringComparison.OrdinalIgnoreCase)
        && capturedAtMilliseconds - open.EndedAtMilliseconds <= MaxGapMilliseconds
        && Similarity(open.TailText, redactedText) >= MinSimilarity;

    /// <summary>
    /// Jaccard similarity over lowercase words (length >= 2).
    /// Public and static for unit tests.
    /// </summary>
    public static double Similarity(string left, string right)
    {
        static HashSet<string> Words(string text) =>
            text.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(word => word.Length >= 2)
                .Select(word => word.ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);

        var a = Words(left);
        var b = Words(right);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var intersection = a.Intersect(b).Count();
        return (double)intersection / (a.Count + b.Count - intersection);
    }

    internal static string Head(string text) =>
        text.Length <= HeadTailCharacters ? text : text[..HeadTailCharacters];

    internal static string Tail(string text) =>
        text.Length <= HeadTailCharacters ? text : text[^HeadTailCharacters..];

    internal static string SerializeScanIds(IReadOnlyList<string> scanIds) =>
        JsonSerializer.Serialize(scanIds, SessionJsonOptions);

    internal static IReadOnlyList<string> DeserializeScanIds(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, SessionJsonOptions)
                ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
