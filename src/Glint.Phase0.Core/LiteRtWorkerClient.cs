using System.Diagnostics;
using System.Text.Json;

namespace Glint.Phase0.Core;

public sealed record LiteRtGenerationRequest(
    string Prompt,
    string? SystemPrompt = null,
    int? TopK = null,
    double? TopP = null,
    double? Temperature = 0,
    int? Seed = 1);

public sealed record LiteRtGenerationResult(
    string Text,
    TimeSpan GenerationElapsed,
    TimeSpan ProcessElapsed);

public interface ILiteRtGenerator
{
    Task<LiteRtGenerationResult> GenerateAsync(
        LiteRtGenerationRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class LiteRtWorkerClient : ILiteRtGenerator
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly string _pythonExecutable;
    private readonly string _workerScript;
    private readonly string _modelPath;
    private readonly string _backend;
    private readonly int _maxNumTokens;

    public LiteRtWorkerClient(
        string pythonExecutable,
        string workerScript,
        string modelPath,
        string backend = "cpu",
        int maxNumTokens = 2048)
    {
        _pythonExecutable = Path.GetFullPath(pythonExecutable);
        _workerScript = Path.GetFullPath(workerScript);
        _modelPath = Path.GetFullPath(modelPath);
        _backend = backend;
        _maxNumTokens = maxNumTokens;
    }

    public async Task<LiteRtGenerationResult> GenerateAsync(
        LiteRtGenerationRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateFiles();
        LiteRtWorkerProtocol.ValidateRequest(request);

        // Cross-process: never load the model while another Glint process
        // holds it (see LiteRtModelGate). A contended one-shot fails fast
        // with a clear message instead of two overlapping native loads
        // failing each other opaquely.
        using var modelGate = await LiteRtWorkerProtocol.LiteRtModelGate
            .AcquireAsync(timeout, cancellationToken)
            .ConfigureAwait(false);

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

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("LiteRT-LM worker could not be started.");
        LiteRtWorkerMetrics.RecordStart();
        var processTimer = Stopwatch.StartNew();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(request, JsonOptions).AsMemory(),
                timeoutSource.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeoutSource.Token).ConfigureAwait(false);
            process.StandardInput.Close();

            // The worker announces itself before serving anything, so skip
            // notices until the response for this request arrives. Native
            // JSON status lines on stdout are noise, not answers: skip those
            // too, or one deserializes as Ok=false/Error=null and surfaces
            // as the bare "reported an unknown error".
            string? responseLine = null;
            var skippedNoise = 0;
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeoutSource.Token)
                    .ConfigureAwait(false);
                if (line is null)
                {
                    break;
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
                        break;
                    }

                    continue;
                }

                responseLine = line;
                break;
            }

            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"LiteRT-LM worker exited {process.ExitCode}: {error.Trim()}");
            }

            WorkerResponse? response;
            try
            {
                response = responseLine is null
                    ? null
                    : JsonSerializer.Deserialize<WorkerResponse>(responseLine, JsonOptions);
            }
            catch (JsonException jsonError)
            {
                throw new InvalidDataException(
                    "LiteRT-LM worker returned a malformed response: "
                    + $"{LiteRtWorkerProtocol.Preview(responseLine)}. {Tail(error)}".Trim(),
                    jsonError);
            }

            if (response is null)
            {
                throw new InvalidDataException(
                    "LiteRT-LM worker returned no JSON response. "
                    + Tail(error).Trim());
            }

            if (!response.Ok)
            {
                var detail = string.IsNullOrWhiteSpace(response.Error)
                    ? $"no error detail; raw response: {LiteRtWorkerProtocol.Preview(responseLine)}"
                    : response.Error.Trim();
                throw new InvalidOperationException(
                    $"LiteRT-LM worker reported a failure: {detail} {Tail(error)}".Trim());
            }

            processTimer.Stop();
            return new(
                response.Text ?? string.Empty,
                TimeSpan.FromMilliseconds(response.ElapsedMilliseconds),
                processTimer.Elapsed);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
    }

    private static string Tail(string error, int maxLength = 500)
    {
        var trimmed = error.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed.Length <= maxLength
            ? $"Worker output: {trimmed}"
            : $"Worker output: ...{trimmed[^maxLength..]}";
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

    private sealed record WorkerResponse(
        bool Ok,
        string? Text,
        string? Error,
        double ElapsedMilliseconds);
}
