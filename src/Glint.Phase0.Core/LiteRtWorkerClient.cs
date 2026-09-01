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
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("Prompt cannot be empty.", nameof(request));
        }

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

            var responseLine = await process.StandardOutput.ReadLineAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"LiteRT-LM worker exited {process.ExitCode}: {error.Trim()}");
            }

            var response = responseLine is null
                ? null
                : JsonSerializer.Deserialize<WorkerResponse>(responseLine, JsonOptions);
            if (response is null)
            {
                throw new InvalidDataException("LiteRT-LM worker returned no JSON response.");
            }

            if (!response.Ok)
            {
                throw new InvalidOperationException(
                    response.Error ?? "LiteRT-LM worker reported an unknown error.");
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
