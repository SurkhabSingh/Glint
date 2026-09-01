using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Windows.Graphics.Capture;
using Windows.Media.Ocr;

namespace Glint.Phase0.Core;

public sealed class WindowsCompatibilityService
{
    public const int MinimumBuild = 19041;
    public const int BorderlessCaptureBuild = 20348;
    public const long RecommendedPhysicalMemoryBytes = 8L * 1024 * 1024 * 1024;
    public const long RecommendedFreeDiskBytes = 6L * 1024 * 1024 * 1024;

    public WindowsCompatibilityReport Inspect()
    {
        var version = Environment.OSVersion.Version;
        var memory = GetPhysicalMemory();
        var disk = GetAvailableDisk();
        var graphics = WindowsGraphicsCaptureService.ProbeGraphicsDevices();
        var ocrLanguages = OcrEngine.AvailableRecognizerLanguages
            .Select(language => language.LanguageTag)
            .OrderBy(language => language, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var architectureSupported =
            RuntimeInformation.ProcessArchitecture == Architecture.X64
            && RuntimeInformation.OSArchitecture == Architecture.X64;

        var checks = new List<CompatibilityCheck>
        {
            new(
                "windows-build",
                version.Build >= MinimumBuild,
                true,
                $"Build {version.Build}; minimum is {MinimumBuild}."),
            new(
                "x64-architecture",
                architectureSupported,
                true,
                $"OS {RuntimeInformation.OSArchitecture}; process {RuntimeInformation.ProcessArchitecture}."),
            new(
                "graphics-capture",
                GraphicsCaptureSession.IsSupported(),
                true,
                "Windows Graphics Capture must be available."),
            new(
                "d3d11-device",
                graphics.HardwareAvailable || graphics.SoftwareAvailable,
                true,
                graphics.HardwareAvailable
                    ? "Hardware D3D11 is available."
                    : graphics.SoftwareAvailable
                        ? "Hardware D3D11 is unavailable; WARP software fallback is available."
                        : $"No D3D11 device is available. {graphics.HardwareError} {graphics.SoftwareError}"),
            new(
                "dpapi",
                ProbeDpapi(out var dpapiDetail),
                true,
                dpapiDetail),
            new(
                "ocr-language",
                ocrLanguages.Length > 0,
                false,
                ocrLanguages.Length > 0
                    ? $"{ocrLanguages.Length} OCR language(s) available: {string.Join(", ", ocrLanguages)}."
                    : "No OCR language is installed; UI Automation remains available."),
            new(
                "borderless-capture",
                version.Build >= BorderlessCaptureBuild,
                false,
                version.Build >= BorderlessCaptureBuild
                    ? "Borderless capture can be requested."
                    : $"Build {BorderlessCaptureBuild} or later is required; bordered capture remains available."),
            new(
                "physical-memory",
                memory >= RecommendedPhysicalMemoryBytes,
                false,
                $"{FormatBytes(memory)} installed; 8 GB is recommended for local models."),
            new(
                "model-disk-space",
                disk >= RecommendedFreeDiskBytes,
                false,
                $"{FormatBytes(disk)} free on the local application-data volume; 6 GB is recommended.")
        };

        return new(
            RuntimeInformation.OSDescription,
            version.ToString(),
            version.Build,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            memory,
            disk,
            checks.Where(check => check.Required).All(check => check.Passed),
            checks.Where(check => check.Required).All(check => check.Passed)
            && memory >= RecommendedPhysicalMemoryBytes
            && disk >= RecommendedFreeDiskBytes,
            checks);
    }

    private static bool ProbeDpapi(out string detail)
    {
        try
        {
            var plaintext = RandomNumberGenerator.GetBytes(32);
            var protectedBytes = ProtectedData.Protect(
                plaintext,
                null,
                DataProtectionScope.CurrentUser);
            var roundTrip = ProtectedData.Unprotect(
                protectedBytes,
                null,
                DataProtectionScope.CurrentUser);
            var passed = CryptographicOperations.FixedTimeEquals(plaintext, roundTrip);
            detail = passed
                ? "Current-user DPAPI round trip succeeded."
                : "Current-user DPAPI returned different bytes.";
            return passed;
        }
        catch (Exception error)
        {
            detail = $"Current-user DPAPI failed: {error.Message}";
            return false;
        }
    }

    private static long GetPhysicalMemory()
    {
        var status = new NativeMethods.MemoryStatusEx
        {
            Length = checked((uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>())
        };
        return NativeMethods.GlobalMemoryStatusEx(ref status)
            ? checked((long)status.TotalPhysical)
            : 0;
    }

    private static long GetAvailableDisk()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.GetPathRoot(localData);
        return string.IsNullOrWhiteSpace(root)
            ? 0
            : new DriveInfo(root).AvailableFreeSpace;
    }

    private static string FormatBytes(long bytes) =>
        $"{bytes / (1024d * 1024 * 1024):F1} GB";
}
