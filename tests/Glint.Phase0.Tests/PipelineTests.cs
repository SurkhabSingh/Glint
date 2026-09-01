using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class PipelineTests
{
    [Fact]
    public async Task SuppressedWindowNeverInvokesTextExtractionOrCapture()
    {
        var automation = new FakeAutomation(
            new(true, true, true, "password"),
            "must not be read");
        var capture = new FakeCapture("must not be captured");
        var store = new FakeStore();
        var pipeline = Pipeline(automation, capture, store);

        var result = await pipeline.CaptureOnceAsync();

        Assert.Equal(PipelineOutcomeKind.Suppressed, result.Kind);
        Assert.False(automation.TextWasRead);
        Assert.False(capture.WasCalled);
        Assert.Empty(store.Events);
    }

    [Fact]
    public async Task SecretFrameNeverReachesStorage()
    {
        var automation = new FakeAutomation(
            new(true, false, true, "known"),
            "-----BEGIN PRIVATE KEY-----");
        var capture = new FakeCapture("private material");
        var store = new FakeStore();
        var pipeline = Pipeline(automation, capture, store);

        var result = await pipeline.CaptureOnceAsync();

        Assert.Equal(PipelineOutcomeKind.DroppedSecretFrame, result.Kind);
        Assert.Empty(store.Events);
    }

    [Fact]
    public async Task StoresRedactedTextAndDeduplicatesSecondCapture()
    {
        var automation = new FakeAutomation(
            new(true, false, true, "known"),
            "api_key=topsecret");
        var capture = new FakeCapture("Contact user@example.com");
        var store = new FakeStore();
        var pipeline = Pipeline(automation, capture, store);

        var first = await pipeline.CaptureOnceAsync();
        var second = await pipeline.CaptureOnceAsync();

        Assert.Equal(PipelineOutcomeKind.Stored, first.Kind);
        Assert.Equal(PipelineOutcomeKind.Deduplicated, second.Kind);
        var captureEvent = Assert.Single(store.Events);
        Assert.DoesNotContain("topsecret", captureEvent.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", captureEvent.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CombinesAndDeduplicatesLines()
    {
        var combined = CapturePipeline.CombineText(
            "First line\nDuplicate line",
            "duplicate line\r\nSecond line");

        Assert.Equal(
            $"First line{Environment.NewLine}Duplicate line{Environment.NewLine}Second line",
            combined);
    }

    private static CapturePipeline Pipeline(
        FakeAutomation automation,
        FakeCapture capture,
        FakeStore store) =>
        new(
            new FakeInspector(Window()),
            automation,
            new PrivacyGate(),
            capture,
            new DeterministicRedactor(),
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

    private sealed class FakeStore : ICaptureEventStore
    {
        public List<RawCaptureEvent> Events { get; } = [];

        public bool ContainsContentHash(string contentHash) =>
            Events.Any(captureEvent => captureEvent.ContentHash == contentHash);

        public void Insert(RawCaptureEvent captureEvent) => Events.Add(captureEvent);
    }
}
