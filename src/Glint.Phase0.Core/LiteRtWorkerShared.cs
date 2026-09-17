using System.Text.Json;

namespace Glint.Phase0.Core;

/// Worker process starts across every client. Each start reloads the model
/// (~200 ms engine load on top of Python startup), so this is what the
/// persistent worker is meant to drive down.
public static class LiteRtWorkerMetrics
{
    private static long _startCount;

    public static long StartCount => System.Threading.Interlocked.Read(ref _startCount);

    internal static void RecordStart() =>
        System.Threading.Interlocked.Increment(ref _startCount);
}

/// Rules shared by the one-shot and persistent workers, so the two cannot
/// drift apart on prompt limits or line framing.
internal static class LiteRtWorkerProtocol
{
    /// max_num_tokens is the whole budget, input plus output. Too small and
    /// send_message fails outright instead of truncating: measured on
    /// gemma-4-e2b, 512 fails and 2048 succeeds for a short prompt.
    public const int DefaultMaxNumTokens = 4096;

    /// The native runtime rejects long inputs within milliseconds regardless
    /// of max_num_tokens, so fail early with a clear message instead.
    public const int MaxEstimatedInputTokens = 1_600;

    public static void ValidateRequest(LiteRtGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("Prompt cannot be empty.", nameof(request));
        }

        var estimatedInputTokens =
            (request.Prompt.Length + (request.SystemPrompt?.Length ?? 0)) / 4;
        if (estimatedInputTokens > MaxEstimatedInputTokens)
        {
            throw new InvalidOperationException(
                $"Prompt too large for the local worker: ~{estimatedInputTokens} estimated " +
                $"input tokens exceed the ~1,600-token budget. Shrink context and retry.");
        }
    }

    /// Worker notices (currently only the ready line) carry "event" and none
    /// of the response fields. Unparseable lines are left to the caller,
    /// which reports them as a protocol error.
    public static bool IsNotification(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("event", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
