using Glint.Phase0.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Glint.Phase0.App;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);
    private readonly AsyncCommand _startScanningCommand;
    private readonly AsyncCommand _pauseScanningCommand;
    private readonly AsyncCommand _searchCommand;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private string _foregroundSummary = "Not checked.";
    private string _storageSummary = "Not checked.";
    private string _captureSummary = "No capture has run.";
    private string _compatibilitySummary = "Not checked.";
    private string _modelSummary = "Checking the local Gemma runtime...";
    private string _historySummary = "No scans yet.";
    private string _searchQuery = string.Empty;
    private string _searchSummary = "Search summaries and redacted captured text stored on this machine.";
    private string _overallTitle = "Phase 0 diagnostics";
    private string _overallMessage = "Run the checks on each representative Windows machine.";
    private InfoBarSeverity _overallSeverity = InfoBarSeverity.Informational;
    private bool _isBusy;
    private bool _isScanning;
    private bool _borderlessAccessAttempted;
    private bool _borderlessAccessAllowed;
    private string? _borderlessAccessError;
    private CancellationTokenSource? _scanCancellation;
    private Task? _scanLoopTask;

    public MainViewModel()
    {
        InitializeCommand = new AsyncCommand(InitializeAsync);
        ProbeCommand = new AsyncCommand(ProbeAsync);
        StorageCommand = new AsyncCommand(CheckStorageAsync);
        CompatibilityCommand = new AsyncCommand(CheckCompatibilityAsync);
        _startScanningCommand = new AsyncCommand(StartScanningAsync, () => !IsScanning);
        _pauseScanningCommand = new AsyncCommand(PauseScanningAsync, () => IsScanning);
        _searchCommand = new AsyncCommand(SearchContextAsync);
        BorderlessCommand = new AsyncCommand(RequestBorderlessAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand InitializeCommand { get; }

    public ICommand ProbeCommand { get; }

    public ICommand StorageCommand { get; }

    public ICommand CompatibilityCommand { get; }

    public ICommand StartScanningCommand => _startScanningCommand;

    public ICommand PauseScanningCommand => _pauseScanningCommand;

    public ICommand SearchCommand => _searchCommand;

    public ICommand BorderlessCommand { get; }

    public ObservableCollection<ManualScanItemViewModel> ScanHistory { get; } = [];

    public ObservableCollection<ContextSearchItemViewModel> SearchResults { get; } = [];

    public string ForegroundSummary
    {
        get => _foregroundSummary;
        private set => SetField(ref _foregroundSummary, value);
    }

    public string StorageSummary
    {
        get => _storageSummary;
        private set => SetField(ref _storageSummary, value);
    }

    public string CaptureSummary
    {
        get => _captureSummary;
        private set => SetField(ref _captureSummary, value);
    }

    public string CompatibilitySummary
    {
        get => _compatibilitySummary;
        private set => SetField(ref _compatibilitySummary, value);
    }

    public string ModelSummary
    {
        get => _modelSummary;
        private set => SetField(ref _modelSummary, value);
    }

    public string HistorySummary
    {
        get => _historySummary;
        private set => SetField(ref _historySummary, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set => SetField(ref _searchQuery, value);
    }

    public string SearchSummary
    {
        get => _searchSummary;
        private set => SetField(ref _searchSummary, value);
    }

    public string OverallTitle
    {
        get => _overallTitle;
        private set => SetField(ref _overallTitle, value);
    }

    public string OverallMessage
    {
        get => _overallMessage;
        private set => SetField(ref _overallMessage, value);
    }

    public InfoBarSeverity OverallSeverity
    {
        get => _overallSeverity;
        private set => SetField(ref _overallSeverity, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (_isScanning == value)
            {
                return;
            }

            _isScanning = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsScanning)));
            _startScanningCommand.NotifyCanExecuteChanged();
            _pauseScanningCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task InitializeAsync()
    {
        RefreshModelStatus();
        LoadScanHistory();
        await CheckCompatibilityAsync();
        await CheckStorageAsync();
        await ProbeAsync();
    }

    private Task CheckCompatibilityAsync()
    {
        try
        {
            var report = new WindowsCompatibilityService().Inspect();
            var warnings = report.Checks
                .Where(check => !check.Passed)
                .Select(check => check.Id)
                .ToArray();
            CompatibilitySummary =
                $"Windows build {report.OsBuild}; {report.OsArchitecture}; "
                + $"core capture: {(report.ReadyForCoreCapture ? "ready" : "blocked")}; "
                + $"local models: {(report.ReadyForLocalModels ? "ready" : "limited")}."
                + (warnings.Length == 0
                    ? " All checks passed."
                    : $" Review: {string.Join(", ", warnings)}.")
                + Environment.NewLine
                + string.Join(
                    Environment.NewLine,
                    report.Checks.Select(check =>
                        $"{(check.Passed ? "PASS" : check.Required ? "FAIL" : "WARN")} "
                        + $"{check.Id}: {check.Detail}"));
            if (report.ReadyForCoreCapture)
            {
                SetOverallSuccess("This machine meets the required Phase 0 runtime checks.");
            }
            else
            {
                SetOverallError("This machine does not meet the required Phase 0 runtime checks.");
            }
        }
        catch (Exception error)
        {
            CompatibilitySummary = $"Compatibility check failed: {error.Message}";
            SetOverallError(CompatibilitySummary);
        }

        return Task.CompletedTask;
    }

    private Task ProbeAsync()
    {
        try
        {
            var window = new ForegroundWindowInspector().Inspect();
            if (window is null)
            {
                ForegroundSummary = "Windows did not report a foreground window.";
                SetOverallError(ForegroundSummary);
                return Task.CompletedTask;
            }

            var security = new UiAutomationService().ProbeSecurity(window);
            var decision = new PrivacyGate().Evaluate(window, security);
            ForegroundSummary = decision.Allowed
                ? $"{window.ProcessName}: allowed; UI Automation state is known."
                : $"{window.ProcessName}: suppressed ({decision.Reason}) - {decision.Detail}";
            SetOverallSuccess("Foreground privacy state was determined.");
        }
        catch (Exception error)
        {
            ForegroundSummary = $"Probe failed: {error.Message}";
            SetOverallError(ForegroundSummary);
        }

        return Task.CompletedTask;
    }

    private Task CheckStorageAsync()
    {
        try
        {
            var root = GetDataRoot();
            var keyStore = new DpapiKeyStore(Path.Combine(root, "secrets", "dbkey.bin"));
            var vec = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "vec0.dll");
            using var database = Phase0Database.Open(
                Path.Combine(root, "memory.db"),
                keyStore,
                vec);
            var diagnostics = database.GetDiagnostics();
            StorageSummary =
                $"SQLCipher {diagnostics.SqlCipherVersion}; FTS5: {diagnostics.Fts5Available}; "
                + $"sqlite-vec: {diagnostics.SqliteVecAvailable}; events: {database.CountEvents()}.";
            SetOverallSuccess("Encrypted storage initialized without plaintext fallback.");
        }
        catch (Exception error)
        {
            StorageSummary = $"Storage failed: {error.Message}";
            SetOverallError(StorageSummary);
        }

        return Task.CompletedTask;
    }

    private async Task StartScanningAsync()
    {
        var runtime = ResolveRuntime();
        if (!runtime.IsReady)
        {
            CaptureSummary =
                $"Gemma runtime is not ready. Missing: {string.Join(", ", runtime.Missing)}.";
            SetOverallError(CaptureSummary);
            return;
        }

        if (!_borderlessAccessAttempted
            && OperatingSystem.IsWindowsVersionAtLeast(
                10,
                0,
                WindowsCompatibilityService.BorderlessCaptureBuild))
        {
            await TryRequestBorderlessAccessAsync();
        }

        var session = new CancellationTokenSource();
        _scanCancellation = session;
        IsScanning = true;
        IsBusy = true;
        CaptureSummary = _borderlessAccessAllowed
            ? "Scanning is active without the Windows capture border. Switch to any target window."
            : "Scanning is active. Windows may show its capture border; switch to any target window.";
        SetOverallInformation(
            "Continuous local scanning is active. Return to Glint and select Pause scanning to stop.");
        _scanLoopTask = RunScanLoopAsync(runtime, session);
    }

    private async Task PauseScanningAsync()
    {
        var session = _scanCancellation;
        var loopTask = _scanLoopTask;
        if (session is null || loopTask is null)
        {
            return;
        }

        CaptureSummary = "Pausing the active scan...";
        session.Cancel();
        try
        {
            await loopTask;
        }
        catch (OperationCanceledException)
        {
            // The scan loop owns normal cancellation, including an in-flight model worker.
        }
    }

    public Task StartScanningFromShellAsync() => _startScanningCommand.ExecuteAsync();

    public Task PauseScanningFromShellAsync() => _pauseScanningCommand.ExecuteAsync();

    public async Task CaptureCurrentWindowAsync()
    {
        var runtime = ResolveRuntime();
        if (!runtime.IsReady)
        {
            CaptureSummary =
                $"Gemma runtime is not ready. Missing: {string.Join(", ", runtime.Missing)}.";
            SetOverallError(CaptureSummary);
            return;
        }

        using var database = OpenDatabase();
        var coordinator = CreateCoordinator(runtime, database);
        var outcome = await RunScanOnceAsync(coordinator, CancellationToken.None);
        ApplyScanOutcome(outcome);
    }

    public Task SearchContextAsync()
    {
        var query = SearchQuery.Trim();
        SearchResults.Clear();
        if (query.Length == 0)
        {
            SearchSummary = "Enter a person, application, topic, or phrase.";
            return Task.CompletedTask;
        }

        try
        {
            using var database = OpenDatabase();
            var results = database.SearchContext(query);
            foreach (var result in results)
            {
                SearchResults.Add(new(result));
            }

            SearchSummary = results.Count switch
            {
                0 => $"No local context matched \"{query}\".",
                1 => "1 local context result.",
                _ => $"{results.Count} local context results."
            };
        }
        catch (Exception error)
        {
            SearchSummary = $"Search failed: {error.Message}";
        }

        return Task.CompletedTask;
    }

    public async Task SetSearchQueryAndRunAsync(string? query)
    {
        SearchQuery = query?.Trim() ?? string.Empty;
        if (SearchQuery.Length > 0)
        {
            await SearchContextAsync();
        }
        else
        {
            SearchResults.Clear();
            SearchSummary = "Enter a person, application, topic, or phrase.";
        }
    }

    private async Task RunScanLoopAsync(
        LiteRtRuntimeResolution runtime,
        CancellationTokenSource session)
    {
        await Task.Yield();
        try
        {
            using var database = OpenDatabase();
            var coordinator = CreateCoordinator(runtime, database);

            while (true)
            {
                session.Token.ThrowIfCancellationRequested();
                var outcome = await RunScanOnceAsync(coordinator, session.Token);
                ApplyScanOutcome(outcome);

                await Task.Delay(ScanInterval, session.Token);
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            CaptureSummary = "Scanning paused.";
            SetOverallInformation("Continuous local scanning is paused.");
        }
        catch (Exception error)
        {
            CaptureSummary = $"Scanning stopped after an unexpected failure: {error.Message}";
            SetOverallError(CaptureSummary);
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, session))
            {
                _scanCancellation = null;
                _scanLoopTask = null;
                IsBusy = false;
                IsScanning = false;
                session.Dispose();
            }
        }
    }

    private static ManualScanCoordinator CreateCoordinator(
        LiteRtRuntimeResolution runtime,
        IManualScanStore store) =>
        new(
            new ForegroundWindowInspector(),
            new UiAutomationService(),
            new PrivacyGate(),
            new WindowsGraphicsCaptureService(),
            new DeterministicRedactor(),
            new LiteRtActivitySummarizer(
                runtime.PythonExecutable!,
                runtime.WorkerScript!,
                runtime.ModelPath!,
                runtime.ModelId),
            store);

    private async Task<ManualScanOutcome> RunScanOnceAsync(
        ManualScanCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            return await coordinator.ScanAsync(cancellationToken);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private void ApplyScanOutcome(ManualScanOutcome outcome)
    {
        if (outcome.Record is not null)
        {
            ScanHistory.Insert(0, new(outcome.Record));
            while (ScanHistory.Count > 50)
            {
                ScanHistory.RemoveAt(ScanHistory.Count - 1);
            }

            UpdateHistorySummary();
        }

        CaptureSummary = outcome.Kind switch
        {
            ManualScanOutcomeKind.Completed =>
                $"{outcome.Record!.ProcessName}: {outcome.Record.Label} "
                + $"({outcome.Record.InferenceMilliseconds:F0} ms Gemma). Continuing to scan.",
            ManualScanOutcomeKind.ModelFailed =>
                $"{outcome.Detail} Continuing to scan.",
            ManualScanOutcomeKind.Unchanged =>
                "Scanning is active; the current visible content is unchanged.",
            ManualScanOutcomeKind.Suppressed =>
                $"Scanning is active; skipped the foreground window: {outcome.Detail}",
            ManualScanOutcomeKind.DroppedSecretFrame =>
                $"{outcome.Detail} Continuing to scan.",
            _ => $"{outcome.Detail} Continuing to scan."
        };

        switch (outcome.Kind)
        {
            case ManualScanOutcomeKind.Completed:
                SetOverallSuccess(
                    "The window was scanned, summarized locally, and saved. Scanning remains active.");
                break;
            case ManualScanOutcomeKind.Unchanged:
            case ManualScanOutcomeKind.Suppressed:
                SetOverallInformation(CaptureSummary);
                break;
            default:
                SetOverallError(CaptureSummary);
                break;
        }
    }

    private async Task RequestBorderlessAsync()
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(
                    10,
                    0,
                    WindowsCompatibilityService.BorderlessCaptureBuild))
            {
                CaptureSummary =
                    $"Borderless capture requires Windows build "
                    + $"{WindowsCompatibilityService.BorderlessCaptureBuild}; "
                    + "normal bordered capture remains available.";
                return;
            }

            var allowed = await TryRequestBorderlessAccessAsync();
            CaptureSummary = allowed
                ? "Borderless capture permission is allowed."
                : _borderlessAccessError is null
                    ? "Borderless capture permission was not granted; the system border remains."
                    : $"Borderless capture is unavailable: {_borderlessAccessError}";
        }
        catch (Exception error)
        {
            CaptureSummary = $"Borderless permission request failed: {error.Message}";
            SetOverallError(CaptureSummary);
        }
    }

    private async Task<bool> TryRequestBorderlessAccessAsync()
    {
        _borderlessAccessAttempted = true;
        _borderlessAccessError = null;
        try
        {
            _borderlessAccessAllowed =
                await WindowsGraphicsCaptureService.RequestBorderlessAccessAsync();
        }
        catch (Exception error)
        {
            _borderlessAccessAllowed = false;
            _borderlessAccessError = error.Message;
        }

        return _borderlessAccessAllowed;
    }

    private static string GetDataRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Glint",
            "Phase0");

    private static Phase0Database OpenDatabase()
    {
        var root = GetDataRoot();
        return Phase0Database.Open(
            Path.Combine(root, "memory.db"),
            new DpapiKeyStore(Path.Combine(root, "secrets", "dbkey.bin")),
            Path.Combine(
                AppContext.BaseDirectory,
                "runtimes",
                "win-x64",
                "native",
                "vec0.dll"));
    }

    private static LiteRtRuntimeResolution ResolveRuntime() =>
        LiteRtRuntimeLocator.Resolve(AppContext.BaseDirectory, GetDataRoot());

    private void RefreshModelStatus()
    {
        var runtime = ResolveRuntime();
        var baseSummary = runtime.IsReady
            ? "Gemma 4 E2B is ready through the local LiteRT worker."
            : $"Gemma is unavailable. Missing: {string.Join(", ", runtime.Missing)}.";
        ModelSummary = runtime.ModelPath is null
            ? baseSummary
            : $"{baseSummary} Model: {runtime.ModelPath}";
    }

    public async Task ImportGemmaModelAsync(string modelPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                ModelSummary = $"Import failed: file not found at {modelPath}.";
                SetOverallError(ModelSummary);
                return;
            }

            var extension = Path.GetExtension(modelPath);
            if (!string.Equals(extension, ".litertlm", StringComparison.OrdinalIgnoreCase))
            {
                ModelSummary =
                    $"Import failed: Glint's LiteRT worker only accepts .litertlm models, not "
                    + $"'{extension}'. Download gemma-4-E2B-it.litertlm from litert-community on "
                    + "Hugging Face.";
                SetOverallError(ModelSummary);
                return;
            }

            LiteRtRuntimeLocator.WriteActiveModelPath(GetDataRoot(), modelPath);
            RefreshModelStatus();
            var runtime = ResolveRuntime();
            if (runtime.IsReady)
            {
                SetOverallSuccess($"Imported Gemma model: {runtime.ModelPath}");
            }
            else
            {
                SetOverallError(
                    "Model pointer saved, but the LiteRT runtime is still incomplete: "
                    + string.Join(", ", runtime.Missing));
            }
        }
        catch (Exception error)
        {
            ModelSummary = $"Import failed: {error.Message}";
            SetOverallError(ModelSummary);
        }

        await Task.CompletedTask;
    }

    private void LoadScanHistory()
    {
        try
        {
            using var database = OpenDatabase();
            ScanHistory.Clear();
            foreach (var scan in database.GetRecentManualScans())
            {
                ScanHistory.Add(new(scan));
            }

            UpdateHistorySummary();
        }
        catch (Exception error)
        {
            HistorySummary = $"History unavailable: {error.Message}";
        }
    }

    private void UpdateHistorySummary()
    {
        HistorySummary = ScanHistory.Count switch
        {
            0 => "No scans yet.",
            1 => "1 timestamped scan.",
            _ => $"{ScanHistory.Count} timestamped scans."
        };
    }

    private void SetOverallSuccess(string message)
    {
        OverallTitle = "Check completed";
        OverallMessage = message;
        OverallSeverity = InfoBarSeverity.Success;
    }

    private void SetOverallError(string message)
    {
        OverallTitle = "Check failed";
        OverallMessage = message;
        OverallSeverity = InfoBarSeverity.Error;
    }

    private void SetOverallInformation(string message)
    {
        OverallTitle = "Scanning status";
        OverallMessage = message;
        OverallSeverity = InfoBarSeverity.Informational;
    }

    public void Dispose()
    {
        _scanCancellation?.Cancel();
    }

    private void SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ManualScanItemViewModel
{
    public ManualScanItemViewModel(ManualScanRecord scan)
    {
        var capturedAt = DateTimeOffset
            .FromUnixTimeMilliseconds(scan.CapturedAtMilliseconds)
            .ToLocalTime();
        TimestampText = capturedAt.ToString("MMM d, yyyy h:mm:ss tt");
        Label = scan.Label ?? "Gemma summary failed";
        Summary = scan.Summary ?? scan.Error ?? "No summary was produced.";
        ImportantText = string.IsNullOrWhiteSpace(scan.ImportantSignals)
            ? string.Empty
            : $"Important: {scan.ImportantSignals}";
        ImportantVisibility = ImportantText.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        ReminderText = string.IsNullOrWhiteSpace(scan.ReminderCandidate)
            ? string.Empty
            : $"Potential reminder: {scan.ReminderCandidate}";
        ReminderVisibility = ReminderText.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        SourceText = string.IsNullOrWhiteSpace(scan.WindowTitle)
            ? scan.ProcessName
            : $"{scan.ProcessName} | {scan.WindowTitle}";
        var ocrLanguage = string.IsNullOrWhiteSpace(scan.OcrLanguage)
            ? "default"
            : scan.OcrLanguage;
        MetricsText =
            $"UIA {scan.UiAutomationCharacters} chars | OCR {scan.OcrCharacters} chars ({ocrLanguage}) | "
            + $"{scan.Redactions} redactions | capture {scan.CaptureMilliseconds:F0} ms | "
            + $"OCR {scan.OcrMilliseconds:F0} ms | Gemma {scan.InferenceMilliseconds:F0} ms";
        StatusText = scan.Status == ManualScanStatus.Completed
            ? $"Summarized locally with {scan.ModelId}"
            : $"OCR saved; {scan.ModelId} failed";
        RedactedInputText = scan.RedactedInputText ?? string.Empty;
        RedactedUiAutomationText = scan.RedactedUiAutomationText ?? string.Empty;
        RedactedOcrText = scan.RedactedOcrText ?? string.Empty;
        DiagnosticsVisibility = string.IsNullOrWhiteSpace(RedactedInputText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        UiAutomationDiagnosticsVisibility = string.IsNullOrWhiteSpace(RedactedUiAutomationText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        OcrDiagnosticsVisibility = string.IsNullOrWhiteSpace(RedactedOcrText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        var contextCharacters = scan.GemmaContextCharacters > 0
            ? scan.GemmaContextCharacters.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
            : "not recorded";
        InputSummaryText =
            $"Full redacted capture text: {RedactedInputText.Length:N0} chars. "
            + $"Gemma context budget: {contextCharacters} chars. "
            + "This is stored locally for Phase 0 prompt debugging.";
    }

    public string TimestampText { get; }

    public string Label { get; }

    public string Summary { get; }

    public string ImportantText { get; }

    public Visibility ImportantVisibility { get; }

    public string ReminderText { get; }

    public Visibility ReminderVisibility { get; }

    public string SourceText { get; }

    public string MetricsText { get; }

    public string StatusText { get; }

    public Visibility DiagnosticsVisibility { get; }

    public string InputSummaryText { get; }

    public string RedactedInputText { get; }

    public string RedactedUiAutomationText { get; }

    public Visibility UiAutomationDiagnosticsVisibility { get; }

    public string RedactedOcrText { get; }

    public Visibility OcrDiagnosticsVisibility { get; }
}

public sealed class ContextSearchItemViewModel
{
    public ContextSearchItemViewModel(ContextSearchResult result)
    {
        var capturedAt = DateTimeOffset
            .FromUnixTimeMilliseconds(result.CapturedAtMilliseconds)
            .ToLocalTime();
        TimestampText = capturedAt.ToString("MMM d, yyyy h:mm:ss tt");
        Label = result.Label ?? result.WindowTitle;
        Summary = result.Summary ?? "No derived summary is available for this capture.";
        SourceText = string.IsNullOrWhiteSpace(result.WindowTitle)
            ? result.ProcessName
            : $"{result.ProcessName} | {result.WindowTitle}";
        Snippet = result.Snippet;
        ImportantText = string.IsNullOrWhiteSpace(result.ImportantSignals)
            ? string.Empty
            : $"Important: {result.ImportantSignals}";
        ImportantVisibility = ImportantText.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        ReminderText = string.IsNullOrWhiteSpace(result.ReminderCandidate)
            ? string.Empty
            : $"Potential reminder: {result.ReminderCandidate}";
        ReminderVisibility = ReminderText.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public string TimestampText { get; }

    public string Label { get; }

    public string Summary { get; }

    public string SourceText { get; }

    public string Snippet { get; }

    public string ImportantText { get; }

    public Visibility ImportantVisibility { get; }

    public string ReminderText { get; }

    public Visibility ReminderVisibility { get; }
}
