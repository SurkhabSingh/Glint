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
    Settled,

    /// A later session in the same thread replaced this one. Deliberately
    /// not Settled: the work was not finished, it moved on. Saying "done"
    /// here would be a claim nothing supports.
    Superseded
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
    long? OutcomeAtMilliseconds = null,
    // Sessions carrying the same outstanding thing. Null when nothing was
    // left outstanding, or when nothing matched confidently.
    string? ThreadId = null);

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

/// One agent conversation. Titles are derived from the first question, never
/// model-generated, so opening a chat costs no inference.
public sealed record ChatThread(
    string Id,
    string Title,
    string Scope,
    long CreatedAtMilliseconds,
    long UpdatedAtMilliseconds);

/// One turn inside a chat thread. Citations are stored as JSON so a reopened
/// chat renders its evidence exactly as answered, independent of later data.
public sealed record ChatMessage(
    string Id,
    string ThreadId,
    string Role,
    string Text,
    string CitationsJson,
    int ScopedCount,
    long CreatedAtMilliseconds);

/// Chat roles, stored as plain strings. Kept small on purpose: user turns,
/// agent answers, and failed answers.
public static class ChatRoles
{
    public const string User = "user";
    public const string Agent = "agent";
    public const string Error = "error";

    public static bool IsValid(string? role) =>
        role is User or Agent or Error;
}

/// Store operations for agent chat history. Chat text lives only in this
/// encrypted store: never in metrics, logs, or the timeline feed.
public interface IChatStore
{
    ChatThread CreateChatThread(string title, string scope, long atMilliseconds);

    IReadOnlyList<ChatThread> GetRecentChatThreads(int limit = 50);

    ChatThread? GetChatThread(string id);

    void RenameChatThread(string id, string title, long atMilliseconds);

    void DeleteChatThread(string id);

    ChatMessage AppendChatMessage(
        string threadId,
        string role,
        string text,
        string citationsJson,
        int scopedCount,
        long atMilliseconds);

    IReadOnlyList<ChatMessage> GetChatMessages(string threadId, int limit = 200);
}
