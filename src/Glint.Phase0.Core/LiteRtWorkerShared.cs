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

    /// True when a stdout line looks like a worker response: a JSON object
    /// carrying an "ok" boolean. Native libraries occasionally print their own
    /// JSON status lines to stdout mid-generation; without this check such a
    /// line deserializes with Ok=false and Error=null, surfacing as the
    /// useless "reported an unknown error" and killing a healthy worker.
    public static bool IsResponse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("ok", out var ok)
                && ok.ValueKind is JsonValueKind.True or JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// True when a line is parseable JSON but neither a notice nor a
    /// response: log noise from the native stack that must be skipped rather
    /// than mistaken for an answer.
    public static bool IsNoise(string line)
    {
        if (IsNotification(line) || IsResponse(line))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// System-wide gate so two Glint processes never load the ~2.5 GB model
    /// concurrently and fail each other with bare native errors. The Rust
    /// sidecar host serializes model calls within one process (model_lock);
    /// this covers the rest: a second host, a manual CLI invocation, or a
    /// health probe racing a summary run. Best effort: if the OS mutex cannot
    /// be created, generation proceeds without the gate rather than failing.
    internal sealed class LiteRtModelGate : IDisposable
    {
        private readonly Mutex? _mutex;
        private bool _held;
        private bool _disposed;

        private LiteRtModelGate(Mutex? mutex, bool held)
        {
            _mutex = mutex;
            _held = held;
        }

        public static async Task<LiteRtModelGate> AcquireAsync(
            TimeSpan maxWait,
            CancellationToken cancellationToken)
        {
            var mutex = Create();
            if (mutex is null)
            {
                return new LiteRtModelGate(null, false);
            }

            var deadline = DateTimeOffset.UtcNow + maxWait;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (mutex.WaitOne(TimeSpan.FromMilliseconds(250)))
                    {
                        return new LiteRtModelGate(mutex, true);
                    }
                }
                catch (AbandonedMutexException)
                {
                    // The previous holder died; ownership passed to us.
                    return new LiteRtModelGate(mutex, true);
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    mutex.Dispose();
                    throw new InvalidOperationException(
                        "LiteRT-LM worker is busy: another Glint process is using "
                        + "the local model. The summary was left for the next run.");
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }

        private static Mutex? Create()
        {
            // Global first so hosts in different sessions share it; Local as
            // the fallback where the global namespace is unavailable.
            foreach (var name in (string[])["Global\\GlintLiteRtModel", "Local\\GlintLiteRtModel"])
            {
                try
                {
                    return new Mutex(false, name);
                }
                catch (Exception)
                {
                    // UnauthorizedAccess, IOException (unsupported platform),
                    // or WaitHandleCannotBeOpenedException: try the next scope.
                }
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_held)
            {
                try
                {
                    _mutex?.ReleaseMutex();
                }
                catch (Exception)
                {
                    // Already released or abandoned; nothing to do.
                }
            }

            _mutex?.Dispose();
        }
    }

    /// Short single-line preview for error messages. Never carries more than
    /// a fragment, so prompts are not echoed into logs.
    public static string Preview(string? line, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(line))
        {
            return "<empty>";
        }

        var flattened = line.Replace('\r', ' ').Replace('\n', ' ');
        return flattened.Length <= maxLength
            ? flattened
            : flattened[..maxLength] + "...";
    }
}
