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
            var automation = window is null
                ? null
                : new UiAutomationService().ProbeSecurity(window);
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
            var combined = CapturePipeline.CombineText(uia.Text, ocr.Text);
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

        case "pipeline":
        {
            await DelayAsync(options);
            using var database = OpenDatabase(dataRoot, options);
            var pipeline = new CapturePipeline(
                new ForegroundWindowInspector(),
                new UiAutomationService(),
                new PrivacyGate(),
                new WindowsGraphicsCaptureService(),
                new DeterministicRedactor(),
                database);
            var outcome = await pipeline.CaptureOnceAsync();
            WriteJson(
                new
                {
                    outcome,
                    eventCount = database.CountEvents(),
                    storage = database.GetDiagnostics()
                },
                json);
            break;
        }

        case "storage":
        {
            using var database = OpenDatabase(dataRoot, options);
            WriteJson(
                new
                {
                    database = database.GetDiagnostics(),
                    eventCount = database.CountEvents()
                },
                json);
            break;
        }

        case "search":
        {
            var query = options.GetValueOrDefault("query")
                ?? throw new ArgumentException("search requires --query <text>.");
            using var database = OpenDatabase(dataRoot, options);
            WriteJson(new { query, results = database.Search(query) }, json);
            break;
        }

        case "vector-smoke":
        {
            using var database = OpenDatabase(dataRoot, options);
            var first = new float[768];
            var second = new float[768];
            var query = new float[768];
            first[0] = 1;
            second[1] = 1;
            query[0] = 0.95f;
            query[1] = 0.05f;
            database.UpsertEmbedding(1, first);
            database.UpsertEmbedding(2, second);
            WriteJson(new { results = database.SearchEmbeddings(query, 2) }, json);
            break;
        }

        case "manual-history":
        {
            using var database = OpenDatabase(dataRoot, options);
            var limit = int.TryParse(options.GetValueOrDefault("limit"), out var parsedLimit)
                ? parsedLimit
                : 50;
            WriteJson(new { scans = database.GetRecentManualScans(limit) }, json);
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
                options.GetValueOrDefault("backend", "cpu"),
                int.TryParse(options.GetValueOrDefault("max-tokens"), out var maxTokens)
                    ? maxTokens
                    : 2048);
            var result = await client.GenerateAsync(
                new LiteRtGenerationRequest(
                    RequireOption(options, "prompt"),
                    options.GetValueOrDefault("system"),
                    Temperature: 0,
                    Seed: 1),
                TimeSpan.FromMinutes(5));
            WriteJson(result, json);
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
                  compatibility [--require-ready]
                  probe [--delay 3]
                  request-borderless
                  capture [--delay 3] [--handle HWND] [--software-device]
                          [--compatibility-known-safe]
                  pipeline [--delay 3] [--data-dir PATH] [--sqlite-vec PATH]
                  storage [--data-dir PATH] [--sqlite-vec PATH]
                  search --query TEXT [--data-dir PATH]
                  vector-smoke [--data-dir PATH]
                  manual-history [--limit 50] [--data-dir PATH]
                  model-probe --runtime PATH --model PATH
                  model-generate --python PATH --worker PATH --model PATH --prompt TEXT [--backend cpu]
                  activity-summarize --text TEXT [--process NAME] [--title TITLE]
                  model-install --manifest PATH [--data-dir PATH]
                  model-repair --manifest PATH [--data-dir PATH]
                  model-rollback [--data-dir PATH]
                  model-confirm [--data-dir PATH]
                  model-remove-active [--data-dir PATH]
                  model-remove-version --model-id ID --version VERSION [--data-dir PATH]

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

static Phase0Database OpenDatabase(
    string dataRoot,
    IReadOnlyDictionary<string, string> options)
{
    var keyStore = new DpapiKeyStore(Path.Combine(dataRoot, "secrets", "dbkey.bin"));
    var bundledVec = Path.Combine(
        AppContext.BaseDirectory,
        "runtimes",
        "win-x64",
        "native",
        "vec0.dll");
    var vec = options.GetValueOrDefault(
        "sqlite-vec",
        File.Exists(bundledVec) ? bundledVec : string.Empty);
    return Phase0Database.Open(Path.Combine(dataRoot, "memory.db"), keyStore, vec);
}

static ForegroundWindowInfo? ResolveWindow(IReadOnlyDictionary<string, string> options)
{
    var inspector = new ForegroundWindowInspector();
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
