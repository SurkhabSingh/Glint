namespace Glint.Phase0.Core;

/// <summary>
/// Where the local model runs. The GPU by default: on a machine with one it
/// is several times faster (about 1.6 s against 5.3 s per activity summary on
/// an RTX 5070 with a Ryzen 7 7700). The user can choose the CPU, and when the
/// GPU cannot load the model Glint falls back to the CPU on its own.
/// </summary>
/// <remarks>
/// The host passes the user's choice in <c>GLINT_LITERT_BACKEND</c>
/// ("gpu" or "cpu"). A GPU failure is remembered for the rest of this
/// process, so one failed load is not retried for every request.
/// </remarks>
public static class LiteRtBackend
{
    public const string Gpu = "gpu";
    public const string Cpu = "cpu";
    public const string EnvironmentVariable = "GLINT_LITERT_BACKEND";

    private static volatile string? _gpuFailure;

    /// The user's choice: "gpu" unless they picked the CPU.
    public static string Preferred =>
        string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim(), Cpu, StringComparison.OrdinalIgnoreCase)
            ? Cpu
            : Gpu;

    /// Where the next model load should go.
    public static string Current => Preferred == Gpu && _gpuFailure is null ? Gpu : Cpu;

    /// Why the GPU was given up on in this process, if it was.
    public static string? GpuFailure => _gpuFailure;

    public static void MarkGpuFailed(string reason) =>
        _gpuFailure = string.IsNullOrWhiteSpace(reason) ? "the GPU could not load the model" : reason.Trim();

    /// For tests: forget a recorded failure.
    internal static void Reset() => _gpuFailure = null;
}
