using Glint.Phase0.Core;
using System.Text.Json;

/// <summary>
/// Glint's one backend process. Everything that touches the store, the screen
/// or the local model is a request to it: looks, window switches, markers,
/// every read the screens make, Ask, and the summaries written while the user
/// is idle. The host starts it once and keeps it running.
/// </summary>
/// <remarks>
/// Protocol: one JSON object per line each way. A request carries an id and
/// an op (<c>{"id":7,"op":"activities","limit":300}</c>); its reply echoes the
/// id with a result or an error. Requests run concurrently and replies come
/// back as each finishes, so a slow look never holds up a screen's read.
/// Lines without an id are events the backend sends on its own
/// (<c>{"event":"sessions-updated","data":{...}}</c>).
///
/// One connection writes, guarded by one lock, so nothing ever waits on
/// another writer's busy timeout. Reads open their own read-only connection,
/// which never blocks the writer. Looks, window-switch checks and probes
/// share one lock because they share the accessibility client. The model is
/// loaded once, shared by Ask and summaries, and unloaded when idle.
/// </remarks>
sealed class GlintBackend : IDisposable
{
    /// Summaries wait for the user to step away from the keyboard this long.
    private const long IdleBeforeSummariesMilliseconds = 60_000;

    /// A game or video looked at this recently is still in front.
    private const long VisualFrontMilliseconds = 180_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _dataRoot;
    private readonly IReadOnlyDictionary<string, string> _options;
    private readonly Phase0Database _writer;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly SemaphoreSlim _capture = new(1, 1);
    private readonly SemaphoreSlim _model = new(1, 1);
    private readonly SemaphoreSlim _output = new(1, 1);
    private readonly SemaphoreSlim _summaryKick = new(0, int.MaxValue);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ForegroundWindowInspector _inspector;
    private readonly UiAutomationService _automation = new();
    private readonly InertAutomation _inertAutomation = new();
    private readonly WindowsGraphicsCaptureService _frames;
    private readonly DisabledFrameCapture _noFrames = new();
    private readonly PrivacyGate _gate = new();
    private readonly DeterministicRedactor _redactor = new();

    private TextWriter? _out;
    private PersistentLiteRtWorker? _worker;
    private volatile bool _recording;
    private long _lastLookAt;
    private ActivityMode? _lastLookMode;

    public GlintBackend(string dataRoot, IReadOnlyDictionary<string, string> options)
    {
        _dataRoot = dataRoot;
        _options = options;
        _writer = OpenStore(readOnly: false);
        _inspector = new ForegroundWindowInspector(HostProcessId(options));
        _frames = new WindowsGraphicsCaptureService(options.ContainsKey("software-device"));
    }

    public async Task RunAsync(TextReader input, TextWriter output)
    {
        _out = output;
        var now = Now();
        // What an earlier version left behind.
        LegacyTimelineImport.Run(_writer, _dataRoot, _redactor);
        if (_options.ContainsKey("recording"))
        {
            // Started again mid-recording: the run and the stretch in front go on.
            _recording = true;
        }
        else
        {
            // What a crash left open ends where it was last seen.
            _writer.CloseDanglingFocus();
            _writer.CloseUnfinishedRun(now);
        }

        var summaries = Task.Run(() => SummaryLoopAsync(_shutdown.Token));
        var running = new List<Task>();
        while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            running.RemoveAll(task => task.IsCompleted);
            running.Add(Task.Run(() => HandleAsync(line)));
        }

        // The host closed the pipe: Glint is exiting.
        _shutdown.Cancel();
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        await Task.WhenAny(summaries, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
    }

