using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace Glint.Phase0.App;

public sealed partial class CommandBarWindow : Window
{
    private const int WidthPixels = 480;
    private const int HeightPixels = 382;
    private const int ShowRestore = 9;

    private readonly AppWindow _appWindow;
    private nint _previousForegroundWindow;
    private bool _allowClose;

    public CommandBarWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);

        var handle = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        _appWindow.SetPresenter(presenter);
        _appWindow.IsShownInSwitchers = false;
        _appWindow.Resize(new SizeInt32(WidthPixels, HeightPixels));
        _appWindow.Closing += (_, args) =>
        {
            if (!_allowClose)
            {
                args.Cancel = true;
                HideAndRestoreForeground();
            }
        };
    }

    public Func<string, Task>? CommandSubmitted { get; set; }

    public bool IsVisible { get; private set; }

    public void Toggle()
    {
        if (IsVisible)
        {
            HideAndRestoreForeground();
        }
        else
        {
            Show();
        }
    }

    public void Show()
    {
        _previousForegroundWindow = GetForegroundWindow();
        CommandTextBox.Text = string.Empty;
        CenterNearTop();
        _appWindow.Show();
        Activate();
        IsVisible = true;
        DispatcherQueue.TryEnqueue(() => CommandTextBox.Focus(FocusState.Programmatic));
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private async Task SubmitAsync(string text)
    {
        var command = text.Trim();
        if (command.Length == 0)
        {
            HideAndRestoreForeground();
            return;
        }

        HideAndRestoreForeground();
        await Task.Delay(120);
        if (CommandSubmitted is not null)
        {
            await CommandSubmitted(command);
        }
    }

    private void HideAndRestoreForeground()
    {
        _appWindow.Hide();
        IsVisible = false;
        if (_previousForegroundWindow != 0)
        {
            ShowWindow(_previousForegroundWindow, ShowRestore);
            SetForegroundWindow(_previousForegroundWindow);
        }
    }

    private void CenterNearTop()
    {
        var display = DisplayArea.GetFromWindowId(
            _appWindow.Id,
            DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        var x = workArea.X + Math.Max(0, (workArea.Width - WidthPixels) / 2);
        var y = workArea.Y + Math.Max(32, workArea.Height / 7);
        _appWindow.Move(new PointInt32(x, y));
    }

    private async void CommandTextBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Escape)
        {
            args.Handled = true;
            HideAndRestoreForeground();
            return;
        }

        if (args.Key == Windows.System.VirtualKey.Enter)
        {
            args.Handled = true;
            await SubmitAsync(CommandTextBox.Text);
        }
    }

    private async void Suggestion_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string command })
        {
            await SubmitAsync(command);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);
}
