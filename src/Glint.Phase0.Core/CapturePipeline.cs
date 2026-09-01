using System.Security.Cryptography;
using System.Text;

namespace Glint.Phase0.Core;

public sealed class CapturePipeline
{
    private readonly IForegroundWindowInspector _windowInspector;
    private readonly IUiAutomationService _automation;
    private readonly PrivacyGate _privacyGate;
    private readonly IOcrCaptureService _capture;
    private readonly DeterministicRedactor _redactor;
    private readonly ICaptureEventStore _store;

    public CapturePipeline(
        IForegroundWindowInspector windowInspector,
        IUiAutomationService automation,
        PrivacyGate privacyGate,
        IOcrCaptureService capture,
        DeterministicRedactor redactor,
        ICaptureEventStore store)
    {
        _windowInspector = windowInspector;
        _automation = automation;
        _privacyGate = privacyGate;
        _capture = capture;
        _redactor = redactor;
        _store = store;
    }

    public async Task<PipelineOutcome> CaptureOnceAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var window = _windowInspector.Inspect();
            if (window is null)
            {
                return new(
                    PipelineOutcomeKind.Suppressed,
                    "No foreground window.",
                    SuppressReason: SuppressReason.NoForegroundWindow);
            }

            // Security properties are inspected before any UIA text or pixels are read.
            var security = _automation.ProbeSecurity(window);
            var decision = _privacyGate.Evaluate(window, security);
            if (!decision.Allowed)
            {
                return new(
                    PipelineOutcomeKind.Suppressed,
                    decision.Detail,
                    SuppressReason: decision.Reason);
            }

            var automationText = _automation.ExtractText(window);
            var ocr = await _capture.CaptureAndRecognizeAsync(window, cancellationToken)
                .ConfigureAwait(false);
            var combined = CombineText(automationText.Text, ocr.Text);
            if (combined.Length == 0)
            {
                return new(
                    PipelineOutcomeKind.Failed,
                    "Capture completed but no text was extracted.",
                    UiAutomationCharacters: automationText.Text.Length,
                    OcrCharacters: ocr.Text.Length,
                    CaptureElapsed: ocr.CaptureElapsed,
                    OcrElapsed: ocr.OcrElapsed);
            }

            var secretReason = SecretSniffer.ShouldDrop(combined);
            if (secretReason is not null)
            {
                return new(
                    PipelineOutcomeKind.DroppedSecretFrame,
                    $"Whole frame dropped: {secretReason}.",
                    UiAutomationCharacters: automationText.Text.Length,
                    OcrCharacters: ocr.Text.Length,
                    CaptureElapsed: ocr.CaptureElapsed,
                    OcrElapsed: ocr.OcrElapsed);
            }

            var redacted = _redactor.Redact(combined);
            var contentHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(redacted.Text)));
            if (_store.ContainsContentHash(contentHash))
            {
                return new(
                    PipelineOutcomeKind.Deduplicated,
                    "Redacted content is unchanged.",
                    UiAutomationCharacters: automationText.Text.Length,
                    OcrCharacters: ocr.Text.Length,
                    Redactions: redacted.Total,
                    CaptureElapsed: ocr.CaptureElapsed,
                    OcrElapsed: ocr.OcrElapsed);
            }

            var eventId = Guid.NewGuid().ToString();
            _store.Insert(new(
                eventId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                window.ProcessName,
                window.ExecutablePath,
                window.Title,
                contentHash,
                redacted.Text,
                redacted.Total));
            return new(
                PipelineOutcomeKind.Stored,
                "Redacted text stored.",
                eventId,
                UiAutomationCharacters: automationText.Text.Length,
                OcrCharacters: ocr.Text.Length,
                Redactions: redacted.Total,
                CaptureElapsed: ocr.CaptureElapsed,
                OcrElapsed: ocr.OcrElapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return new(PipelineOutcomeKind.Failed, $"{error.GetType().Name}: {error.Message}");
        }
    }

    public static string CombineText(string automationText, string ocrText)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { automationText, ocrText })
        {
            foreach (var line in source.Split(
                         ['\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = string.Join(
                    ' ',
                    line.Split(
                        (char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                if (normalized.Length >= 2 && seen.Add(normalized))
                {
                    lines.Add(normalized);
                }
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
