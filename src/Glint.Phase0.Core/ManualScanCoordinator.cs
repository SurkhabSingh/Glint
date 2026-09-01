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
    private readonly IActivitySummarizer _summarizer;
    private readonly IManualScanStore _store;

    public ManualScanCoordinator(
        IForegroundWindowInspector windowInspector,
        IUiAutomationService automation,
        PrivacyGate privacyGate,
        IOcrCaptureService capture,
        DeterministicRedactor redactor,
        IActivitySummarizer summarizer,
        IManualScanStore store)
    {
        _windowInspector = windowInspector;
        _automation = automation;
        _privacyGate = privacyGate;
        _capture = capture;
        _redactor = redactor;
        _summarizer = summarizer;
        _store = store;
    }

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

            ActivitySummary? summary = null;
            string? modelError = null;
            try
            {
                summary = await _summarizer.SummarizeAsync(
                        capturedAt,
                        window.ProcessName,
                        window.Title,
                        redacted.Text,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                modelError = $"{error.GetType().Name}: {error.Message}";
            }

            var scan = new ManualScanRecord(
                Guid.NewGuid().ToString("N"),
                capturedAt.ToUnixTimeMilliseconds(),
                window.ProcessName,
                window.Title,
                summary?.Label,
                summary?.Summary,
                summary is null ? ManualScanStatus.ModelFailed : ManualScanStatus.Completed,
                modelError,
                contentHash,
                summary?.ModelId ?? _summarizer.ModelId,
                automationText.Text.Length,
                ocr.Text.Length,
                redacted.Total,
                ocr.CaptureElapsed.TotalMilliseconds,
                ocr.OcrElapsed.TotalMilliseconds,
                summary?.Elapsed.TotalMilliseconds ?? 0,
                summary?.ImportantSignals,
                summary?.ReminderCandidate,
                summary?.ContextCharacters ?? 0,
                redacted.Text,
                redactedUiAutomation.Text,
                redactedOcr.Text,
                ocr.RecognizerLanguage);
            _store.SaveManualScan(captureEvent, scan);

            return summary is null
                ? new(
                    ManualScanOutcomeKind.ModelFailed,
                    $"OCR was captured and stored, but Gemma failed: {modelError}",
                    scan)
                : new(
                    ManualScanOutcomeKind.Completed,
                    "OCR was redacted, summarized locally, and stored.",
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
