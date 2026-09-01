using Glint.Phase0.Core;
using Microsoft.UI.Xaml;

namespace Glint.Phase0.App;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private CommandBarWindow? _commandBar;
    private TrayHotkeyHost? _trayHost;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        _commandBar = new CommandBarWindow
        {
            CommandSubmitted = HandleCommandAsync,
        };

        _mainWindow.Activate();

        _trayHost = new TrayHotkeyHost(
            _mainWindow.WindowHandle,
            new TrayHotkeyHost.TrayActions(
                OpenCommandBar,
                _mainWindow.ShowDashboard,
                query => _ = _mainWindow.OpenSearchAsync(query),
                () => _mainWindow.ViewModel.StartScanningFromShellAsync(),
                () => _mainWindow.ViewModel.PauseScanningFromShellAsync(),
                () => _mainWindow.ViewModel.IsScanning,
                ExitApplication));

        _mainWindow.DispatcherQueue.TryEnqueue(_mainWindow.HideToTray);
    }

    private void OpenCommandBar()
    {
        _commandBar?.Toggle();
    }

    private async Task HandleCommandAsync(string text)
    {
        if (_mainWindow is null)
        {
            return;
        }

        QuickCommand command = QuickCommandParser.Parse(text);
        switch (command.Kind)
        {
            case QuickCommandKind.CaptureCurrentWindow:
                await _mainWindow.ViewModel.CaptureCurrentWindowAsync();
                break;

            case QuickCommandKind.StartScanning:
                await _mainWindow.ViewModel.StartScanningFromShellAsync();
                break;

            case QuickCommandKind.PauseScanning:
                await _mainWindow.ViewModel.PauseScanningFromShellAsync();
                break;

            case QuickCommandKind.SearchContext:
                await _mainWindow.OpenSearchAsync(command.Query);
                break;

            case QuickCommandKind.OpenDashboard:
                _mainWindow.ShowDashboard();
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported quick command kind: {command.Kind}.");
        }
    }

    private void ExitApplication()
    {
        _trayHost?.Dispose();
        _trayHost = null;

        _commandBar?.CloseForExit();
        _commandBar = null;

        _mainWindow?.RequestExit();
        _mainWindow = null;
    }
}
