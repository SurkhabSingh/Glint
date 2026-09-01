using System.Text.Json.Serialization;

namespace Glint.Phase0.Core;

public readonly record struct WindowBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
}

public sealed record ForegroundWindowInfo(
    [property: JsonIgnore]
    nint Handle,
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    string Title,
    WindowBounds Bounds,
    bool IsMinimized,
    bool IsElevated,
    bool ElevationDetermined,
    bool IsDisplayProtected,
    bool IsSecureDesktop,
    bool DesktopStateDetermined,
    bool IsSelf);

public sealed record AutomationSecurityProbe(
    bool Determined,
    bool IsPassword,
    bool BelongsToForegroundProcess,
    string Detail);

public sealed record AutomationTextResult(
    string Text,
    int NodesVisited,
    bool Truncated,
    TimeSpan Elapsed,
    string? Error = null);

public enum SuppressReason
{
    NoForegroundWindow,
    SelfCapture,
    Minimized,
    SecureDesktop,
    DesktopStateUnknown,
    ElevatedProcess,
    ElevationStateUnknown,
    DisplayProtected,
    PasswordField,
    AutomationStateUnknown,
    BlocklistedApplication,
    PrivateBrowsing,
    SensitiveWindowTitle
}

public sealed record PrivacyDecision(
    bool Allowed,
    SuppressReason? Reason,
    string Detail)
{
    public static PrivacyDecision Allow() => new(true, null, "capture allowed");

    public static PrivacyDecision Suppress(SuppressReason reason, string detail) =>
        new(false, reason, detail);
}

public sealed record RedactionResult(
    string Text,
    IReadOnlyDictionary<string, int> Counts)
{
    public int Total => Counts.Values.Sum();
}

public enum PipelineOutcomeKind
{
    Suppressed,
    DroppedSecretFrame,
    Deduplicated,
    Stored,
    Failed
}

public sealed record PipelineOutcome(
    PipelineOutcomeKind Kind,
    string Detail,
    string? EventId = null,
    SuppressReason? SuppressReason = null,
    int UiAutomationCharacters = 0,
    int OcrCharacters = 0,
    int Redactions = 0,
    TimeSpan? CaptureElapsed = null,
    TimeSpan? OcrElapsed = null);

public sealed record OcrCaptureResult(
    string Text,
    int Width,
    int Height,
    TimeSpan CaptureElapsed,
    TimeSpan OcrElapsed,
    string? Error = null,
    string? RecognizerLanguage = null);

public sealed record GraphicsDeviceCompatibility(
    bool HardwareAvailable,
    bool SoftwareAvailable,
    string? HardwareError,
    string? SoftwareError);

public sealed record CompatibilityCheck(
    string Id,
    bool Passed,
    bool Required,
    string Detail);

public sealed record WindowsCompatibilityReport(
    string OsDescription,
    string OsVersion,
    int OsBuild,
    string OsArchitecture,
    string ProcessArchitecture,
    long PhysicalMemoryBytes,
    long AvailableDiskBytes,
    bool ReadyForCoreCapture,
    bool ReadyForLocalModels,
    IReadOnlyList<CompatibilityCheck> Checks);

public sealed record StorageDiagnostics(
    string SqlCipherVersion,
    string SqliteVersion,
    bool Fts5Available,
    bool SqliteVecAvailable,
    string DatabasePath);

public sealed record VectorSearchResult(long RowId, double Distance);

public sealed record RawCaptureEvent(
    string Id,
    long TimestampMilliseconds,
    string ProcessName,
    string? ExecutablePath,
    string WindowTitle,
    string ContentHash,
    string Text,
    int Redactions);
