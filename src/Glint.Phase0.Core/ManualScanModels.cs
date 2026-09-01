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
    string? OcrLanguage = null);

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
    SuppressReason? SuppressReason = null);

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

    void SaveManualScan(RawCaptureEvent captureEvent, ManualScanRecord scan);

    IReadOnlyList<ManualScanRecord> GetRecentManualScans(int limit = 50);
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
