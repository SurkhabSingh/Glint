using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Glint.Phase0.Core;

/// Keeps one LiteRT-LM worker alive across requests so the model is loaded
/// once instead of per call, and unloads it again after an idle period so a
/// resident ~2.5 GB model does not sit in RAM between bursts of work.
///
/// Requests are serialized: the wire protocol is one JSON line in, one out,
/// so a second concurrent request would interleave. Anything that leaves an
/// exchange half-finished (timeout, cancellation, a malformed reply) kills
/// the worker rather than risk reading the previous answer next time; the
/// following request starts a fresh one.
public sealed class PersistentLiteRtWorker : ILiteRtGenerator, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private const int StderrTailLines = 20;

    private readonly string _pythonExecutable;
    private readonly string _workerScript;
    private readonly string _modelPath;
    private readonly string _backend;
    private readonly int _maxNumTokens;
    private readonly TimeSpan _idleUnloadAfter;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer? _idleTimer;
    private readonly object _stderrLock = new();
    private readonly Queue<string> _stderrTail = new();

    private Process? _process;
    private long _nextRequestId;
    private bool _disposed;

    public PersistentLiteRtWorker(
        string pythonExecutable,
        string workerScript,
        string modelPath,
        string backend = "cpu",
        int maxNumTokens = LiteRtWorkerProtocol.DefaultMaxNumTokens,
        TimeSpan? idleUnloadAfter = null)
    {
        _pythonExecutable = Path.GetFullPath(pythonExecutable);
        _workerScript = Path.GetFullPath(workerScript);
        _modelPath = Path.GetFullPath(modelPath);
        _backend = backend;
        _maxNumTokens = maxNumTokens;
        _idleUnloadAfter = idleUnloadAfter ?? TimeSpan.FromMinutes(5);
        if (_idleUnloadAfter > TimeSpan.Zero && _idleUnloadAfter != Timeout.InfiniteTimeSpan)
        {
            _idleTimer = new Timer(_ => UnloadIfIdle(), null, Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// True while a worker process is alive and holding the model.
    public bool IsLoaded
    {
        get
        {
            var process = _process;
            return process is { HasExited: false };
        }
    }

    public async Task<LiteRtGenerationResult> GenerateAsync(
        LiteRtGenerationRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LiteRtWorkerProtocol.ValidateRequest(request);
        ValidateFiles();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        // Cross-process: never load the model while another Glint process
        // holds it. Uses the caller's timeout as the maximum wait so a
        // contended run fails with a clear message instead of two overlapping
        // native loads failing each other opaquely.
        using var modelGate = await LiteRtWorkerProtocol.LiteRtModelGate
            .AcquireAsync(timeout, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            PauseIdleTimer();
            var processTimer = Stopwatch.StartNew();
            using var timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                var process = await EnsureWorkerAsync(timeoutSource.Token).ConfigureAwait(false);
                var id = ++_nextRequestId;
                var payload = JsonSerializer.Serialize(
                    new WorkerRequest(
                        id,
                        "generate",
                        request.Prompt,
                        request.SystemPrompt,
                        request.TopK,
                        request.TopP,
                        request.Temperature,
                        request.Seed),
                    JsonOptions);

                await process.StandardInput
                    .WriteLineAsync(payload.AsMemory(), timeoutSource.Token)
                    .ConfigureAwait(false);
                await process.StandardInput.FlushAsync(timeoutSource.Token).ConfigureAwait(false);

                var (response, rawLine) = await ReadResponseAsync(process, id, timeoutSource.Token)
                    .ConfigureAwait(false);
                if (!response.Ok)
                {
                    throw new InvalidOperationException(
                        ReportFailure(id, response.Error, rawLine));
                }

                processTimer.Stop();
                return new(
                    response.Text ?? string.Empty,
                    TimeSpan.FromMilliseconds(response.ElapsedMilliseconds),
                    processTimer.Elapsed);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                KillWorker();
                throw new TimeoutException(
                    $"LiteRT-LM worker did not answer within {timeout.TotalSeconds:0.#}s.");
            }
            catch
            {
                KillWorker();
                throw;
            }
        }
        finally
        {
            _gate.Release();
            ResumeIdleTimer();
        }
    }

    /// Round-trip a ping, starting the worker if needed. Used by smoke tests
    /// and health checks to load the model without generating anything.
    public async Task<bool> PingAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateFiles();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var modelGate = await LiteRtWorkerProtocol.LiteRtModelGate
            .AcquireAsync(timeout, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            PauseIdleTimer();
            using var timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                var process = await EnsureWorkerAsync(timeoutSource.Token).ConfigureAwait(false);
                var id = ++_nextRequestId;
                var payload = JsonSerializer.Serialize(
                    new WorkerRequest(id, "ping", null, null, null, null, null, null),
                    JsonOptions);
                await process.StandardInput
                    .WriteLineAsync(payload.AsMemory(), timeoutSource.Token)
                    .ConfigureAwait(false);
                await process.StandardInput.FlushAsync(timeoutSource.Token).ConfigureAwait(false);
                var (response, _) = await ReadResponseAsync(process, id, timeoutSource.Token)
                    .ConfigureAwait(false);
                return response.Ok;
            }
            catch
            {
                KillWorker();
                throw;
            }
        }
        finally
        {
            _gate.Release();
            ResumeIdleTimer();
        }
    }

    private async Task<Process> EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        var existing = _process;
        if (existing is { HasExited: false })
        {
            return existing;
        }

        KillWorker();

        var start = new ProcessStartInfo
        {
            FileName = _pythonExecutable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(_workerScript);
        start.ArgumentList.Add("--model");
        start.ArgumentList.Add(_modelPath);
        start.ArgumentList.Add("--backend");
        start.ArgumentList.Add(_backend);
        start.ArgumentList.Add("--max-num-tokens");
        start.ArgumentList.Add(_maxNumTokens.ToString(
            System.Globalization.CultureInfo.InvariantCulture));

        var process = Process.Start(start)
            ?? throw new InvalidOperationException("LiteRT-LM worker could not be started.");
        LiteRtWorkerMetrics.RecordStart();
        _process = process;

        lock (_stderrLock)
        {
            _stderrTail.Clear();
        }

        // stderr must be drained continuously: this process outlives many
        // requests, and a full pipe buffer would block the worker mid-answer.
        _ = Task.Run(() => DrainStderrAsync(process));

        // Wait for the ready notice so the model load is charged to startup
        // rather than to the first request's timeout budget.
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken)
                .ConfigureAwait(false);
            if (line is null)
            {
                throw new InvalidOperationException(
                    $"LiteRT-LM worker exited before signalling ready. {StderrTail()}".Trim());
            }

            if (LiteRtWorkerProtocol.IsNotification(line))
            {
                break;
            }
        }

        return process;
    }

    private async Task<(WorkerResponse Response, string RawLine)> ReadResponseAsync(
        Process process,
        long expectedId,
        CancellationToken cancellationToken)
    {
        // Native code can print its own JSON status lines to stdout
        // mid-generation. Those are noise: skip them (bounded) rather than
        // mistake one for a failed answer, which used to surface as the
        // bare "reported an unknown error" and kill a healthy worker.
        // Unparseable lines are still protocol errors, so a truly corrupt
        // stream fails fast instead of hanging until the timeout.
        var skippedNoise = 0;
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken)
                .ConfigureAwait(false);
            if (line is null)
            {
                throw new InvalidOperationException(
                    $"LiteRT-LM worker exited during a request. {StderrTail()}".Trim());
            }

            if (LiteRtWorkerProtocol.IsNotification(line))
            {
                continue;
            }

            if (LiteRtWorkerProtocol.IsNoise(line))
            {
                skippedNoise++;
                if (skippedNoise > 100)
                {
                    throw new InvalidDataException(
                        $"LiteRT-LM worker never answered request {expectedId}: "
                        + "stdout carried only log noise. "
                        + StderrTail());
                }

                continue;
            }

            WorkerResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<WorkerResponse>(line, JsonOptions);
            }
            catch (JsonException error)
            {
                throw new InvalidDataException(
                    $"LiteRT-LM worker returned a malformed response to request {expectedId}: "
                    + $"{LiteRtWorkerProtocol.Preview(line)}. {StderrTail()}".Trim(),
                    error);
            }

            if (response is null)
            {
                throw new InvalidDataException("LiteRT-LM worker returned no JSON response.");
            }

            // A mismatched id means the stream is out of step; the caller
            // kills the worker rather than trust a stale answer.
            if (response.Id is { } id && id != expectedId)
            {
                throw new InvalidDataException(
                    $"LiteRT-LM worker answered request {id}, expected {expectedId}.");
            }

            return (response, line);
        }
    }

    /// Failure text always carries the worker's stderr tail and never relies
    /// on the error field being present, so "an unknown error" with no detail
    /// can no longer reach the user. The "reported" wording is load-bearing:
    /// the summarizer retries messages containing it.
    private string ReportFailure(long requestId, string? error, string rawLine)
    {
        var detail = string.IsNullOrWhiteSpace(error)
            ? $"no error detail; raw response: {LiteRtWorkerProtocol.Preview(rawLine)}"
            : error.Trim();
        return $"LiteRT-LM worker reported a failure for request {requestId}: {detail} {StderrTail()}".Trim();
    }

    private async Task DrainStderrAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false)
                   is { } line)
            {
                lock (_stderrLock)
                {
                    _stderrTail.Enqueue(line);
                    while (_stderrTail.Count > StderrTailLines)
                    {
                        _stderrTail.Dequeue();
                    }
                }
            }
        }
        catch (Exception)
        {
            // The pipe closes when the worker exits; nothing to report.
        }
    }

    private string StderrTail()
    {
        lock (_stderrLock)
        {
            if (_stderrTail.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder("Worker output: ");
            builder.AppendJoin(" | ", _stderrTail);
            return builder.ToString();
        }
    }

    private void KillWorker()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone.
        }
        finally
        {
            process.Dispose();
        }
    }

    private void PauseIdleTimer() =>
        _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);

    private void ResumeIdleTimer()
    {
        if (_disposed)
        {
            return;
        }

        _idleTimer?.Change(_idleUnloadAfter, Timeout.InfiniteTimeSpan);
    }

    private void UnloadIfIdle()
    {
        // Never interrupt an in-flight request: if the gate is taken, that
        // request will re-arm the timer when it finishes.
        if (!_gate.Wait(0))
        {
            return;
        }

        try
        {
            KillWorker();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ValidateFiles()
    {
        if (!File.Exists(_pythonExecutable))
        {
            throw new FileNotFoundException("Python runtime is missing.", _pythonExecutable);
        }

        if (!File.Exists(_workerScript))
        {
            throw new FileNotFoundException("LiteRT-LM worker script is missing.", _workerScript);
        }

        if (!File.Exists(_modelPath))
        {
            throw new FileNotFoundException("Gemma model is missing.", _modelPath);
        }

        if (_backend is not ("cpu" or "gpu" or "npu"))
        {
            throw new ArgumentOutOfRangeException(nameof(_backend), "Unknown LiteRT-LM backend.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _idleTimer?.Dispose();

        // Best effort graceful stop so the worker closes the model cleanly;
        // a wedged worker is killed instead.
        var process = _process;
        if (process is { HasExited: false })
        {
            try
            {
                process.StandardInput.WriteLine(
                    JsonSerializer.Serialize(
                        new WorkerRequest(++_nextRequestId, "shutdown", null, null, null, null, null, null),
                        JsonOptions));
                process.StandardInput.Flush();
                process.StandardInput.Close();
                if (!process.WaitForExit(2_000))
                {
                    KillWorker();
                }
            }
            catch (Exception)
            {
                KillWorker();
            }
        }

        KillWorker();
        _gate.Dispose();
    }

    // ping and shutdown carry null generation fields; the worker only reads
    // them for op = generate.
    private sealed record WorkerRequest(
        long Id,
        string Op,
        string? Prompt,
        string? SystemPrompt,
        int? TopK,
        double? TopP,
        double? Temperature,
        int? Seed);

    private sealed record WorkerResponse(
        long? Id,
        bool Ok,
        string? Text,
        string? Error,
        double ElapsedMilliseconds);
}
