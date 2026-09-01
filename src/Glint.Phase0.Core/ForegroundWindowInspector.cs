using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Glint.Phase0.Core;

public interface IForegroundWindowInspector
{
    ForegroundWindowInfo? Inspect();
}

public sealed class ForegroundWindowInspector : IForegroundWindowInspector
{
    public ForegroundWindowInfo? Inspect()
    {
        var handle = NativeMethods.GetForegroundWindow();
        return Inspect(handle);
    }

    public ForegroundWindowInfo? Inspect(nint handle)
    {
        if (handle == 0)
        {
            return null;
        }

        _ = NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
        {
            return null;
        }

        var titleLength = NativeMethods.GetWindowTextLength(handle);
        var titleBuffer = new StringBuilder(Math.Max(1, titleLength + 1));
        _ = NativeMethods.GetWindowText(handle, titleBuffer, titleBuffer.Capacity);

        string processName = "unknown";
        string? executablePath = null;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            processName = process.ProcessName;
            try
            {
                executablePath = process.MainModule?.FileName;
            }
            catch (Exception error) when (
                error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                executablePath = null;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        var bounds = NativeMethods.GetWindowRect(handle, out var rect)
            ? new WindowBounds(rect.Left, rect.Top, rect.Right, rect.Bottom)
            : default;

        var (elevated, elevationDetermined) = QueryElevation(processId);
        var (secureDesktop, desktopDetermined) = QuerySecureDesktop();
        var protectedWindow = NativeMethods.GetWindowDisplayAffinity(handle, out var affinity)
            && affinity != NativeMethods.WdaNone;

        return new ForegroundWindowInfo(
            handle,
            checked((int)processId),
            processName,
            executablePath,
            titleBuffer.ToString(),
            bounds,
            NativeMethods.IsIconic(handle),
            elevated,
            elevationDetermined,
            protectedWindow,
            secureDesktop,
            desktopDetermined,
            processId == Environment.ProcessId);
    }

    private static (bool Elevated, bool Determined) QueryElevation(uint processId)
    {
        var process = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation,
            false,
            processId);
        if (process == 0)
        {
            return (false, false);
        }

        try
        {
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery, out var token))
            {
                return (false, false);
            }

            try
            {
                var size = checked((uint)Marshal.SizeOf<NativeMethods.TokenElevationInfo>());
                if (!NativeMethods.GetTokenInformation(
                        token,
                        NativeMethods.TokenElevation,
                        out var elevation,
                        size,
                        out _))
                {
                    return (false, false);
                }

                return (elevation.TokenIsElevated != 0, true);
            }
            finally
            {
                _ = NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            _ = NativeMethods.CloseHandle(process);
        }
    }

    private static (bool SecureDesktop, bool Determined) QuerySecureDesktop()
    {
        var desktop = NativeMethods.OpenInputDesktop(
            0,
            false,
            NativeMethods.DesktopReadObjects | NativeMethods.DesktopSwitchDesktop);
        if (desktop == 0)
        {
            return (true, false);
        }

        try
        {
            _ = NativeMethods.GetUserObjectInformation(
                desktop,
                NativeMethods.UoiName,
                null,
                0,
                out var needed);
            if (needed == 0)
            {
                return (true, false);
            }

            var name = new StringBuilder(checked((int)(needed / sizeof(char) + 1)));
            if (!NativeMethods.GetUserObjectInformation(
                    desktop,
                    NativeMethods.UoiName,
                    name,
                    needed,
                    out _))
            {
                return (true, false);
            }

            return (!string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase), true);
        }
        finally
        {
            _ = NativeMethods.CloseDesktop(desktop);
        }
    }
}
