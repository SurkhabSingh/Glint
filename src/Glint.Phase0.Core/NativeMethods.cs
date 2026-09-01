using System.Runtime.InteropServices;
using System.Text;

namespace Glint.Phase0.Core;

internal static class NativeMethods
{
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint TokenQuery = 0x0008;
    internal const int TokenElevation = 20;
    internal const uint DesktopReadObjects = 0x0001;
    internal const uint DesktopSwitchDesktop = 0x0100;
    internal const int UoiName = 2;
    internal const uint WdaNone = 0x00000000;

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    internal static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(nint hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hWnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowDisplayAffinity(nint hWnd, out uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint OpenInputDesktop(
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint desiredAccess);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetUserObjectInformationW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetUserObjectInformation(
        nint handle,
        int index,
        StringBuilder? info,
        uint length,
        out uint needed);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseDesktop(nint desktop);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(
        nint token,
        int tokenInformationClass,
        out TokenElevationInfo tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Rect
    {
        internal readonly int Left;
        internal readonly int Top;
        internal readonly int Right;
        internal readonly int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct TokenElevationInfo
    {
        internal readonly uint TokenIsElevated;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }
}
