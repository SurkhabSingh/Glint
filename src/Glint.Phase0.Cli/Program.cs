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
                new ForegroundWindowInspector(HostProcessId(options)),
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

        // Agent chat history. All chat text lives in the encrypted store;
        // nothing here prints prompts or answers to metrics, only the JSON
        // documents the frontend asked for.
        case "chat-threads":
        {
            using var database = OpenDatabase(dataRoot, options);
            var limit = int.TryParse(options.GetValueOrDefault("limit"), out var parsedLimit)
                ? Math.Clamp(parsedLimit, 1, 200)
                : 50;
            WriteJson(new { threads = database.GetRecentChatThreads(limit) }, json);
            break;
        }

        case "chat-thread":
        {
            using var database = OpenDatabase(dataRoot, options);
            var id = RequireOption(options, "id");
            var thread = database.GetChatThread(id)
                ?? throw new ArgumentException($"Unknown chat thread: {id}.");
            var limit = int.TryParse(options.GetValueOrDefault("limit"), out var parsedLimit)
                ? Math.Clamp(parsedLimit, 1, 2_000)
                : 200;
            WriteJson(
                new { thread, messages = database.GetChatMessages(id, limit) },
                json);
            break;
        }

        case "chat-create":
        {
            using var database = OpenDatabase(dataRoot, options);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            WriteJson(
                database.CreateChatThread(
                    RequireOption(options, "title"),
                    options.GetValueOrDefault("scope", "all") ?? "all",
                    long.TryParse(options.GetValueOrDefault("at"), out var parsedAt)
                        ? parsedAt
                        : now),
                json);
            break;
        }

        case "chat-rename":
        {
            using var database = OpenDatabase(dataRoot, options);
            var id = RequireOption(options, "id");
            database.RenameChatThread(
                id,
                RequireOption(options, "title"),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            WriteJson(new { id }, json);
            break;
        }

        case "chat-delete":
        {
            using var database = OpenDatabase(dataRoot, options);
            var id = RequireOption(options, "id");
            database.DeleteChatThread(id);
            WriteJson(new { id }, json);
            break;
        }

        case "chat-append":
        {
            using var database = OpenDatabase(dataRoot, options);
            WriteJson(
                database.AppendChatMessage(
                    RequireOption(options, "thread"),
                    RequireOption(options, "role"),
                    RequireOption(options, "text"),
                    options.GetValueOrDefault("citations", "[]") ?? "[]",
                    int.TryParse(options.GetValueOrDefault("scoped"), out var parsedScoped)
                        ? parsedScoped
                        : 0,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                json);
            break;
        }

        case "manual-scan":
        {
            await DelayAsync(options);
            // Capture no longer runs the model, so it does not need the Gemma
            // runtime and works even before one is installed. Summaries
            // arrive per session, from the sessionize verb.
            using var database = OpenDatabase(dataRoot, options);
            // The activity model: identity first, then what this kind of app
            // needs. Text only for reading work, compared line by line with
            // what the same page already stored.
            // A stutter test can turn either suspect off for one run.
            IUiAutomationService automation = StutterTestMode.AccessibilityOff
                ? new InertAutomation()
                : new UiAutomationService();
            IFrameCaptureService frames = StutterTestMode.CaptureOff
                ? new DisabledFrameCapture()
                : new WindowsGraphicsCaptureService(options.ContainsKey("software-device"));
            var coordinator = new ActivityScanCoordinator(
                new ForegroundWindowInspector(HostProcessId(options)),
                automation,
                (IPageReader)automation,
                new PrivacyGate(),
                frames,
                new DeterministicRedactor(),
                database);
            WriteJson(await coordinator.ScanAsync(), json);
            break;
        }

        // Diagnostics: what the page probe sees in a window. Records control
        // types, names and parsed hosts only, never field values.
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

        // One answer, for diagnostics and scripts. The app uses ask-serve.
        case "ask":
        {
            using var database = OpenDatabase(dataRoot, options);
            var resolution = LiteRtRuntimeLocator.Resolve(AppContext.BaseDirectory, dataRoot);
            using var worker = resolution.IsReady && !options.ContainsKey("no-model")
                ? new PersistentLiteRtWorker(
                    resolution.PythonExecutable!,
                    resolution.WorkerScript!,
                    resolution.ModelPath!,
                    maxNumTokens: 4096,
                    idleUnloadAfter: Timeout.InfiniteTimeSpan)
                : null;
            var answer = await new AskEngine(new DatabaseAskSource(database), worker).AnswerAsync(
                new AskRequest(
                    RequireOption(options, "question"),
                    options.GetValueOrDefault("scope"),
                    options.GetValueOrDefault("day")));
            WriteJson(answer, json);
            break;
        }

        // The app's Ask: one long-lived process that keeps the model loaded
        // between questions, so only the first question pays for loading it.
        // Reads one JSON request per line on stdin and writes one JSON answer
        // per line on stdout. Questions travel over the pipe, never in argv.
        case "ask-serve":
        {
            var line = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            using var input = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
            using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            PersistentLiteRtWorker? worker = null;
            try
            {
                while (true)
                {
                    var reading = input.ReadLineAsync();
                    if (await Task.WhenAny(reading, Task.Delay(TimeSpan.FromMinutes(30))) != reading)
                    {
                        break; // idle: the host starts a new one when needed
                    }

                    var requestLine = await reading;
                    if (requestLine is null)
                    {
                        break; // the host closed the pipe
                    }

                    if (string.IsNullOrWhiteSpace(requestLine))
                    {
                        continue;
                    }

                    try
                    {
                        var request = JsonSerializer.Deserialize<AskRequest>(requestLine, line)
                            ?? throw new InvalidDataException("Empty request.");
                        if (worker is null)
                        {
                            var resolution = LiteRtRuntimeLocator.Resolve(AppContext.BaseDirectory, dataRoot);
                            if (resolution.IsReady)
                            {
                                worker = new PersistentLiteRtWorker(
                                    resolution.PythonExecutable!,
                                    resolution.WorkerScript!,
                                    resolution.ModelPath!,
                                    maxNumTokens: 4096,
                                    idleUnloadAfter: TimeSpan.FromMinutes(5));
                            }
                        }

                        // Opened per question, so every answer sees the latest data.
                        using var database = OpenDatabase(dataRoot, options);
                        var answer = await new AskEngine(new DatabaseAskSource(database), worker).AnswerAsync(request);
                        await output.WriteLineAsync(JsonSerializer.Serialize(answer, line));
                    }
                    catch (Exception error)
                    {
                        await output.WriteLineAsync(JsonSerializer.Serialize(new { error = $"{error.GetType().Name}: {error.Message}" }, line));
                    }
                }
            }
            finally
            {
                worker?.Dispose();
            }

            break;
        }

        // The capture worker: one process for the whole time Glint runs, with
        // the encrypted database opened once. Every look, every window-switch
        // privacy check and every marker is a request on stdin (one JSON line)
        // answered on stdout (one JSON line, echoing the request id), so
        // nothing starts a process or unlocks the database per look.
        // Requests: {"id":1,"op":"scan"|"probe"|"mark"|"ping",
        //            "testMode":"normal", "handle":123, "kind":"...", "at":ms, "detail":"..."}
        case "scan-serve":
        {
            var line = new JsonSerializerOptions(json) { WriteIndented = false };
            using var input = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
            using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            using var database = OpenDatabase(dataRoot, options);
            var hostProcessId = HostProcessId(options);
            var inspector = new ForegroundWindowInspector(hostProcessId);
            var realAutomation = new UiAutomationService();
            var inertAutomation = new InertAutomation();
            var realFrames = new WindowsGraphicsCaptureService(options.ContainsKey("software-device"));
            var noFrames = new DisabledFrameCapture();
            var gate = new PrivacyGate();
            var redactor = new DeterministicRedactor();
            while (true)
            {
                var requestLine = await input.ReadLineAsync();
                if (requestLine is null)
                {
                    break; // the host closed the pipe (Glint exited)
                }

                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    continue;
                }

                long id = 0;
                try
                {
                    using var request = JsonDocument.Parse(requestLine);
                    var root = request.RootElement;
                    id = root.TryGetProperty("id", out var idValue) ? idValue.GetInt64() : 0;
                    var op = root.TryGetProperty("op", out var opValue) ? opValue.GetString() : null;
                    var testMode = root.TryGetProperty("testMode", out var modeValue) ? modeValue.GetString() : null;
                    Environment.SetEnvironmentVariable(
                        StutterTestMode.EnvironmentVariable,
                        string.IsNullOrWhiteSpace(testMode) || testMode == "normal" ? null : testMode);
                    IUiAutomationService automation = StutterTestMode.AccessibilityOff ? inertAutomation : realAutomation;
                    object result;
                    switch (op)
                    {
                        case "scan":
                        {
                            var coordinator = new ActivityScanCoordinator(
                                inspector,
                                automation,
                                (IPageReader)automation,
                                gate,
                                StutterTestMode.CaptureOff ? noFrames : realFrames,
                                redactor,
                                database);
                            result = await coordinator.ScanAsync();
                            break;
                        }

                        case "probe":
                        {
                            var handle = root.TryGetProperty("handle", out var handleValue) ? handleValue.GetInt64() : 0;
                            var window = handle == 0 ? inspector.Inspect() : inspector.Inspect(new nint(handle));
                            var security = window is null ? null : automation.ProbeSecurity(window);
                            var decision = window is null
                                ? PrivacyDecision.Suppress(SuppressReason.NoForegroundWindow, "Windows did not report a foreground window")
                                : gate.Evaluate(window, security ?? new(false, false, false, "UI Automation unavailable"));
                            result = new { window, automation = security, decision };
                            break;
                        }

                        case "mark":
                        {
                            var kind = root.GetProperty("kind").GetString()
                                ?? throw new InvalidDataException("A mark needs a kind.");
                            var at = root.TryGetProperty("at", out var atValue) && atValue.ValueKind == JsonValueKind.Number
                                ? atValue.GetInt64()
                                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            var detail = root.TryGetProperty("detail", out var detailValue) ? detailValue.GetString() : null;
                            database.RecordMarker(kind, at, detail);
                            result = new { kind, atMilliseconds = at, detail };
                            break;
                        }

                        case "ping":
                            result = new { ok = true, processId = Environment.ProcessId };
                            break;

                        default:
                            throw new InvalidDataException($"Unknown request: {op}.");
                    }

                    await output.WriteLineAsync(JsonSerializer.Serialize(new { id, result }, line));
                }
                catch (Exception error)
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(new { id, error = $"{error.GetType().Name}: {error.Message}" }, line));
                }
            }

            break;
        }

        // Diagnostics: recording and presence markers in a time range.
        case "markers":
        {
            using var database = OpenDatabase(dataRoot, options);
            var to = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var from = long.TryParse(options.GetValueOrDefault("since"), out var since)
                ? since
                : to - 86_400_000;
            WriteJson(new { markers = database.GetMarkers(from, to) }, json);
            break;
        }

        // Diagnostics: what Windows reports as playing.
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

        case "search-context":
        {
            var query = options.GetValueOrDefault("query")
                ?? throw new ArgumentException("search-context requires --query <text>.");
            var limit = int.TryParse(options.GetValueOrDefault("limit"), out var parsedLimit)
                ? parsedLimit
                : 30;
            using var database = OpenDatabase(dataRoot, options);
            WriteJson(
                new { query, results = database.SearchContext(query, limit) },
                json);
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

        // The user's own verdict on a session. Absolute: no rule overwrites it.
        case "session-outcome":
        {
            using var database = OpenDatabase(dataRoot, options);
            var sessionId = RequireOption(options, "id");
            var requested = RequireOption(options, "outcome");
            if (!Enum.TryParse<SessionOutcome>(requested, ignoreCase: true, out var outcome))
            {
                throw new ArgumentException(
                    $"--outcome must be one of: {string.Join(", ", Enum.GetNames<SessionOutcome>())}.");
            }

            var decidedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            database.SetSessionOutcome(sessionId, outcome, decidedAt);
            WriteJson(
                new { id = sessionId, outcome = outcome.ToString(), decidedAtMilliseconds = decidedAt },
                json);
            break;
        }

        // Records that the user went away or came back, or that recording
        // started or stopped. The sessionizer uses these to tell a real break
        // from a screen that simply did not change.
        case "mark":
        {
            using var database = OpenDatabase(dataRoot, options);
            var kind = RequireOption(options, "kind");
            var at = long.TryParse(options.GetValueOrDefault("at"), out var parsedAt)
                ? parsedAt
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var detail = options.GetValueOrDefault("detail");
            database.RecordMarker(kind, at, detail);
            WriteJson(new { kind, atMilliseconds = at, detail }, json);
            break;
        }

        // At startup: close a recording a crash or shutdown left open, and
        // report whether anything is waiting to be grouped or summarized.
        case "recover":
        {
            using var database = OpenDatabase(dataRoot, options);
            var recovery = database.RecoverUnfinishedRun(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            WriteJson(recovery, json);
            break;
        }

        // Sessions, newest first, with their summaries. The UI shows these
        // rather than per-capture rows.
        case "sessions":
        {
            using var database = OpenDatabase(dataRoot, options);
            var sessionLimit = int.TryParse(options.GetValueOrDefault("limit"), out var parsedLimit)
                ? Math.Clamp(parsedLimit, 1, 200)
                : 50;
            WriteJson(new { sessions = database.GetRecentSessions(sessionLimit) }, json);
            break;
        }

        // Groups ungrouped captures into sessions and summarizes them: one
        // model call per session rather than one per capture. Safe to run
        // repeatedly; sessions still in progress are left alone.
        case "sessionize":
        {
            using var database = OpenDatabase(dataRoot, options);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                + (options.ContainsKey("seal-open") ? Sessionizer.QuietTailMilliseconds : 0);

            // Sealing sessions and separating activities need no model, so
            // they run even before the local AI runtime is installed. Only
            // reading activities wait for it.
            var sessionBuilder = new SessionBuilder(
                database,
                new NoSessionSummaries(),
                summarizeSessions: false);
            var sealedResult = await sessionBuilder.RunAsync(now);

            var resolution = LiteRtRuntimeLocator.Resolve(AppContext.BaseDirectory, dataRoot);
            var maxNarrations = int.TryParse(options.GetValueOrDefault("max-summaries"), out var parsedMax)
                ? Math.Clamp(parsedMax, 0, 100)
                : 8;
            var startsBefore = LiteRtWorkerMetrics.StartCount;
            ActivityBuildResult activities;
            var earlyNarrated = 0;
            string? backendUsed = null;
            if (resolution.IsReady && maxNarrations > 0)
            {
                // One worker for every summary in this run, so the model loads
                // once no matter how many activities are described.
                using var worker = new PersistentLiteRtWorker(
                    resolution.PythonExecutable!,
                    resolution.WorkerScript!,
                    resolution.ModelPath!,
                    maxNumTokens: 4096,
                    idleUnloadAfter: Timeout.InfiniteTimeSpan);
                var builder = new ActivityBuilder(
                    database,
                    database,
                    new LiteRtActivityNarrator(worker),
                    maxNarrations);
                activities = await builder.RunAsync();
                // While recording: also summarize activities of the sitting in
                // progress that have already ended (closed, or moved on from).
                if (options.ContainsKey("ended"))
                {
                    earlyNarrated = await builder.NarrateEndedAsync(
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Math.Max(0, maxNarrations - activities.Narrated));
                }

                backendUsed = LiteRtWorkerMetrics.StartCount > startsBefore ? worker.Backend : null;
            }
            else
            {
                activities = await new ActivityBuilder(database, database, null).RunAsync();
            }

            WriteJson(
                new
                {
                    sealedResult.Sealed,
                    // Hosts loop while this is above zero: progress means an
                    // activity was described in this run.
                    summarized = activities.Narrated,
                    earlyNarrated,
                    failed = activities.NarrationFailed,
                    sealedResult.Decided,
                    sealedResult.Threaded,
                    activities = activities.Activities,
                    sessionsBuilt = activities.SessionsProcessed,
                    verified = activities.Verified,
                    partial = activities.Partial,
                    fallback = activities.Fallback,
                    runtimeReady = resolution.IsReady,
                    workerStarts = LiteRtWorkerMetrics.StartCount - startsBefore,
                    // Which processor the model ran on, and why not the GPU if it fell back.
                    aiBackend = backendUsed,
                    gpuFailure = LiteRtBackend.GpuFailure
                },
                json);
            break;
        }

        // Activities, newest first: the unit the timeline and Activity page show.
        case "activities":
        {
            using var database = OpenDatabase(dataRoot, options);
            var limit = int.TryParse(options.GetValueOrDefault("limit"), out var parsedLimit)
                ? Math.Clamp(parsedLimit, 1, 1000)
                : 200;
            WriteJson(new { activities = database.GetRecentActivities(limit) }, json);
            break;
        }

        // Time per activity inside [from, to) for the dashboard chart: stored
        // activities plus what is being recorded right now. Slim rows only:
        // no summaries, tasks or captured text.
        case "usage":
        {
            using var database = OpenDatabase(dataRoot, options);
            var from = long.Parse(RequireOption(options, "from"), System.Globalization.CultureInfo.InvariantCulture);
            var to = long.Parse(RequireOption(options, "to"), System.Globalization.CultureInfo.InvariantCulture);
            if (to <= from)
            {
                throw new ArgumentException("--to must be after --from.");
            }

            var source = new DatabaseAskSource(database);
            var stored = source.GetActivitiesBetween(from, to);
            var live = source.GetLiveActivities()
                .Where(activity => activity.StartedAtMilliseconds < to
                    && Math.Max(activity.EndedAtMilliseconds, activity.StartedAtMilliseconds + 1) > from)
                .Where(activity => !stored.Any(existing => existing.Key == activity.Key
                    && existing.StartedAtMilliseconds == activity.StartedAtMilliseconds));
            var rows = stored.Concat(live)
                .Select(activity => new
                {
                    id = activity.Id,
                    app = activity.App,
                    site = activity.Site,
                    label = string.IsNullOrWhiteSpace(activity.Label) ? activity.Subject : activity.Label,
                    subject = activity.Subject,
                    mode = activity.Mode.ToString(),
                    category = activity.Category.ToString(),
                    startedAtMilliseconds = activity.StartedAtMilliseconds,
                    endedAtMilliseconds = activity.EndedAtMilliseconds,
                    segments = (activity.Segments.Count > 0
                            ? activity.Segments
                            : [new ActivitySegment(activity.StartedAtMilliseconds, activity.EndedAtMilliseconds)])
                        .Select(segment => new[] { segment.StartMilliseconds, segment.EndMilliseconds })
                        .ToList()
                })
                .ToList();
            WriteJson(new { from, to, activities = rows }, json);
            break;
        }

        // The user's verdict on an activity's task. Absolute: no rule rewrites it.
        case "activity-task":
        {
            using var database = OpenDatabase(dataRoot, options);
            var id = RequireOption(options, "id");
            var requested = RequireOption(options, "status");
            if (!Enum.TryParse<ActivityTaskStatus>(requested, ignoreCase: true, out var status))
            {
                throw new ArgumentException(
                    $"--status must be one of: {string.Join(", ", Enum.GetNames<ActivityTaskStatus>())}.");
            }

            var updated = database.SetActivityTaskStatus(id, status)
                ?? throw new ArgumentException($"No activity {id}.");
            WriteJson(new { activity = updated }, json);
            break;
        }

        // How Glint treats an app or site: Read, Make, Play, Watch or Private.
        // The user's choice wins over the catalog and over learning.
        case "app-mode":
        {
            using var database = OpenDatabase(dataRoot, options);
            var key = RequireOption(options, "key");
            if (!key.StartsWith("app:", StringComparison.Ordinal) && !key.StartsWith("site:", StringComparison.Ordinal))
            {
                throw new ArgumentException("--key must start with app: or site:.");
            }

            if (!Enum.TryParse<ActivityMode>(RequireOption(options, "mode"), ignoreCase: true, out var mode))
            {
                throw new ArgumentException(
                    $"--mode must be one of: {string.Join(", ", Enum.GetNames<ActivityMode>())}.");
            }

            ActivityCategory? category = null;
            if (options.TryGetValue("category", out var categoryText))
            {
                category = Enum.TryParse<ActivityCategory>(categoryText, ignoreCase: true, out var parsedCategory)
                    ? parsedCategory
                    : throw new ArgumentException(
                        $"--category must be one of: {string.Join(", ", Enum.GetNames<ActivityCategory>())}.");
            }

            var profile = database.SetAppModeByUser(
                key,
                mode,
                category,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            WriteJson(new { profile }, json);
            break;
        }

        case "app-profiles":
        {
            using var database = OpenDatabase(dataRoot, options);
            WriteJson(new { profiles = database.GetAppProfiles() }, json);
            break;
        }

        // Exercises the persistent worker: N generations through one process,
        // so worker starts should be 1 regardless of the run count.
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
                  manual-scan [--delay 3] [--software-device] [--data-dir PATH] [--sqlite-vec PATH]
                  runtime-status [--data-dir PATH]
                  search-context --query TEXT [--limit 30] [--data-dir PATH]
                  model-probe --runtime PATH --model PATH
                  model-generate --python PATH --worker PATH --model PATH --prompt TEXT [--backend cpu] [--temperature 0] [--seed 1]
                  session-outcome --id ID --outcome open|settled|unknown [--data-dir PATH]
                  mark --kind run.started|run.stopped|user.away|user.returned|user.locked|user.unlocked|system.sleep|system.resumed|system.shutdown|app.closed [--at MS] [--detail APP]
                  recover [--data-dir PATH]
                  sessions [--limit 50] [--data-dir PATH] [--sqlite-vec PATH]
                  sessionize [--max-summaries 8] [--seal-open] [--ended] [--data-dir PATH] [--sqlite-vec PATH]
                  activities [--limit 200] [--data-dir PATH]
                  usage --from MS --to MS [--data-dir PATH]
                  activity-task --id ID --status Open|LooksDone|Done|None [--data-dir PATH]
                  app-mode --key app:NAME|site:HOST --mode Read|Make|Play|Watch|Private [--category NAME]
                  app-profiles [--data-dir PATH]
                  ask --question TEXT [--scope day|week|all] [--day YYYY-MM-DD] [--no-model]
                  ask-serve [--data-dir PATH]   (JSON lines on stdin/stdout)
                  scan-serve [--data-dir PATH] [--host-pid PID]   (capture worker: JSON lines on stdin/stdout)
                  worker-bench [--runs 3] [--prompt TEXT] [--max-tokens 4096] [--data-dir PATH]
                  activity-summarize --text TEXT [--process NAME] [--title TITLE]
                  model-install --manifest PATH [--data-dir PATH]
                  model-repair --manifest PATH [--data-dir PATH]
                  model-rollback [--data-dir PATH]
                  model-confirm [--data-dir PATH]
                  model-remove-active [--data-dir PATH]
                  model-remove-version --model-id ID --version VERSION [--data-dir PATH]

                --host-pid PID treats that process's windows as Glint itself
                (probe, capture, pipeline, manual-scan); hosts pass their own PID.
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

/// <summary>
/// Stands in for the per-session summarizer, which sessionize no longer
/// calls: activities are described one at a time instead.
/// </summary>
sealed class NoSessionSummaries : IActivitySummarizer
{
    public string ModelId => "none";

    public Task<ActivitySummary> SummarizeAsync(
        DateTimeOffset capturedAt,
        string processName,
        string windowTitle,
        string redactedText,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Sessions are described per activity.");
}

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
