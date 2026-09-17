using System.Security.Cryptography;
using System.Text;

namespace Glint.Phase0.Core;

public sealed class ManualScanCoordinator
{
    private readonly IForegroundWindowInspector _windowInspector;
    private readonly IUiAutomationService _automation;
    private readonly PrivacyGate _privacyGate;
    private readonly IOcrCaptureService _capture;
    private readonly DeterministicRedactor _redactor;
    private readonly IManualScanStore _store;

    public ManualScanCoordinator(
        IForegroundWindowInspector windowInspector,
        IUiAutomationService automation,
        PrivacyGate privacyGate,
        IOcrCaptureService capture,
        DeterministicRedactor redactor,
        IManualScanStore store)
    {
        _windowInspector = windowInspector;
        _automation = automation;
        _privacyGate = privacyGate;
        _capture = capture;
        _redactor = redactor;
        _store = store;
    }

    /// A label for a capture that costs no model call: the window title, or
    /// the app when the window has no title. Replaced for the whole stretch
    /// once its session is summarized.
    internal static string LiveLabel(string processName, string windowTitle) =>
        string.IsNullOrWhiteSpace(windowTitle) ? processName : windowTitle.Trim();

    public async Task<ManualScanOutcome> ScanAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var window = _windowInspector.Inspect();
            if (window is null)
            {
                return new(
                    ManualScanOutcomeKind.Suppressed,
                    "Windows did not report a foreground window.",
                    SuppressReason: SuppressReason.NoForegroundWindow);
            }

            var security = _automation.ProbeSecurity(window);
            var decision = _privacyGate.Evaluate(window, security);
            if (!decision.Allowed)
            {
                return new(
                    ManualScanOutcomeKind.Suppressed,
                    decision.Detail,
                    SuppressReason: decision.Reason);
            }

            var capturedAt = DateTimeOffset.UtcNow;
            var automationText = _automation.ExtractText(window);
            var ocr = await _capture.CaptureAndRecognizeAsync(window, cancellationToken)
                .ConfigureAwait(false);
            var combined = CapturePipeline.CombineText(automationText.Text, ocr.Text);
            if (combined.Length == 0)
            {
                return new(
                    ManualScanOutcomeKind.Failed,
                    ocr.Error is null
                        ? "Capture completed but no text was extracted."
                        : $"Capture completed without text. {ocr.Error}");
            }

            var dropReason = SecretSniffer.ShouldDrop(combined);
            if (dropReason is not null)
            {
                return new(
                    ManualScanOutcomeKind.DroppedSecretFrame,
                    $"Whole frame dropped before inference: {dropReason}.");
            }

            var redacted = _redactor.Redact(combined);
            var redactedUiAutomation = _redactor.Redact(automationText.Text);
            var redactedOcr = _redactor.Redact(ocr.Text);
            var contentHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(redacted.Text)));
            if (_store.ContainsManualScanContentHash(contentHash))
            {
                return new(
                    ManualScanOutcomeKind.Unchanged,
                    "The visible content has not changed since its last saved scan.");
            }

            var captureEvent = new RawCaptureEvent(
                Guid.NewGuid().ToString("N"),
                capturedAt.ToUnixTimeMilliseconds(),
                window.ProcessName,
                window.ExecutablePath,
                window.Title,
                contentHash,
                redacted.Text,
                redacted.Total);

            // No model call here. A capture used to cost one summary of its
            // own, measured at ~9.9 s of inference, so typing in a window
            // meant a model call per tick. Summaries are produced once per
            // session by SessionBuilder instead; until that runs, the card
            // carries a label derived from the window, which costs nothing
            // and is accurate.
            var scan = new ManualScanRecord(
                Guid.NewGuid().ToString("N"),
                capturedAt.ToUnixTimeMilliseconds(),
                window.ProcessName,
                window.Title,
                LiveLabel(window.ProcessName, window.Title),
                null,
                ManualScanStatus.Completed,
                null,
                contentHash,
                string.Empty,
                automationText.Text.Length,
                ocr.Text.Length,
                redacted.Total,
                ocr.CaptureElapsed.TotalMilliseconds,
                ocr.OcrElapsed.TotalMilliseconds,
                0,
                null,
                null,
                0,
                redacted.Text,
                redactedUiAutomation.Text,
                redactedOcr.Text,
                ocr.RecognizerLanguage,
                SessionId: null);
            _store.SaveManualScan(captureEvent, scan);

            return new(
                ManualScanOutcomeKind.Completed,
                "Screen text was redacted and stored.",
                scan);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return new(
                ManualScanOutcomeKind.Failed,
                $"{error.GetType().Name}: {error.Message}");
        }
    }
}
