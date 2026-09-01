using System.Diagnostics;

namespace Glint.Phase0.Core;

public sealed record LiteRtProbeResult(
    bool Ready,
    string Detail,
    string? RuntimeVersion,
    long? ModelBytes);

public sealed class LiteRtWorkerProbe
{
    public async Task<LiteRtProbeResult> ProbeAsync(
        string runtimeExecutable,
        string modelPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(runtimeExecutable))
        {
            return new(false, "LiteRT-LM runtime executable is missing.", null, null);
        }

        if (!File.Exists(modelPath))
        {
            return new(false, "Gemma 4 model artifact is missing.", null, null);
        }

        var info = FileVersionInfo.GetVersionInfo(runtimeExecutable);
        var start = new ProcessStartInfo
        {
            FileName = runtimeExecutable,
            Arguments = "--help",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(start);
        if (process is null)
        {
            return new(false, "LiteRT-LM runtime could not be started.", info.FileVersion, null);
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            return new(
                false,
                $"runtime probe exited {process.ExitCode}: {error.Trim()}",
                info.FileVersion,
                new FileInfo(modelPath).Length);
        }

        var detail = string.IsNullOrWhiteSpace(output)
            ? "runtime accepted --help"
            : output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0];
        return new(true, detail, info.FileVersion, new FileInfo(modelPath).Length);
    }
}
