using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Glint.Phase0.App;

public sealed partial class MainWindow : Window
{
    private const int ShowRestore = 9;
    private readonly AppWindow _appWindow;
    private bool _allowClose;

    public MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        WindowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Changed += (_, args) =>
        {
            if (args.DidPresenterChange
                && _appWindow.Presenter is OverlappedPresenter
                {
                    State: OverlappedPresenterState.Minimized
                })
            {
                HideToTray();
            }
        };
        _appWindow.Closing += (_, args) =>
        {
            if (!_allowClose)
            {
                args.Cancel = true;
                HideToTray();
            }
        };
        Closed += (_, _) => ViewModel.Dispose();
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        ShowPage("activity");
        ViewModel.InitializeCommand.Execute(null);
    }

    public nint WindowHandle { get; }

    public void HideToTray() => _appWindow.Hide();

    public void ShowDashboard()
    {
        _appWindow.Show();
        ShowWindow(WindowHandle, ShowRestore);
        Activate();
        SetForegroundWindow(WindowHandle);
    }

    public async Task OpenSearchAsync(string? query)
    {
        ShowDashboard();
        var searchItem = RootNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .First(item => string.Equals(item.Tag as string, "search", StringComparison.Ordinal));
        RootNavigation.SelectedItem = searchItem;
        ShowPage("search");
        await ViewModel.SetSearchQueryAndRunAsync(query);
        SearchBox.Focus(FocusState.Programmatic);
    }

    public void RequestExit()
    {
        _allowClose = true;
        Close();
    }

    private void RootNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag)
        {
            ShowPage(tag);
        }
    }

    private void ShowPage(string tag)
    {
        ActivityPage.Visibility = tag == "activity" ? Visibility.Visible : Visibility.Collapsed;
        SearchPage.Visibility = tag == "search" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = tag == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ImportGemmaModel_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".litertlm");
            InitializeWithWindow.Initialize(picker, WindowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            await ViewModel.ImportGemmaModelAsync(file.Path);
        }
        catch (System.Exception error)
        {
            await ViewModel.ImportGemmaModelAsync(string.Empty);
            System.Diagnostics.Debug.WriteLine($"Import picker failed: {error}");
        }
    }

    private async void SearchBox_KeyDown(
        object sender,
        Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Enter)
        {
            args.Handled = true;
            await ViewModel.SearchContextAsync();
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);
}
