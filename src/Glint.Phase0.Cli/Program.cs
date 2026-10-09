using Glint.Phase0.Core;
using System.Text.Json;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true
};

try
{
    var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
    var options = ParseOptions(args.Skip(1).ToArray());
    var dataRoot = Path.GetFullPath(
        options.GetValueOrDefault(
            "data-dir",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Glint",
                "Phase0")));

    switch (command)
    {
        // Glint's backend: the one process that owns the store, the looks
        // and the model. JSON lines on stdin and stdout; see GlintBackend.
        case "serve":
        {
            using var backend = new GlintBackend(dataRoot, options);
            using var input = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
            using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
            await backend.RunAsync(input, output);
            break;
        }

        case "compatibility":
        {
            var report = new WindowsCompatibilityService().Inspect();
            WriteJson(report, json);
            if (options.ContainsKey("require-ready") && !report.ReadyForCoreCapture)
            {
                return 2;
            }

            break;
        }

        case "probe":
        {
            await DelayAsync(options);
            var window = ResolveWindow(options);
            IUiAutomationService prober = StutterTestMode.AccessibilityOff
                ? new InertAutomation()
                : new UiAutomationService();
            var automation = window is null
                ? null
                : prober.ProbeSecurity(window);
            var decision = window is null
                ? PrivacyDecision.Suppress(
                    SuppressReason.NoForegroundWindow,
                    "Windows did not report a foreground window")
                : new PrivacyGate().Evaluate(
                    window,
                    automation ?? new(false, false, false, "UI Automation unavailable"));
            WriteJson(new { window, automation, decision }, json);
            break;
        }

        case "request-borderless":
            WriteJson(
                new { allowed = await WindowsGraphicsCaptureService.RequestBorderlessAccessAsync() },
                json);
            break;

        case "capture":
        {
            await DelayAsync(options);
            var window = ResolveWindow(options)
                ?? throw new InvalidOperationException("No foreground window.");
            var automation = new UiAutomationService();
            var compatibilityKnownSafe = options.ContainsKey("compatibility-known-safe");
            var isWindowsCalculator =
                string.Equals(
                    window.ProcessName,
                    "CalculatorApp",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(window.Title, "Calculator", StringComparison.OrdinalIgnoreCase)
                && window.ExecutablePath?.Contains(
                    @"\WindowsApps\Microsoft.WindowsCalculator_",
                    StringComparison.OrdinalIgnoreCase) == true;
            if (compatibilityKnownSafe
                && !new[]
                    {
                        "Glint.Phase0.WpfTarget",
                        "Glint.Phase0.Win32Target",
                        "Glint.Phase0.App"
                    }
                    .Contains(window.ProcessName, StringComparer.OrdinalIgnoreCase)
                && !isWindowsCalculator)
            {
                throw new InvalidOperationException(
                    "--compatibility-known-safe is restricted to Glint diagnostic targets.");
            }

            var security = compatibilityKnownSafe
                ? new AutomationSecurityProbe(
                    true,
                    false,
                    true,
                    "Known-safe compatibility fixture; focused-control probe bypassed.")
                : automation.ProbeSecurity(window);
            var decision = new PrivacyGate().Evaluate(window, security);
            if (!decision.Allowed)
            {
                WriteJson(new { window, security, decision }, json);
                break;
            }

            var uia = automation.ExtractText(window);
            var ocr = await new WindowsGraphicsCaptureService(
                    options.ContainsKey("software-device"))
                .CaptureAndRecognizeAsync(window);
            var combined = ScreenText.Combine(uia.Text, ocr.Text);
            var dropReason = SecretSniffer.ShouldDrop(combined);
            var redacted = dropReason is null
                ? new DeterministicRedactor().Redact(combined)
                : null;
            WriteJson(
                new
                {
                    window.ProcessName,
                    window.Title,
                    compatibilityKnownSafe,
                    security,
                    uiAutomation = new
                    {
                        characters = uia.Text.Length,
                        uia.NodesVisited,
                        uia.Truncated,
                        uia.Error,
                        elapsedMs = uia.Elapsed.TotalMilliseconds
                    },
                    ocr = new
                    {
                        characters = ocr.Text.Length,
                        ocr.Width,
                        ocr.Height,
                        captureMs = ocr.CaptureElapsed.TotalMilliseconds,
                        ocrMs = ocr.OcrElapsed.TotalMilliseconds,
                        ocr.Error,
                        language = ocr.RecognizerLanguage
                    },
                    dropReason,
                    redactions = redacted?.Counts,
                    text = redacted?.Text
                },
                json);
            break;
        }

        case "page-probe":
        {
            var processName = options.GetValueOrDefault("process");
            var handle = processName is null
                ? 0
                : System.Diagnostics.Process.GetProcessesByName(processName)
                    .Select(process => process.MainWindowHandle)
                    .FirstOrDefault(value => value != 0);
            var target = processName is null
                ? ResolveWindow(options)
                : handle == 0 ? null : new ForegroundWindowInspector().Inspect(handle);
            if (target is null)
            {
                throw new InvalidOperationException("No window to probe.");
            }

            var trace = new List<string>();
            var probe = new UiAutomationService().ProbePage(target, trace);
            WriteJson(
                new
                {
                    target.ProcessName,
                    probe.IsBrowser,
                    probe.Site,
                    probe.DocumentBounds,
                    trace
                },
                json);
            break;
        }

        // Diagnostics: which part of a look blocks another app. Runs one part
        // (accessibility, screen grab, OCR, or nothing) against a window by
        // process name, repeatedly, while pinging that window's UI thread
        // every 10 ms. A blocked UI thread is what makes scrolling and
        // dragging stutter, so the ping delays say which part is to blame.
        case "stutter-probe":
        {
            var processName = RequireOption(options, "process");
            var part = options.GetValueOrDefault("part", "idle");
            var repeat = int.TryParse(options.GetValueOrDefault("repeat"), out var parsedRepeat) ? Math.Clamp(parsedRepeat, 1, 50) : 5;
            var handle = System.Diagnostics.Process.GetProcessesByName(processName)
                .Select(process => process.MainWindowHandle)
                .FirstOrDefault(value => value != 0);
            var target = handle == 0 ? null : new ForegroundWindowInspector().Inspect(handle);
            if (target is null)
            {
                throw new InvalidOperationException($"No window for {processName}.");
            }

            using var ping = StutterProbe.StartPinging(handle);
            var automation = new UiAutomationService();
            var frames = new WindowsGraphicsCaptureService();
            var partTimes = new List<double>();
            var characters = 0;
            for (var run = 0; run < repeat; run++)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                switch (part)
                {
                    case "accessibility":
                    {
                        var probe = automation.ProbePage(target);
                        // The page's text when the probe found one, else the window's.
                        var text = automation.ExtractPageText(target, probe);
                        characters = text.Text.Length;
                        break;
                    }

                    case "find-page":
                        automation.ProbePage(target);
                        break;

                    case "read-text":
                        characters = automation.ExtractText(target).Text.Length;
                        break;

                    case "visible-text":
                    {
                        // Only the first suspect: the visible ranges of the page document.
                        var probe = automation.ProbePage(target);
                        var document = typeof(PageProbe)
                            .GetProperty("Document", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?
                            .GetValue(probe) as System.Windows.Automation.AutomationElement;
                        if (document is not null
                            && document.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out var pattern)
                            && pattern is System.Windows.Automation.TextPattern text)
                        {
                            foreach (var range in text.GetVisibleRanges())
                            {
                                characters += range.GetText(64_000).Length;
                            }
                        }
                        else
                        {
                            characters = -1; // no document with a text pattern
                        }

                        break;
                    }

                    case "names-batched":
                    {
                        // Every element's name and value in one cached request,
                        // visible ones only, no text pattern.
                        var root = System.Windows.Automation.AutomationElement.FromHandle(handle);
                        var cache = new System.Windows.Automation.CacheRequest { TreeScope = System.Windows.Automation.TreeScope.Element };
                        cache.Add(System.Windows.Automation.AutomationElement.NameProperty);
                        cache.Add(System.Windows.Automation.AutomationElement.IsOffscreenProperty);
                        cache.Add(System.Windows.Automation.AutomationElement.IsPasswordProperty);
                        using (cache.Activate())
                        {
                            var all = root.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                            foreach (System.Windows.Automation.AutomationElement element in all)
                            {
                                if (element.Cached.IsOffscreen || element.Cached.IsPassword)
                                {
                                    continue;
                                }

                                characters += element.Cached.Name?.Length ?? 0;
                            }
                        }

                        break;
                    }

                    case "focus-check":
                        automation.ProbeSecurity(target);
                        break;

                    case "capture":
                    {
                        using var frame = await frames.CaptureFrameAsync(target);
                        break;
                    }

                    case "ocr":
                    {
                        using var frame = await frames.CaptureFrameAsync(target);
                        var ocr = await frame.RecognizeAsync(null);
                        characters = ocr.Text.Length;
                        break;
                    }

                    default:
                        await Task.Delay(500);
                        break;
                }

                partTimes.Add(timer.Elapsed.TotalMilliseconds);
                await Task.Delay(300);
            }

            var pings = ping.Stop();
            pings.Sort();
            double At(double q) => pings.Count == 0 ? 0 : pings[Math.Min(pings.Count - 1, (int)(q * pings.Count))];
            WriteJson(
                new
                {
                    process = processName,
                    part,
                    repeat,
                    partMedianMs = Math.Round(partTimes.OrderBy(x => x).ElementAt(partTimes.Count / 2)),
                    characters,
                    pings = pings.Count,
                    pingMedianMs = Math.Round(At(0.5), 1),
                    pingP99Ms = Math.Round(At(0.99), 1),
                    pingMaxMs = Math.Round(pings.Count == 0 ? 0 : pings[^1], 1),
                    pingsOver16Ms = pings.Count(x => x > 16),
                    pingsOver50Ms = pings.Count(x => x > 50)
                },
                json);
            break;
        }

        case "media-sessions":
        {
            WriteJson(new { sessions = new WindowsMediaSessionReader().Read() }, json);
            break;
        }

        case "runtime-status":
        {
            var resolution = LiteRtRuntimeLocator.Resolve(AppContext.BaseDirectory, dataRoot);
            WriteJson(resolution, json);
            break;
        }

        case "model-probe":
        {
            var runtime = RequireOption(options, "runtime");
            var model = RequireOption(options, "model");
            var result = await new LiteRtWorkerProbe().ProbeAsync(runtime, model);
            WriteJson(result, json);
            break;
        }

        case "model-generate":
        {
            var client = new LiteRtWorkerClient(
                RequireOption(options, "python"),
                RequireOption(options, "worker"),
                RequireOption(options, "model"),
                options.GetValueOrDefault("backend", LiteRtBackend.Current),
                int.TryParse(options.GetValueOrDefault("max-tokens"), out var maxTokens)
                    ? maxTokens
                    : 2048);
            var result = await client.GenerateAsync(
                new LiteRtGenerationRequest(
                    RequireOption(options, "prompt"),
                    options.GetValueOrDefault("system"),
                    Temperature: double.TryParse(
                            options.GetValueOrDefault("temperature"),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var parsedTemperature)
                        ? parsedTemperature
                        : 0,
                    Seed: int.TryParse(
                            options.GetValueOrDefault("seed"),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var parsedSeed)
                        ? parsedSeed
                        : null),
                TimeSpan.FromMinutes(5));
            WriteJson(result, json);
            break;
        }

        case "worker-bench":
        {
            var resolution = LiteRtRuntimeLocator.Resolve(AppContext.BaseDirectory, dataRoot);
            if (!resolution.IsReady)
            {
                throw new InvalidOperationException(
                    $"Gemma runtime is not ready. Missing: {string.Join(", ", resolution.Missing)}.");
            }

            var runs = int.TryParse(options.GetValueOrDefault("runs"), out var parsedRuns)
                ? Math.Clamp(parsedRuns, 1, 20)
                : 3;
            var benchPrompt = options.GetValueOrDefault(
                "prompt",
                "Reply with the single word: ok");
            using var worker = new PersistentLiteRtWorker(
                resolution.PythonExecutable!,
                resolution.WorkerScript!,
                resolution.ModelPath!,
                options.GetValueOrDefault("backend", LiteRtBackend.Current),
                int.TryParse(options.GetValueOrDefault("max-tokens"), out var benchTokens)
                    ? benchTokens
                    : 4096);

            var startsBefore = LiteRtWorkerMetrics.StartCount;
            var timings = new List<object>();
            for (var run = 1; run <= runs; run++)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var generated = await worker.GenerateAsync(
                    new(benchPrompt),
                    TimeSpan.FromMinutes(5));
                timer.Stop();
                timings.Add(new
                {
                    run,
                    wallMilliseconds = timer.ElapsedMilliseconds,
                    generationMilliseconds = Math.Round(
                        generated.GenerationElapsed.TotalMilliseconds),
                    characters = generated.Text.Length
                });
            }

            WriteJson(
                new
                {
                    runs,
                    workerStarts = LiteRtWorkerMetrics.StartCount - startsBefore,
                    stillLoaded = worker.IsLoaded,
                    timings
                },
                json);
            break;
        }

        case "activity-summarize":
        {
            var resolution = LiteRtRuntimeLocator.Resolve(AppContext.BaseDirectory, dataRoot);
            if (!resolution.IsReady)
            {
                throw new InvalidOperationException(
                    $"Gemma runtime is not ready. Missing: {string.Join(", ", resolution.Missing)}.");
            }

            var summarizer = new LiteRtActivitySummarizer(
                resolution.PythonExecutable!,
                resolution.WorkerScript!,
                resolution.ModelPath!,
                resolution.ModelId);
            var result = await summarizer.SummarizeAsync(
                DateTimeOffset.UtcNow,
                options.GetValueOrDefault("process", "Glint.Phase0.Cli"),
                options.GetValueOrDefault("title", "Manual summary probe"),
                RequireOption(options, "text"));
            WriteJson(result, json);
            break;
        }

        case "model-install":
        {
            var manifest = await ReadModelManifestAsync(options, json);
            using var http = new HttpClient();
            var manager = new ModelPackageManager(http, Path.Combine(dataRoot, "models"));
            var progress = new Progress<ModelInstallProgress>(value =>
                Console.Error.WriteLine(
                    $"{value.DownloadedBytes}/{value.TotalBytes} ({value.Fraction:P1})"));
            var installed = await manager.InstallAsync(manifest, progress);
            WriteJson(new { installed, active = manager.GetActiveModel() }, json);
            break;
        }

        case "model-repair":
        {
            var manifest = await ReadModelManifestAsync(options, json);
            using var http = new HttpClient();
            var manager = new ModelPackageManager(http, Path.Combine(dataRoot, "models"));
            var repaired = await manager.RepairAsync(manifest);
            WriteJson(new { repaired, active = manager.GetActiveModel() }, json);
            break;
        }

        case "model-rollback":
        {
            using var http = new HttpClient();
            var manager = new ModelPackageManager(http, Path.Combine(dataRoot, "models"));
            WriteJson(new { rolledBack = manager.Rollback(), active = manager.GetActiveModel() }, json);
            break;
        }

        case "model-confirm":
        {
            using var http = new HttpClient();
            var manager = new ModelPackageManager(http, Path.Combine(dataRoot, "models"));
            manager.ConfirmActiveVersion();
            WriteJson(new { confirmed = true, active = manager.GetActiveModel() }, json);
            break;
        }

        case "model-remove-active":
        {
            using var http = new HttpClient();
            var manager = new ModelPackageManager(http, Path.Combine(dataRoot, "models"));
            WriteJson(new { removed = manager.RemoveActiveModel() }, json);
            break;
        }

        case "model-remove-version":
        {
            using var http = new HttpClient();
            var manager = new ModelPackageManager(http, Path.Combine(dataRoot, "models"));
            var removed = manager.RemoveVersion(
                RequireOption(options, "model-id"),
                RequireOption(options, "version"));
            WriteJson(new { removed, active = manager.GetActiveModel() }, json);
            break;
        }

        case "help":
        default:
            Console.WriteLine(
                """
                Glint Windows Phase 0

                Commands:
                  serve [--data-dir PATH] [--host-pid PID] [--sqlite-vec PATH]
                        Glint's backend: the only process that opens the store.
                        JSON lines on stdin/stdout, e.g. {"id":1,"op":"activities"}.
                        Ops: scan probe focus mark activities sessions usage history
                             timeline search storage app-profiles app-mode activity-task
                             session-outcome chat-* ask ai-backend runtime-status summarize
                  compatibility [--require-ready]
                  probe [--delay 3]
                  request-borderless
                  capture [--delay 3] [--handle HWND] [--software-device]
                          [--compatibility-known-safe]
                  page-probe --process NAME
                  stutter-probe --process NAME
                  media-sessions
                  runtime-status [--data-dir PATH]
                  model-probe --runtime PATH --model PATH
                  model-generate --python PATH --worker PATH --model PATH --prompt TEXT [--backend cpu] [--temperature 0] [--seed 1]
                  worker-bench [--runs 3] [--prompt TEXT] [--max-tokens 4096] [--data-dir PATH]
                  activity-summarize --text TEXT [--process NAME] [--title TITLE]
                  model-install --manifest PATH [--data-dir PATH]
                  model-repair --manifest PATH [--data-dir PATH]
                  model-rollback [--data-dir PATH]
                  model-confirm [--data-dir PATH]
                  model-remove-active [--data-dir PATH]
                  model-remove-version --model-id ID --version VERSION [--data-dir PATH]

                --host-pid PID treats that process's windows as Glint itself
                (serve, probe, capture); hosts pass their own PID.
                --compatibility-known-safe is only for the shipped non-sensitive test fixture.
                Capture commands never write image pixels to disk.
                """);
            break;
    }

    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(
        args.Contains("--verbose", StringComparer.OrdinalIgnoreCase)
            ? error
            : $"{error.GetType().Name}: {error.Message}");
    return 1;
}

