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
    private readonly int? _hostProcessId;

    // hostProcessId is the long-lived UI process that owns Glint's windows
    // when this code runs in a separate short-lived process (the Tauri host
    // spawns the CLI per action), so those windows still count as self.
    public ForegroundWindowInspector(int? hostProcessId = null)
    {
        _hostProcessId = hostProcessId;
    }

    /// <summary>
    /// The foreground window, traced back to the window that owns it. A Save
    /// As dialog, an export window or a pop-up menu belongs to the app's
    /// main window: inspecting the dialog as its own window made every
    /// dialog look like a switch to something else.
    /// </summary>
    public ForegroundWindowInfo? Inspect()
    {
        var handle = NativeMethods.GetForegroundWindow();
        var owner = handle == 0 ? 0 : NativeMethods.GetAncestor(handle, NativeMethods.GaRootOwner);
        if (owner == 0 || owner == handle)
        {
            return Inspect(handle);
        }

        _ = NativeMethods.GetWindowThreadProcessId(handle, out var dialogProcess);
        _ = NativeMethods.GetWindowThreadProcessId(owner, out var ownerProcess);
        if (dialogProcess == 0 || dialogProcess != ownerProcess)
        {
            // Owned across processes: not the same app, so not its dialog.
            return Inspect(handle);
        }

        var info = Inspect(owner);
        return info is null ? Inspect(handle) : info with { DialogTitle = TitleOf(handle) };
    }

    private static string TitleOf(nint handle)
    {
        var length = NativeMethods.GetWindowTextLength(handle);
        var buffer = new StringBuilder(Math.Max(1, length + 1));
        _ = NativeMethods.GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static bool CoversMonitor(nint handle, WindowBounds bounds)
    {
        var monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
        if (monitor == 0 || bounds.Width == 0)
        {
            return false;
        }

        var info = new NativeMethods.MonitorInfo
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        return bounds.Left <= info.Monitor.Left
            && bounds.Top <= info.Monitor.Top
            && bounds.Right >= info.Monitor.Right
            && bounds.Bottom >= info.Monitor.Bottom;
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
                // Reading the main module needs more access than Windows gives
                // for an app running as administrator; the image name does not.
                executablePath = ImagePathOf(processId);
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
            IsSelfProcess(processId, Environment.ProcessId, _hostProcessId),
            IsFullscreen: CoversMonitor(handle, bounds));
    }

    private static string? ImagePathOf(uint processId)
    {
        var process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref size) && size > 0
                ? buffer.ToString(0, (int)size)
                : null;
        }
        finally
        {
            _ = NativeMethods.CloseHandle(process);
        }
    }

    internal static bool IsSelfProcess(uint processId, int currentProcessId, int? hostProcessId) =>
        processId == currentProcessId
        || (hostProcessId is { } host && processId == host);

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
