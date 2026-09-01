using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class ManualScanCoordinatorTests
{
    [Fact]
    public async Task RedactsBeforeGemmaAndPersistsCompletedSummary()
    {
        var summarizer = new FakeSummarizer();
        var store = new FakeStore();
        var coordinator = Coordinator(
            new(true, false, true, "known"),
            "api_key=topsecret",
            "Contact user@example.com",
            summarizer,
            store);

        var outcome = await coordinator.ScanAsync();

        Assert.Equal(ManualScanOutcomeKind.Completed, outcome.Kind);
        Assert.DoesNotContain("topsecret", summarizer.Input, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", summarizer.Input, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:", summarizer.Input, StringComparison.Ordinal);
        var saved = Assert.Single(store.Scans);
        Assert.Equal("Editing Windows capture", saved.Scan.Label);
        Assert.Equal("Meeting tomorrow at 10 PM.", saved.Scan.ImportantSignals);
        Assert.Equal("Remind the user about the 10 PM meeting tomorrow.", saved.Scan.ReminderCandidate);
        Assert.Contains("[REDACTED:", saved.Scan.RedactedInputText, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:", saved.Scan.RedactedUiAutomationText, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:", saved.Scan.RedactedOcrText, StringComparison.Ordinal);
        Assert.Equal(ManualScanStatus.Completed, saved.Scan.Status);
        Assert.Equal(saved.Scan.ContentHash, saved.Capture.ContentHash);
    }

    [Fact]
    public async Task ModelFailureStillPersistsTimestampedCapture()
    {
        var summarizer = new FakeSummarizer(new InvalidOperationException("runtime failed"));
        var store = new FakeStore();
        var coordinator = Coordinator(
            new(true, false, true, "known"),
            "Safe visible text",
            string.Empty,
            summarizer,
            store);

        var outcome = await coordinator.ScanAsync();

        Assert.Equal(ManualScanOutcomeKind.ModelFailed, outcome.Kind);
        var saved = Assert.Single(store.Scans);
        Assert.Equal(ManualScanStatus.ModelFailed, saved.Scan.Status);
        Assert.Contains("runtime failed", saved.Scan.Error, StringComparison.Ordinal);
        Assert.True(saved.Scan.CapturedAtMilliseconds > 0);
    }

    [Fact]
    public async Task UnchangedContentDoesNotRunGemmaOrCreateAnotherHistoryItem()
    {
        var summarizer = new FakeSummarizer();
        var store = new FakeStore();
        var coordinator = Coordinator(
            new(true, false, true, "known"),
            "Stable visible text",
            string.Empty,
            summarizer,
            store);

        var first = await coordinator.ScanAsync();
        var second = await coordinator.ScanAsync();

        Assert.Equal(ManualScanOutcomeKind.Completed, first.Kind);
        Assert.Equal(ManualScanOutcomeKind.Unchanged, second.Kind);
        Assert.Equal(1, summarizer.CallCount);
        Assert.Single(store.Scans);
    }

    [Fact]
    public async Task SuppressedWindowNeverReadsOrSummarizes()
    {
        var automation = new FakeAutomation(
            new(true, true, true, "password"),
            "must not be read");
        var capture = new FakeCapture("must not be captured");
        var summarizer = new FakeSummarizer();
        var store = new FakeStore();
        var coordinator = new ManualScanCoordinator(
            new FakeInspector(Window()),
            automation,
            new PrivacyGate(),
            capture,
            new DeterministicRedactor(),
            summarizer,
            store);

        var outcome = await coordinator.ScanAsync();

        Assert.Equal(ManualScanOutcomeKind.Suppressed, outcome.Kind);
        Assert.False(automation.TextWasRead);
        Assert.False(capture.WasCalled);
        Assert.Null(summarizer.Input);
        Assert.Empty(store.Scans);
    }

    [Fact]
    public void ParsesLegacyLabelAndSummaryShape()
    {
        var parsed = LiteRtActivitySummarizer.ParseResponse(
            "LABEL: Reviewing API changes\nSUMMARY: The user reviewed the retry implementation.");

        Assert.Equal("Reviewing API changes", parsed.Label);
        Assert.Equal("The user reviewed the retry implementation.", parsed.Summary);
        Assert.Null(parsed.ImportantSignals);
        Assert.Null(parsed.ReminderCandidate);
    }

    [Fact]
    public void PromotesExplicitBlockerWhenModelOmitsImportantField()
    {
        var parsed = LiteRtActivitySummarizer.ParseResponse(
            """
            LABEL: Production deployment blocked
            SUMMARY: Alex reported that deployment is blocked by a login issue and requested logs.
            IMPORTANT: NONE
            REMINDER: Send Alex the logs tonight
            """);

        Assert.Contains("blocked", parsed.ImportantSignals, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Send Alex the logs tonight", parsed.ReminderCandidate);
    }

    [Fact]
    public void ParsesConversationSignalsAndReminderCandidate()
    {
        var parsed = LiteRtActivitySummarizer.ParseResponse(
            """
            LABEL: Scheduling project meeting
            SUMMARY: The user and Alex discussed the release issue. They agreed to meet tomorrow at 10 PM.
            IMPORTANT: Release is blocked; Alex requested logs before the meeting
            REMINDER: Send Alex the logs and attend the meeting at 10 PM tomorrow
            """);

        Assert.Equal("Scheduling project meeting", parsed.Label);
        Assert.Contains("release issue", parsed.Summary, StringComparison.Ordinal);
        Assert.Equal(
            "Release is blocked; Alex requested logs before the meeting",
            parsed.ImportantSignals);
        Assert.Equal(
            "Send Alex the logs and attend the meeting at 10 PM tomorrow",
            parsed.ReminderCandidate);
    }

    [Fact]
    public void PromptKeepsMetadataAndLatestConversationWithinLargerContext()
    {
        var text =
            "Discord channels and navigation"
            + new string('x', 20_000)
            + "\nAlex: We have a meeting at 10pm tomorrow. Please send the release logs.";
        var prompt = LiteRtActivitySummarizer.BuildPrompt(
            new DateTimeOffset(2026, 6, 7, 12, 0, 0, TimeSpan.Zero),
            "Discord",
            "Project chat",
            text);

        Assert.Contains("Discord channels and navigation", prompt, StringComparison.Ordinal);
        Assert.Contains("meeting at 10pm tomorrow", prompt, StringComparison.Ordinal);
        Assert.Contains("middle context omitted", prompt, StringComparison.Ordinal);
        Assert.Contains("commitments, requests, deadlines", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetriesWithSmallerContextWhenLiteRtRejectsDensePrompt()
    {
        var generator = new FailingLargePromptGenerator();
        var summarizer = new LiteRtActivitySummarizer(generator, "test-gemma");
        var summary = await summarizer.SummarizeAsync(
            new DateTimeOffset(2026, 6, 7, 12, 0, 0, TimeSpan.Zero),
            "Codex",
            "Dense coding thread",
            "Visible useful content" + new string('x', 20_000));

        Assert.True(generator.CallCount >= 2);
        Assert.True(summary.ContextCharacters < LiteRtActivitySummarizer.DefaultContextCharacters);
        Assert.Equal("Dense Codex Thread", summary.Label);
    }

    private static ManualScanCoordinator Coordinator(
        AutomationSecurityProbe security,
        string automationText,
        string ocrText,
        FakeSummarizer summarizer,
        FakeStore store) =>
        new(
            new FakeInspector(Window()),
            new FakeAutomation(security, automationText),
            new PrivacyGate(),
            new FakeCapture(ocrText),
            new DeterministicRedactor(),
            summarizer,
            store);

    private static ForegroundWindowInfo Window() =>
        new(
            123,
            42,
            "Code",
            @"C:\Code.exe",
            "README.md - Visual Studio Code",
            new(0, 0, 1200, 800),
            false,
            false,
            true,
            false,
            false,
            true,
            false);

    private sealed class FakeInspector(ForegroundWindowInfo window) : IForegroundWindowInspector
    {
        public ForegroundWindowInfo? Inspect() => window;
    }

    private sealed class FakeAutomation(
        AutomationSecurityProbe security,
        string text) : IUiAutomationService
    {
        public bool TextWasRead { get; private set; }

        public AutomationSecurityProbe ProbeSecurity(ForegroundWindowInfo window) => security;

        public AutomationTextResult ExtractText(ForegroundWindowInfo window)
        {
            TextWasRead = true;
            return new(text, 1, false, TimeSpan.Zero);
        }
    }

    private sealed class FakeCapture(string text) : IOcrCaptureService
    {
        public bool WasCalled { get; private set; }

        public Task<OcrCaptureResult> CaptureAndRecognizeAsync(
            ForegroundWindowInfo window,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(
                new OcrCaptureResult(text, 100, 100, TimeSpan.Zero, TimeSpan.Zero));
        }
    }

    private sealed class FakeSummarizer(Exception? error = null) : IActivitySummarizer
    {
        public string ModelId => "test-gemma";

        public string? Input { get; private set; }

        public int CallCount { get; private set; }

        public Task<ActivitySummary> SummarizeAsync(
            DateTimeOffset capturedAt,
            string processName,
            string windowTitle,
            string redactedText,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Input = redactedText;
            return error is null
                ? Task.FromResult(
                    new ActivitySummary(
                        "Editing Windows capture",
                        "The user edited the Windows capture implementation.",
                        ModelId,
                        TimeSpan.FromMilliseconds(10),
                        "Meeting tomorrow at 10 PM.",
                        "Remind the user about the 10 PM meeting tomorrow."))
                : Task.FromException<ActivitySummary>(error);
        }
    }

    private sealed class FailingLargePromptGenerator : ILiteRtGenerator
    {
        public int CallCount { get; private set; }

        public Task<LiteRtGenerationResult> GenerateAsync(
            LiteRtGenerationRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (request.Prompt.Length > 10_000)
            {
                return Task.FromException<LiteRtGenerationResult>(
                    new InvalidOperationException(
                        "RuntimeError: litert_lm_conversation_send_message failed"));
            }

            return Task.FromResult(
                new LiteRtGenerationResult(
                    """
                    LABEL: Dense Codex Thread
                    SUMMARY: The user was reviewing a dense Codex coding thread.
                    IMPORTANT: NONE
                    REMINDER: NONE
                    """,
                    TimeSpan.FromMilliseconds(5),
                    TimeSpan.FromMilliseconds(10)));
        }
    }

    private sealed class FakeStore : IManualScanStore
    {
        public List<(RawCaptureEvent Capture, ManualScanRecord Scan)> Scans { get; } = [];

        public bool ContainsManualScanContentHash(string contentHash) =>
            Scans.Any(item => item.Scan.ContentHash == contentHash);

        public void SaveManualScan(RawCaptureEvent captureEvent, ManualScanRecord scan) =>
            Scans.Add((captureEvent, scan));

        public IReadOnlyList<ManualScanRecord> GetRecentManualScans(int limit = 50) =>
            Scans.Select(item => item.Scan).Take(limit).ToArray();
    }
}
