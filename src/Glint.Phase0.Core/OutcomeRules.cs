namespace Glint.Phase0.Core;

/// <summary>
/// Decides whether a session left something outstanding, from the summary the
/// model has already produced. Costs no extra inference.
/// </summary>
/// <remarks>
/// The summarizer is asked for a REMINDER field holding "one specific
/// reminder candidate with its original date/time wording and owner/action",
/// which is a future action by definition. That is the open-loop signal, and
/// it is already paid for: measured against real sessions, five of eleven
/// carried one.
///
/// Two deliberate limits:
///
/// IMPORTANT is not used, even though it is often populated. The prompt asks
/// it to cover "commitments, requests, deadlines, decisions, blockers, risks"
/// — a mix of outstanding things and settled ones. A decision already taken
/// is not an open loop, so treating that field as evidence would be guessing.
///
/// Nothing here ever returns Settled. There is no reliable evidence of
/// completion in screen text: work stopping looks exactly like work being
/// finished. Settled comes from the user saying so, or later from a session
/// that supersedes this one.
/// </remarks>
public static class OutcomeRules
{
    /// <summary>
    /// Whether a model field actually says something.
    /// </summary>
    /// <remarks>
    /// The parser already nulls a literal "NONE" or "N/A", but the model
    /// usually declines in prose instead — "No explicit commitments,
    /// requests, deadlines... were found" — which is not null and would read
    /// as a signal. Six of eleven real sessions carried exactly that.
    /// </remarks>
    public static bool IsMeaningful(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        string[] declines =
        [
            "none",
            "n/a",
            "no explicit",
            "no commitments",
            "no specific",
            "no reminder",
            "no outstanding",
            "nothing"
        ];

        return !declines.Any(decline =>
            trimmed.StartsWith(decline, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The outcome a session's own summary supports. A user-set outcome is
    /// returned unchanged: rules never overwrite what the user said.
    /// </summary>
    public static (SessionOutcome Outcome, SessionOutcomeSource Source) Evaluate(
        ActivitySession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.OutcomeSource == SessionOutcomeSource.User)
        {
            return (session.Outcome, session.OutcomeSource);
        }

        // Source is Rule either way, including when nothing was found, so
        // "the rule ran and found nothing" is distinguishable from "not
        // looked at yet" and a session is not re-examined on every pass.
        return (
            IsMeaningful(session.ReminderCandidate)
                ? SessionOutcome.Open
                : SessionOutcome.Unknown,
            SessionOutcomeSource.Rule);
    }
}
