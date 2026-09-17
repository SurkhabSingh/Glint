namespace Glint.Phase0.Core;

public enum ManualScanStatus
{
    Completed,
    ModelFailed
}

public sealed record ManualScanRecord(
    string Id,
    long CapturedAtMilliseconds,
    string ProcessName,
    string WindowTitle,
    string? Label,
    string? Summary,
    ManualScanStatus Status,
    string? Error,
    string ContentHash,
    string ModelId,
    int UiAutomationCharacters,
    int OcrCharacters,
    int Redactions,
    double CaptureMilliseconds,
    double OcrMilliseconds,
    double InferenceMilliseconds,
    string? ImportantSignals = null,
    string? ReminderCandidate = null,
    int GemmaContextCharacters = 0,
    string? RedactedInputText = null,
    string? RedactedUiAutomationText = null,
    string? RedactedOcrText = null,
    string? OcrLanguage = null,
    string? SessionId = null);

public sealed record ContextSearchResult(
    string Id,
    long CapturedAtMilliseconds,
    string ProcessName,
    string WindowTitle,
    string? Label,
    string? Summary,
    string? ImportantSignals,
    string? ReminderCandidate,
    string Snippet);

public enum ManualScanOutcomeKind
{
    Completed,
    ModelFailed,
    Unchanged,
    Suppressed,
    DroppedSecretFrame,
    Failed
}

public sealed record ManualScanOutcome(
    ManualScanOutcomeKind Kind,
    string Detail,
    ManualScanRecord? Record = null,
    SuppressReason? SuppressReason = null,
    // Worker processes started while producing this scan: 0 when nothing was
    // summarized, more than 1 when the summarizer stepped down its context
    // budget. Each start reloads the model, so this is the cost to watch.
    long WorkerStarts = 0);

public sealed record ActivitySummary(
    string Label,
    string Summary,
    string ModelId,
    TimeSpan Elapsed,
    string? ImportantSignals = null,
    string? ReminderCandidate = null,
    int ContextCharacters = LiteRtActivitySummarizer.DefaultContextCharacters);

public interface IManualScanStore
{
    bool ContainsManualScanContentHash(string contentHash);

    bool IsRepeatOfLastCapture(string contentHash);

    void SaveManualScan(RawCaptureEvent captureEvent, ManualScanRecord scan);

    IReadOnlyList<ManualScanRecord> GetRecentManualScans(int limit = 50);
}

public enum ActivitySessionStatus
{
    Active,
    Closed,
    OpenLoop
}

/// Whether a session left something outstanding.
public enum SessionOutcome
{
    /// No evidence either way. Shown as unknown, never as done: claiming a
    /// loop is closed when nothing says so is the one failure that would
    /// make the feature untrustworthy.
    Unknown,

    /// Something was left for later.
    Open,

    /// Nothing outstanding.
    Settled
}

/// Where a session's outcome came from, so a weaker source can never
/// overwrite a stronger one.
public enum SessionOutcomeSource
{
    None,

    /// Derived from the summary the model already produced. A proposal.
    Rule,

    /// A later session continued or superseded this one.
    Recurrence,

    /// The user said so. Absolute, and never overridden.
    User
}

public sealed record ActivitySession(
    string Id,
    long StartedAtMilliseconds,
    long EndedAtMilliseconds,
    string ProcessName,
    string WindowTitle,
    IReadOnlyList<string> ScanIds,
    string? Label,
    string? Summary,
    ActivitySessionStatus Status,
    string? ImportantSignals = null,
    string? ReminderCandidate = null,
    string HeadText = "",
    string TailText = "",
    // Too little was on screen to be worth a summary, so no model call was
    // made. Distinct from "not summarized yet", which is a pending session.
    bool IsMinor = false,
    SessionOutcome Outcome = SessionOutcome.Unknown,
    SessionOutcomeSource OutcomeSource = SessionOutcomeSource.None,
    long? OutcomeAtMilliseconds = null);

public interface ISessionStore
{
    ActivitySession? GetOpenSession();

    void UpsertSession(ActivitySession session);

    IReadOnlyList<ActivitySession> GetRecentSessions(int limit = 50);
}

public interface IActivitySummarizer
{
    string ModelId { get; }

    Task<ActivitySummary> SummarizeAsync(
        DateTimeOffset capturedAt,
        string processName,
        string windowTitle,
        string redactedText,
        CancellationToken cancellationToken = default);
}
