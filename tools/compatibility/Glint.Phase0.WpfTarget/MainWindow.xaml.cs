using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Glint.Phase0.WpfTarget;

public partial class MainWindow : Window
{
    private const uint WdaExcludeFromCapture = 0x00000011;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        var arguments = Environment.GetCommandLineArgs();
        var textArgument = Array.FindIndex(
            arguments,
            argument => argument.Equals("--text", StringComparison.OrdinalIgnoreCase));
        if (textArgument >= 0 && textArgument + 1 < arguments.Length)
        {
            ModeText.Text = $"Mode: {arguments[textArgument + 1][..Math.Min(
                arguments[textArgument + 1].Length,
                200)]}";
        }

        if (arguments.Contains("--protected", StringComparer.OrdinalIgnoreCase))
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (!SetWindowDisplayAffinity(handle, WdaExcludeFromCapture))
            {
                throw new InvalidOperationException(
                    $"SetWindowDisplayAffinity failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }

            ModeText.Text = "Mode: WDA_EXCLUDEFROMCAPTURE";
        }

        if (arguments.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
        {
            ModeText.Text = "Mode: minimized";
            WindowState = WindowState.Minimized;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
}
