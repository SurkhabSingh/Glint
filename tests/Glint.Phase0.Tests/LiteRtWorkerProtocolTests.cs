using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class LiteRtWorkerProtocolTests
{
    [Fact]
    public void ReadyNoticeIsNotMistakenForAResponse()
    {
        Assert.True(LiteRtWorkerProtocol.IsNotification(
            """{"event":"ready","loadMs":197.7,"pid":1234,"maxNumTokens":4096}"""));
    }

    [Theory]
    [InlineData("""{"id":1,"ok":true,"text":"Ok","elapsedMilliseconds":344.8}""")]
    [InlineData("""{"id":1,"ok":false,"error":"RuntimeError","elapsedMilliseconds":21.5}""")]
    [InlineData("""{"id":2,"ok":true,"op":"ping","elapsedMilliseconds":0.01}""")]
    public void ResponsesAreNotTreatedAsNotices(string line)
    {
        Assert.False(LiteRtWorkerProtocol.IsNotification(line));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    public void UnparseableLinesAreLeftToTheCaller(string line)
    {
        // Reported as a protocol error rather than silently skipped, which
        // would hang the read loop.
        Assert.False(LiteRtWorkerProtocol.IsNotification(line));
    }

    [Theory]
    [InlineData("""{"id":1,"ok":true,"text":"Ok","elapsedMilliseconds":344.8}""")]
    [InlineData("""{"id":1,"ok":false,"error":"RuntimeError: boom","elapsedMilliseconds":21.5}""")]
    [InlineData("""{"id":2,"ok":true,"op":"ping","elapsedMilliseconds":0.01}""")]
    public void ResponseLinesAreRecognizedAsResponses(string line)
    {
        Assert.True(LiteRtWorkerProtocol.IsResponse(line));
        Assert.False(LiteRtWorkerProtocol.IsNoise(line));
    }

    [Theory]
    [InlineData("""{"event":"ready","loadMs":197.7,"pid":1234,"maxNumTokens":4096}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"level":"info","message":"loading shard 1/2"}""")]
    public void NonResponseLinesAreNotMistakenForResponses(string line)
    {
        Assert.False(LiteRtWorkerProtocol.IsResponse(line));
    }

    [Theory]
    [InlineData("""{"level":"info","message":"loading shard 1/2"}""")]
    [InlineData("""{"status":"ok"}""")]
    public void NativeJsonStatusLinesAreNoiseToBeSkipped(string line)
    {
        // A stray stdout line without "ok" must never deserialize into a
        // phantom Ok=false/Error=null failure ("an unknown error").
        Assert.True(LiteRtWorkerProtocol.IsNoise(line));
        Assert.False(LiteRtWorkerProtocol.IsNotification(line));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    public void UnparseableLinesAreNotNoise(string line)
    {
        // Corrupt bytes stay protocol errors so a broken stream fails fast
        // instead of hanging until the timeout.
        Assert.False(LiteRtWorkerProtocol.IsNoise(line));
    }

    [Fact]
    public void PreviewsAreBoundedAndNeverNull()
    {
        Assert.Equal("<empty>", LiteRtWorkerProtocol.Preview(null));
        Assert.Equal("<empty>", LiteRtWorkerProtocol.Preview(""));
        Assert.Equal("abc", LiteRtWorkerProtocol.Preview("abc"));
        Assert.EndsWith("...", LiteRtWorkerProtocol.Preview(new string('x', 500)));
    }

    [Fact]
    public void EmptyPromptsAreRejectedBeforeSpawningAWorker()
    {
        Assert.Throws<ArgumentException>(
            () => LiteRtWorkerProtocol.ValidateRequest(new("   ")));
    }

    [Fact]
    public void OversizePromptsAreRejectedBeforeSpawningAWorker()
    {
        // The native runtime rejects these within milliseconds anyway; the
        // summarizer relies on this to step down to a smaller budget.
        var prompt = new string('x', (LiteRtWorkerProtocol.MaxEstimatedInputTokens + 10) * 4);

        var error = Assert.Throws<InvalidOperationException>(
            () => LiteRtWorkerProtocol.ValidateRequest(new(prompt)));

        Assert.Contains("Prompt too large", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptsInsideTheBudgetAreAccepted()
    {
        var prompt = new string('x', (LiteRtWorkerProtocol.MaxEstimatedInputTokens - 10) * 4);

        LiteRtWorkerProtocol.ValidateRequest(new(prompt));
    }

    [Fact]
    public void TheSystemPromptCountsTowardsTheBudget()
    {
        var half = new string('x', LiteRtWorkerProtocol.MaxEstimatedInputTokens * 2);

        Assert.Throws<InvalidOperationException>(
            () => LiteRtWorkerProtocol.ValidateRequest(new(half, SystemPrompt: half + "xxxxx")));
    }
}
