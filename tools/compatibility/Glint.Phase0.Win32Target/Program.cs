using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Glint.Phase0.Win32Target;

internal static class Program
{
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsVisible = 0x10000000;
    private const uint WsChild = 0x40000000;
    private const int SwShow = 5;
    private const uint WmDestroy = 0x0002;
    private static readonly WindowProcedure Procedure = HandleWindowMessage;

    [STAThread]
    private static void Main()
    {
        var instance = GetModuleHandle(null);
        var windowClass = new WindowClass
        {
            Size = checked((uint)Marshal.SizeOf<WindowClass>()),
            Instance = instance,
            ClassName = "GlintPhase0Win32Target",
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            Cursor = LoadCursor(0, new nint(32512))
        };
        if (RegisterClassEx(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var window = CreateWindowEx(
            0,
            windowClass.ClassName,
            "Glint Win32 Compatibility Target",
            WsOverlappedWindow,
            620,
            300,
            720,
            420,
            0,
            0,
            instance,
            0);
        if (window == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        _ = CreateWindowEx(
            0,
            "STATIC",
            "Glint native Win32 compatibility target",
            WsChild | WsVisible,
            40,
            60,
            560,
            32,
            window,
            0,
            instance,
            0);
        _ = CreateWindowEx(
            0,
            "STATIC",
            "Known-safe text for UI Automation, graphics capture, and OCR.",
            WsChild | WsVisible,
            40,
            110,
            600,
            32,
            window,
            0,
            instance,
            0);
        _ = CreateWindowEx(
            0,
            "BUTTON",
            "Known safe control",
            WsChild | WsVisible,
            40,
            170,
            190,
            42,
            window,
            0,
            instance,
            0);

        _ = ShowWindow(window, SwShow);
        _ = UpdateWindow(window);
        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            _ = TranslateMessage(ref message);
            _ = DispatchMessage(ref message);
        }
    }

    private static nint HandleWindowMessage(
        nint window,
        uint message,
        nuint wordParameter,
        nint longParameter)
    {
        if (message == WmDestroy)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProc(window, message, wordParameter, longParameter);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(
        nint window,
        uint message,
        nuint wordParameter,
        nint longParameter);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        internal uint Size;
        internal uint Style;
        internal nint WindowProcedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint BackgroundBrush;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal nint Window;
        internal uint Value;
        internal nuint WordParameter;
        internal nint LongParameter;
        internal uint Time;
        internal Point Position;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(nint window);

    [DllImport("user32.dll")]
    private static extern int GetMessage(
        out Message message,
        nint window,
        uint minimum,
        uint maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Message message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(
        nint window,
        uint message,
        nuint wordParameter,
        nint longParameter);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern nint LoadCursor(nint instance, nint cursorName);
}
