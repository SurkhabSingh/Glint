using System.Runtime.InteropServices;

namespace Glint.Phase0.App;

internal sealed class TrayHotkeyHost : IDisposable
{
    private const uint WindowMessageHotkey = 0x0312;
    private const uint WindowMessageNull = 0x0000;
    private const uint WindowMessageLeftButtonUp = 0x0202;
    private const uint WindowMessageLeftButtonDoubleClick = 0x0203;
    private const uint WindowMessageRightButtonUp = 0x0205;
    private const uint TrayCallbackMessage = 0x8000 + 42;
    private const int HotkeyId = 0x474C;
    private const uint ModifierAlt = 0x0001;
    private const uint ModifierControl = 0x0002;
    private const uint ModifierNoRepeat = 0x4000;
    private const uint VirtualKeyG = 0x47;
    private const uint NotifyAdd = 0x00000000;
    private const uint NotifyDelete = 0x00000002;
    private const uint NotifyIcon = 0x00000002;
    private const uint NotifyMessage = 0x00000001;
    private const uint NotifyTip = 0x00000004;
    private const uint MenuString = 0x00000000;
    private const uint MenuGray = 0x00000001;
    private const uint MenuSeparator = 0x00000800;
    private const uint TrackRightButton = 0x0002;
    private const uint TrackReturnCommand = 0x0100;
    private const int DefaultApplicationIcon = 32512;

    private const uint CommandOpenBar = 1001;
    private const uint CommandOpenDashboard = 1002;
    private const uint CommandStartScanning = 1003;
    private const uint CommandPauseScanning = 1004;
    private const uint CommandSearch = 1005;
    private const uint CommandExit = 1099;

    private static readonly nuint SubclassId = 0x474C494E;

    private readonly nint _windowHandle;
    private readonly TrayActions _actions;
    private readonly SubclassProcedure _subclassProcedure;
    private NotifyIconData _notifyIcon;
    private bool _subclassInstalled;
    private bool _trayInstalled;

    public TrayHotkeyHost(nint windowHandle, TrayActions actions)
    {
        _windowHandle = windowHandle;
        _actions = actions;
        _subclassProcedure = WindowProcedure;

        _subclassInstalled = SetWindowSubclass(
            _windowHandle,
            _subclassProcedure,
            SubclassId,
            0);
        HotkeyRegistered = RegisterHotKey(
            _windowHandle,
            HotkeyId,
            ModifierControl | ModifierAlt | ModifierNoRepeat,
            VirtualKeyG);
        InstallTrayIcon();
    }

    public bool HotkeyRegistered { get; }

    public void Dispose()
    {
        UnregisterHotKey(_windowHandle, HotkeyId);
        if (_trayInstalled)
        {
            ShellNotifyIcon(NotifyDelete, ref _notifyIcon);
            _trayInstalled = false;
        }

        if (_subclassInstalled)
        {
            RemoveWindowSubclass(_windowHandle, _subclassProcedure, SubclassId);
            _subclassInstalled = false;
        }
    }

    private void InstallTrayIcon()
    {
        _notifyIcon = new NotifyIconData
        {
            Size = checked((uint)Marshal.SizeOf<NotifyIconData>()),
            WindowHandle = _windowHandle,
            Id = 1,
            Flags = NotifyMessage | NotifyIcon | NotifyTip,
            CallbackMessage = TrayCallbackMessage,
            IconHandle = LoadIcon(0, new nint(DefaultApplicationIcon)),
            Tip = "Glint - Ctrl+Alt+G",
            Info = string.Empty,
            InfoTitle = string.Empty
        };
        _trayInstalled = ShellNotifyIcon(NotifyAdd, ref _notifyIcon);
    }

    private nint WindowProcedure(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        if (message == WindowMessageHotkey && unchecked((int)wParam) == HotkeyId)
        {
            _actions.OpenCommandBar();
            return 0;
        }

        if (message == TrayCallbackMessage)
        {
            var mouseMessage = unchecked((uint)lParam.ToInt64());
            switch (mouseMessage)
            {
                case WindowMessageLeftButtonUp:
                    _actions.OpenCommandBar();
                    return 0;
                case WindowMessageLeftButtonDoubleClick:
                    _actions.OpenDashboard();
                    return 0;
                case WindowMessageRightButtonUp:
                    ShowContextMenu();
                    return 0;
            }
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MenuString, CommandOpenBar, "Open command bar\tCtrl+Alt+G");
            AppendMenu(menu, MenuString, CommandOpenDashboard, "Open Glint");
            AppendMenu(menu, MenuString, CommandSearch, "Search context");
            AppendMenu(menu, MenuSeparator, 0, null);
            AppendMenu(
                menu,
                MenuString | (_actions.IsScanning() ? MenuGray : 0),
                CommandStartScanning,
                "Start scanning");
            AppendMenu(
                menu,
                MenuString | (_actions.IsScanning() ? 0 : MenuGray),
                CommandPauseScanning,
                "Pause scanning");
            AppendMenu(menu, MenuSeparator, 0, null);
            AppendMenu(menu, MenuString, CommandExit, "Exit Glint");

            GetCursorPos(out var point);
            SetForegroundWindow(_windowHandle);
            var selected = TrackPopupMenuEx(
                menu,
                TrackRightButton | TrackReturnCommand,
                point.X,
                point.Y,
                _windowHandle,
                0);
            PostMessage(_windowHandle, WindowMessageNull, 0, 0);
            ExecuteCommand(selected);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void ExecuteCommand(uint command)
    {
        switch (command)
        {
            case CommandOpenBar:
                _actions.OpenCommandBar();
                break;
            case CommandOpenDashboard:
                _actions.OpenDashboard();
                break;
            case CommandSearch:
                _actions.OpenSearch(null);
                break;
            case CommandStartScanning:
                _ = _actions.StartScanning();
                break;
            case CommandPauseScanning:
                _ = _actions.PauseScanning();
                break;
            case CommandExit:
                _actions.Exit();
                break;
        }
    }

    internal sealed record TrayActions(
        Action OpenCommandBar,
        Action OpenDashboard,
        Action<string?> OpenSearch,
        Func<Task> StartScanning,
        Func<Task> PauseScanning,
        Func<bool> IsScanning,
        Action Exit);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIconHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private delegate nint SubclassProcedure(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint windowHandle,
        SubclassProcedure procedure,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint windowHandle,
        SubclassProcedure procedure,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        nint windowHandle,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint windowHandle, int id);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadIconW")]
    private static extern nint LoadIcon(nint instance, nint iconName);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(
        nint menu,
        uint flags,
        uint itemId,
        string? text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(
        nint menu,
        uint flags,
        int x,
        int y,
        nint windowHandle,
        nint parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);
}