    private async Task HandleAsync(string line)
    {
        long id = 0;
        try
        {
            using var request = JsonDocument.Parse(line);
            var root = request.RootElement;
            id = root.TryGetProperty("id", out var idValue) ? idValue.GetInt64() : 0;
            var op = Str(root, "op") ?? throw new InvalidDataException("A request needs an op.");
            var result = await DispatchAsync(op, root).ConfigureAwait(false);
            await SendAsync(new { id, result }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await SendAsync(new { id, error = Describe(error) }).ConfigureAwait(false);
        }
    }

    private Task<object?> DispatchAsync(string op, JsonElement request) => op switch
    {
        "ping" => Done(new { ok = true, processId = Environment.ProcessId }),
        "scan" => ScanAsync(request),
        "probe" => ProbeAsync(request),
        "focus" => FocusAsync(request),
        "mark" => MarkAsync(request),
        "activities" => Read(store => Activities(store, request)),
        "sessions" => Read(store => Sessions(store, request)),
        "usage" => Read(store => Usage(store, request)),
        "history" => Read(store => History(store, request)),
        "timeline" => Read(store => Timeline(store, request)),
        "search" => Read(store => (object)new { results = store.SearchContext(Req(request, "query"), Int(request, "limit") ?? 30) }),
        "storage" => Read(store => (object)new { database = store.GetDiagnostics(), eventCount = store.CountChunks() }),
        "app-profiles" => Read(store => (object)new { profiles = store.GetAppProfiles() }),
        "chat-threads" => Read(store => (object)new { threads = store.GetRecentChatThreads(Math.Clamp(Int(request, "limit") ?? 50, 1, 200)) }),
        "chat-thread" => Read(store => ChatThread(store, request)),
        "chat-create" => Write(store => store.CreateChatThread(Req(request, "title"), Str(request, "scope") ?? "all", Now())),
        "chat-rename" => Write(store => { store.RenameChatThread(Req(request, "id"), Req(request, "title"), Now()); return new { id = Req(request, "id") }; }),
        "chat-delete" => Write(store => { store.DeleteChatThread(Req(request, "id")); return new { id = Req(request, "id") }; }),
        "chat-append" => Write(store => store.AppendChatMessage(
            Req(request, "thread"),
            Req(request, "role"),
            Req(request, "text"),
            Str(request, "citations") ?? "[]",
            Int(request, "scoped") ?? 0,
            Now())),
        "app-mode" => Write(store => AppMode(store, request)),
        "activity-task" => Write(store => ActivityTask(store, request)),
        "session-outcome" => Write(store => SessionOutcome(store, request)),
        "ask" => AskAsync(request),
        "ai-backend" => AiBackendAsync(request),
        "runtime-status" => Done(LiteRtRuntimeLocator.Resolve(AppContext.BaseDirectory, _dataRoot)),
        "summarize" => Done(Kick()),
        _ => throw new InvalidDataException($"Unknown request: {op}.")
    };

    // -----------------------------------------------------------------------
    // Capture: looks, window switches, markers
    // -----------------------------------------------------------------------

    private async Task<object?> ScanAsync(JsonElement request)
    {
        ApplyTestMode(request);
        await _capture.WaitAsync().ConfigureAwait(false);
        try
        {
            await _write.WaitAsync().ConfigureAwait(false);
            try
            {
                IUiAutomationService automation = StutterTestMode.AccessibilityOff ? _inertAutomation : _automation;
                var coordinator = new ActivityScanCoordinator(
                    _inspector,
                    automation,
                    (IPageReader)automation,
                    _gate,
                    StutterTestMode.CaptureOff ? _noFrames : _frames,
                    _redactor,
                    _writer);
                var outcome = await coordinator.ScanAsync().ConfigureAwait(false);
                var now = Now();
                _writer.TouchFocus(now);
                Interlocked.Exchange(ref _lastLookAt, now);
                _lastLookMode = outcome.Mode;
                return outcome;
            }
            finally
            {
                _write.Release();
            }
        }
        finally
        {
            _capture.Release();
        }
    }

    private async Task<object?> ProbeAsync(JsonElement request)
    {
        ApplyTestMode(request);
        await _capture.WaitAsync().ConfigureAwait(false);
        try
        {
            var handle = Long(request, "handle") ?? 0;
            var window = handle == 0 ? _inspector.Inspect() : _inspector.Inspect(new nint(handle));
            IUiAutomationService automation = StutterTestMode.AccessibilityOff ? _inertAutomation : _automation;
            var security = window is null ? null : automation.ProbeSecurity(window);
            var decision = window is null
                ? PrivacyDecision.Suppress(SuppressReason.NoForegroundWindow, "Windows did not report a foreground window")
                : _gate.Evaluate(window, security ?? new(false, false, false, "UI Automation unavailable"));
            return new { window, automation = security, decision };
        }
        finally
        {
            _capture.Release();
        }
    }

    /// <summary>
    /// A window came to the front. Logged only while recording, and only its
    /// app when the privacy gate keeps its title out. Returns the new focus
    /// row for the live timeline, or null when nothing changed.
    /// </summary>
    private async Task<object?> FocusAsync(JsonElement request)
    {
        if (!_recording)
        {
            return null;
        }

        var at = Long(request, "at") ?? Now();
        var handle = Long(request, "handle") ?? 0;
        var row = await LogFocusAsync(handle, at).ConfigureAwait(false);
        return row is null ? null : TimelineRow(row, open: true);
    }

    private async Task<FocusRow?> LogFocusAsync(long handle, long at)
    {
        await _capture.WaitAsync().ConfigureAwait(false);
        try
        {
            var window = handle == 0 ? _inspector.Inspect() : _inspector.Inspect(new nint(handle));
            if (window is null || window.IsSelf)
            {
                return null;
            }

            IUiAutomationService automation = StutterTestMode.AccessibilityOff ? _inertAutomation : _automation;
            var decision = _gate.Evaluate(window, automation.ProbeSecurity(window));
            var title = decision.Allowed ? _redactor.Redact(window.Title).Text : null;
            await _write.WaitAsync().ConfigureAwait(false);
            try
            {
                var open = _writer.GetOpenFocus();
                if (open is not null && open.ProcessName == window.ProcessName && open.Title == title)
                {
                    // Refocusing the same window: still the same stretch.
                    _writer.TouchFocus(at);
                    return null;
                }

                return _writer.OpenFocus(
                    at,
                    window.ProcessName,
                    title,
                    decision.Allowed ? null : decision.Reason?.ToString() ?? "Unknown",
                    decision.Allowed ? null : decision.Detail);
            }
            finally
            {
                _write.Release();
            }
        }
        finally
        {
            _capture.Release();
        }
    }

    private async Task<object?> MarkAsync(JsonElement request)
    {
        var kind = Req(request, "kind");
        var at = Long(request, "at") ?? Now();
        var detail = Str(request, "detail");
        var marker = new ActivityMarker(at, kind, detail);
        await _write.WaitAsync().ConfigureAwait(false);
        try
        {
            _writer.RecordMarker(kind, at, detail);
            if (marker.EndsAStretch)
            {
                _writer.CloseFocus(at);
            }
        }
        finally
        {
            _write.Release();
        }

        _recording = kind switch
        {
            "run.started" => true,
            "run.stopped" or "system.shutdown" => false,
            _ => _recording
        };

        // Back at the PC, in whatever is in front: that starts a stretch even
        // though no window switch happened.
        FocusRow? resumed = null;
        if (_recording && kind is "run.started" or "user.returned" or "user.unlocked" or "system.resumed")
        {
            resumed = await LogFocusAsync(0, at).ConfigureAwait(false);
        }

        if (marker.EndsAStretch || marker.ClosesApp)
        {
            Kick();
        }

        return new
        {
            kind,
            atMilliseconds = at,
            detail,
            timeline = kind switch
            {
                "run.started" => new { id = $"m-{at}-start", ts_wall_ms = at, kind = "scan.started" },
                "run.stopped" => new { id = $"m-{at}-stop", ts_wall_ms = at, kind = "scan.stopped" },
                _ => null
            },
            focus = resumed is null ? null : TimelineRow(resumed, open: true)
        };
    }

    // -----------------------------------------------------------------------
    // Reads: worked out on demand from what was recorded
    // -----------------------------------------------------------------------

    private static object Activities(Phase0Database store, JsonElement request)
    {
        var now = Now();
        var to = Long(request, "to") ?? now + 1;
        var from = Long(request, "from") ?? to - 30 * 86_400_000L;
        var limit = Math.Clamp(Int(request, "limit") ?? 300, 1, 5_000);
        var activities = new ActivityView(store).Build(from, to).Activities
            .OrderByDescending(activity => activity.StartedAtMilliseconds)
            .Take(limit)
            .ToList();
        return new { activities };
    }

    private static object Sessions(Phase0Database store, JsonElement request)
    {
        var now = Now();
        var limit = Math.Clamp(Int(request, "limit") ?? 50, 1, 500);
        var sessions = new ActivityView(store).Build(now - 30 * 86_400_000L, now + 1).Sessions
            .OrderByDescending(session => session.StartedAtMilliseconds)
            .Take(limit)
            .ToList();
        return new { sessions };
    }

    /// Time per activity inside [from, to) for the dashboard: slim rows only,
    /// no summaries or captured text.
    private static object Usage(Phase0Database store, JsonElement request)
    {
        var from = Long(request, "from") ?? throw new ArgumentException("usage needs from.");
        var to = Long(request, "to") ?? throw new ArgumentException("usage needs to.");
        if (to <= from)
        {
            throw new ArgumentException("to must be after from.");
        }

        var rows = new ActivityView(store).Build(from, to).Activities
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
        return new { from, to, activities = rows };
    }

    private static object History(Phase0Database store, JsonElement request)
    {
        var looks = store.GetRecentLooks(Math.Clamp(Int(request, "limit") ?? 50, 1, 500));
        if (looks.Count == 0)
        {
            return new { scans = looks };
        }

        var from = looks.Min(look => look.CapturedAtMilliseconds);
        var sessionByLook = new ActivityView(store).Build(from, Now() + 1).SessionByLook;
        return new
        {
            scans = looks
                .Select(look => look with { SessionId = sessionByLook.GetValueOrDefault(look.Id) })
                .ToList()
        };
    }

    /// <summary>
    /// The focus log for [from, to) in the shape the Timeline reads, with the
    /// recording's starts and stops between them.
    /// </summary>
    private static object Timeline(Phase0Database store, JsonElement request)
    {
        var from = Long(request, "from") ?? throw new ArgumentException("timeline needs from.");
        var to = Long(request, "to") ?? throw new ArgumentException("timeline needs to.");
        var events = new List<object>();
        events.AddRange(store.GetFocusBetween(from, to).Select(row => TimelineRow(row, open: row.EndedAtMilliseconds is null)));
        foreach (var marker in store.GetMarkers(from, to - 1))
        {
            var kind = marker.Kind switch
            {
                "run.started" => "scan.started",
                "run.stopped" => "scan.stopped",
                _ => null
            };
            if (kind is not null)
            {
                events.Add(new { id = $"m-{marker.TimestampMilliseconds}-{kind}", ts_wall_ms = marker.TimestampMilliseconds, kind });
            }
        }

        // Recording runs, paired from their markers.
        var eons = new List<object>();
        long? started = null;
        foreach (var marker in store.GetMarkers(Now() - 90 * 86_400_000L, Now() + 1))
        {
            if (marker.Kind == "run.started")
            {
                if (started is { } open)
                {
                    eons.Add(new { id = $"eon-{open}", started_ms = open, ended_ms = (long?)marker.TimestampMilliseconds });
                }

                started = marker.TimestampMilliseconds;
            }
            else if (marker.Kind is "run.stopped" or "system.shutdown" && started is { } open)
            {
                eons.Add(new { id = $"eon-{open}", started_ms = open, ended_ms = (long?)marker.TimestampMilliseconds });
                started = null;
            }
        }

        if (started is { } running)
        {
            eons.Add(new { id = $"eon-{running}", started_ms = running, ended_ms = (long?)null });
        }

        return new { events, eons };
    }

    private static object TimelineRow(FocusRow row, bool open) => new
    {
        id = $"f-{row.Id}",
        ts_wall_ms = row.StartedAtMilliseconds,
        ended_ms = row.EndedAtMilliseconds ?? (open ? (long?)null : row.LastSeenMilliseconds),
        kind = "window.focused",
        accuracy = "hook-exact",
        process = row.ProcessName,
        title = row.Title,
        suppressed = row.Suppressed,
        suppressedDetail = row.SuppressedDetail
    };

    private static object ChatThread(Phase0Database store, JsonElement request)
    {
        var id = Req(request, "id");
        var thread = store.GetChatThread(id) ?? throw new ArgumentException($"Unknown chat thread: {id}.");
        return new { thread, messages = store.GetChatMessages(id, Math.Clamp(Int(request, "limit") ?? 200, 1, 2_000)) };
    }

    // -----------------------------------------------------------------------
    // What the user says
    // -----------------------------------------------------------------------

    private static object AppMode(Phase0Database store, JsonElement request)
    {
        var key = Req(request, "key");
        if (!key.StartsWith("app:", StringComparison.Ordinal) && !key.StartsWith("site:", StringComparison.Ordinal))
        {
            throw new ArgumentException("The key must name an app (app:) or a site (site:).");
        }

        if (!Enum.TryParse<ActivityMode>(Req(request, "mode"), ignoreCase: true, out var mode))
        {
            throw new ArgumentException($"mode must be one of: {string.Join(", ", Enum.GetNames<ActivityMode>())}.");
        }

        ActivityCategory? category = Str(request, "category") is { } text
            ? Enum.Parse<ActivityCategory>(text, ignoreCase: true)
            : null;
        return new { profile = store.SetAppModeByUser(key, mode, category, Now()) };
    }

    private static object ActivityTask(Phase0Database store, JsonElement request)
    {
        var id = Req(request, "id");
        if (!Enum.TryParse<ActivityTaskStatus>(Req(request, "status"), ignoreCase: true, out var status))
        {
            throw new ArgumentException($"status must be one of: {string.Join(", ", Enum.GetNames<ActivityTaskStatus>())}.");
        }

        store.SetActivityTaskStatus(id, status, Now());
        return new { id, status = status.ToString() };
    }

    private static object SessionOutcome(Phase0Database store, JsonElement request)
    {
        var id = Req(request, "id");
        if (!Enum.TryParse<Glint.Phase0.Core.SessionOutcome>(Req(request, "outcome"), ignoreCase: true, out var outcome))
        {
            throw new ArgumentException($"outcome must be one of: {string.Join(", ", Enum.GetNames<Glint.Phase0.Core.SessionOutcome>())}.");
        }

        var at = Now();
        store.SetSessionOutcome(id, outcome, at);
        return new { id, outcome = outcome.ToString(), decidedAtMilliseconds = at };
    }

    // -----------------------------------------------------------------------
    // The local model: Ask, and summaries while the user is idle
    // -----------------------------------------------------------------------

    /// <summary>
    /// Answers a question. With a thread, the recent turns go with it and the
    /// question and answer are kept, the question first so a crash during the
    /// answer still leaves a truthful thread.
    /// </summary>
    private async Task<object?> AskAsync(JsonElement request)
    {
        var question = Req(request, "question").Trim();
        if (question.Length == 0)
        {
            throw new ArgumentException("Type a question or a message.");
        }

        var threadId = Str(request, "threadId") is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        IReadOnlyList<AskTurn> history = [];
        if (threadId is not null)
        {
            using var store = OpenStore(readOnly: true);
            history = RecentTurns(store.GetChatMessages(threadId, 200), 8);
            await Write(writer => writer.AppendChatMessage(threadId, ChatRoles.User, question, "[]", 0, Now())).ConfigureAwait(false);
        }

        try
        {
            AskResponse answer;
            await _model.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                using var store = OpenStore(readOnly: true);
                answer = await new AskEngine(new ViewAskSource(store), Worker())
                    .AnswerAsync(new AskRequest(question, Str(request, "scope"), Str(request, "day"), history), _shutdown.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                _model.Release();
            }

            if (threadId is not null)
            {
                await Write(writer => writer.AppendChatMessage(
                    threadId,
                    ChatRoles.Agent,
                    answer.Answer,
                    JsonSerializer.Serialize(answer.Citations, Json),
                    answer.ScopedCount,
                    Now())).ConfigureAwait(false);
            }

            return answer;
        }
        catch (Exception error) when (threadId is not null && error is not OperationCanceledException)
        {
            await Write(writer => writer.AppendChatMessage(threadId, ChatRoles.Error, Describe(error), "[]", 0, Now())).ConfigureAwait(false);
            throw;
        }
    }

    /// User and agent turns only, newest <paramref name="keep"/>, oldest first.
    internal static IReadOnlyList<AskTurn> RecentTurns(IReadOnlyList<ChatMessage> messages, int keep)
    {
        var turns = messages
            .Where(message => message.Role is ChatRoles.User or ChatRoles.Agent && !string.IsNullOrWhiteSpace(message.Text))
            .Select(message => new AskTurn(message.Role, message.Text.Trim()))
            .ToList();
        return turns.Skip(Math.Max(0, turns.Count - keep)).ToList();
    }

    /// Where the model runs. The model loaded on the old processor is let go;
    /// the next request loads it on the new one.
    private async Task<object?> AiBackendAsync(JsonElement request)
    {
        var backend = Req(request, "backend");
        if (backend is not ("gpu" or "cpu"))
        {
            throw new ArgumentException($"Unknown processor: {backend}");
        }

        Environment.SetEnvironmentVariable("GLINT_LITERT_BACKEND", backend);
        await _model.WaitAsync().ConfigureAwait(false);
        try
        {
            _worker?.Dispose();
            _worker = null;
        }
        finally
        {
            _model.Release();
        }

        return new { backend };
    }

    /// The shared model worker; null when the runtime is not installed.
    /// Called with the model lock held.
    private PersistentLiteRtWorker? Worker()
    {
        if (_worker is not null)
        {
            return _worker;
        }

        var resolution = LiteRtRuntimeLocator.Resolve(AppContext.BaseDirectory, _dataRoot);
        if (!resolution.IsReady)
        {
            return null;
        }

        _worker = new PersistentLiteRtWorker(
            resolution.PythonExecutable!,
            resolution.WorkerScript!,
            resolution.ModelPath!,
            maxNumTokens: 4096,
            idleUnloadAfter: TimeSpan.FromMinutes(5));
        return _worker;
    }

    private object Kick()
    {
        _summaryKick.Release();
        return new { queued = true };
    }

    /// <summary>
    /// Describes finished reading activities, one model call at a time, when
    /// the user is not using the PC (or not recording), and never while a game
    /// or video is in front. Woken by anything that ends a stretch, and every
    /// half minute otherwise.
    /// </summary>
    private async Task SummaryLoopAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await _summaryKick.WaitAsync(TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false);
                var described = 0;
                var reused = 0;
                while (!cancellation.IsCancellationRequested && MaySummarize())
                {
                    var result = await SummarizeOneAsync(cancellation).ConfigureAwait(false);
                    if (result is null)
                    {
                        break;
                    }

                    described += result.Summarized;
                    reused += result.Reused;
                    if (result.Failed > 0 || result.Summarized + result.Reused == 0 || result.Waiting == 0)
                    {
                        break;
                    }
                }

                if (described + reused > 0)
                {
                    await SendAsync(new
                    {
                        @event = "sessions-updated",
                        data = new
                        {
                            summarized = described,
                            reused,
                            aiBackend = _worker?.Backend,
                            gpuFailure = LiteRtBackend.GpuFailure
                        }
                    }).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"Glint summaries: {Describe(error)}");
            }
        }
    }

    private bool MaySummarize()
    {
        if (!_recording)
        {
            return true;
        }

        var now = Now();
        var visualInFront = _lastLookMode is ActivityMode.Play or ActivityMode.Watch
            && now - Interlocked.Read(ref _lastLookAt) < VisualFrontMilliseconds;
        return !visualInFront && UserInput.IdleMilliseconds() >= IdleBeforeSummariesMilliseconds;
    }

    private async Task<SummaryRunResult?> SummarizeOneAsync(CancellationToken cancellation)
    {
        await _model.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var worker = Worker();
            if (worker is null)
            {
                return null;
            }

            using var store = OpenStore(readOnly: true);
            var now = Now();
            return await SummaryQueue.RunAsync(
                    store,
                    summary =>
                    {
                        _write.Wait(cancellation);
                        try
                        {
                            _writer.SaveSummary(summary, now);
                        }
                        finally
                        {
                            _write.Release();
                        }
                    },
                    new LiteRtActivityNarrator(worker),
                    now,
                    maxNarrations: 1,
                    cancellation)
                .ConfigureAwait(false);
        }
        finally
        {
            _model.Release();
        }
    }

    // -----------------------------------------------------------------------
    // Plumbing
    // -----------------------------------------------------------------------

    private async Task<object?> Read(Func<Phase0Database, object> read)
    {
        await Task.Yield();
        using var store = OpenStore(readOnly: true);
        return read(store);
    }

    private async Task<object?> Write(Func<Phase0Database, object> write)
    {
        await _write.WaitAsync().ConfigureAwait(false);
        try
        {
            return write(_writer);
        }
        finally
        {
            _write.Release();
        }
    }

    private static Task<object?> Done(object? value) => Task.FromResult(value);

    private Phase0Database OpenStore(bool readOnly)
    {
        var keyStore = new DpapiKeyStore(Path.Combine(_dataRoot, "secrets", "dbkey.bin"));
        var bundledVec = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "vec0.dll");
        var vec = _options.GetValueOrDefault("sqlite-vec", File.Exists(bundledVec) ? bundledVec : string.Empty);
        return Phase0Database.Open(Path.Combine(_dataRoot, "memory.db"), keyStore, vec, readOnly);
    }

    private async Task SendAsync(object message)
    {
        var text = JsonSerializer.Serialize(message, Json);
        await _output.WaitAsync().ConfigureAwait(false);
        try
        {
            await _out!.WriteLineAsync(text).ConfigureAwait(false);
            await _out.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _output.Release();
        }
    }

    private static void ApplyTestMode(JsonElement request)
    {
        var mode = Str(request, "testMode");
        Environment.SetEnvironmentVariable(
            StutterTestMode.EnvironmentVariable,
            string.IsNullOrWhiteSpace(mode) || mode == "normal" ? null : mode);
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string Describe(Exception error) => $"{error.GetType().Name}: {error.Message}";

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Req(JsonElement element, string name) =>
        Str(element, name) ?? throw new ArgumentException($"Missing {name}.");

    private static long? Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    private static int? Int(JsonElement element, string name) =>
        Long(element, name) is { } value ? (int)Math.Clamp(value, int.MinValue, int.MaxValue) : null;

    private static int? HostProcessId(IReadOnlyDictionary<string, string> options) =>
        options.TryGetValue("host-pid", out var value) && int.TryParse(value, out var pid) && pid > 0 ? pid : null;

    public void Dispose()
    {
        _shutdown.Cancel();
        _worker?.Dispose();
        _writer.Dispose();
    }
}