// --host-pid names the long-lived UI process (the Tauri host) so its windows
// are treated as Glint itself rather than as capturable foreground apps.
static int? HostProcessId(IReadOnlyDictionary<string, string> options) =>
    options.TryGetValue("host-pid", out var value)
    && int.TryParse(
        value,
        System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture,
        out var pid)
    && pid > 0
        ? pid
        : null;

static ForegroundWindowInfo? ResolveWindow(IReadOnlyDictionary<string, string> options)
{
    var inspector = new ForegroundWindowInspector(HostProcessId(options));
    if (!options.TryGetValue("handle", out var value))
    {
        return inspector.Inspect();
    }

    var normalized = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? value[2..]
        : value;
    var numberStyle = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? System.Globalization.NumberStyles.HexNumber
        : System.Globalization.NumberStyles.Integer;
    if (!long.TryParse(
            normalized,
            numberStyle,
            System.Globalization.CultureInfo.InvariantCulture,
            out var handle)
        || handle == 0)
    {
        throw new ArgumentException("--handle must be a non-zero decimal or hexadecimal HWND.");
    }

    return inspector.Inspect(new nint(handle));
}

static async Task DelayAsync(IReadOnlyDictionary<string, string> options)
{
    if (options.TryGetValue("delay", out var value)
        && int.TryParse(value, out var seconds)
        && seconds > 0)
    {
        Console.Error.WriteLine($"Switch to the target window. Capturing in {seconds} seconds...");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
    }
}

static Dictionary<string, string> ParseOptions(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < values.Length; index++)
    {
        var value = values[index];
        if (!value.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = value[2..];
        var optionValue = index + 1 < values.Length
                          && !values[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? values[++index]
            : "true";
        result[key] = optionValue;
    }

    return result;
}

static string RequireOption(IReadOnlyDictionary<string, string> options, string key) =>
    options.GetValueOrDefault(key)
    ?? throw new ArgumentException($"Missing required option --{key}.");

static async Task<ModelArtifactManifest> ReadModelManifestAsync(
    IReadOnlyDictionary<string, string> options,
    JsonSerializerOptions json)
{
    var manifestPath = RequireOption(options, "manifest");
    return JsonSerializer.Deserialize<ModelArtifactManifest>(
               await File.ReadAllTextAsync(manifestPath),
               json)
           ?? throw new InvalidDataException("Model manifest is invalid.");
}

static void WriteJson<T>(T value, JsonSerializerOptions options) =>
    Console.WriteLine(JsonSerializer.Serialize(value, options));

/// Pings a window's UI thread (WM_NULL through SendMessageTimeout) every
/// 10 ms on a background thread and records how long each answer took.
sealed class StutterProbe : IDisposable
{
    private readonly nint _window;
    private readonly List<double> _delays = [];
    private readonly Thread _thread;
    private volatile bool _stop;

    private StutterProbe(nint window)
    {
        _window = window;
        _thread = new Thread(Run) { IsBackground = true, Name = "stutter-probe" };
    }

    public static StutterProbe StartPinging(nint window)
    {
        var probe = new StutterProbe(window);
        probe._thread.Start();
        return probe;
    }

    public List<double> Stop()
    {
        _stop = true;
        _thread.Join();
        lock (_delays)
        {
            return [.. _delays];
        }
    }

    public void Dispose()
    {
        _stop = true;
    }

    private void Run()
    {
        while (!_stop)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            _ = SendMessageTimeoutW(_window, 0, 0, 0, 0x0002, 2_000, out _);
            lock (_delays)
            {
                _delays.Add(timer.Elapsed.TotalMilliseconds);
            }

            Thread.Sleep(10);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessageTimeoutW(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}
