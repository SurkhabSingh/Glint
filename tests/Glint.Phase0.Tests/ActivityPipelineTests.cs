using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

/// The capture side and the build side together, on a real encrypted store.
public sealed class ActivityPipelineTests : IDisposable
{
    private const long T0 = 1_760_000_000_000;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "glint-phase0-tests",
        Guid.NewGuid().ToString("N"));

    private readonly Phase0Database _database;
    private readonly FakeInspector _inspector = new();
    private readonly FakeAutomation _automation = new();
    private readonly FakePages _pages = new();
    private readonly FakeFrames _frames = new();
    private readonly FakeMedia _media = new();
    private long _now = T0;
    private long _idle = 60_000;

    public ActivityPipelineTests()
    {
        _database = Phase0Database.Open(
            Path.Combine(_directory, "memory.db"),
            new DpapiKeyStore(Path.Combine(_directory, "key.bin")));
    }

    private ActivityScanCoordinator Coordinator() =>
        new(
            _inspector,
            _automation,
            _pages,
            new PrivacyGate(selfElevated: false),
            _frames,
            new DeterministicRedactor(),
            _database,
            () => _now,
            () => _idle,
            _media);

    [Fact]
    public async Task ALivePageIsStoredOnceThenOnlyWhatIsNew()
    {
        _inspector.Window = Window("zen", "(1) Lakers vs Celtics live - Scores - Zen Browser");
        _pages.Probe = new PageProbe(true, "scores.example.com", null, null, new object());
        _pages.Text = "Lakers vs Celtics\nLakers 87 - 92 Celtics\nQ3 04:12 remaining\nLive commentary\nBox score";
        var coordinator = Coordinator();

        var first = await coordinator.ScanAsync();
        Assert.Equal(CaptureChange.Keyframe, first.Change);

        // Nothing moved on screen: not even read.
        _now += 2_500;
        _pages.Reads = 0;
        var still = await coordinator.ScanAsync();
        Assert.Equal(ManualScanOutcomeKind.Unchanged, still.Kind);
        Assert.Equal(0, _pages.Reads);

        // The score and clock tick: the screen moved, the text is "the same".
        _now += 2_500;
        _frames.Shift++;
        _pages.Text = "Lakers vs Celtics\nLakers 89 - 94 Celtics\nQ3 03:40 remaining\nLive commentary\nBox score";
        var tick = await coordinator.ScanAsync();
        Assert.Equal(ManualScanOutcomeKind.Unchanged, tick.Kind);

        // The user posts a comment: only the new line is stored.
        _now += 2_500;
        _idle = 500;
        _frames.Shift++;
        _pages.Text += "\nYou: What a comeback!";
        var posted = await coordinator.ScanAsync();
        Assert.Equal(CaptureChange.Delta, posted.Change);

        var scans = _database.GetRecentManualScans(50);
        Assert.Equal(2, scans.Count);
        var delta = scans[0];
        Assert.Equal("You: What a comeback!", delta.RedactedInputText);
        Assert.True(delta.UserCaused);
        Assert.Equal(T0 + 5_000, scans[1].LastSeenMilliseconds);
    }

    [Fact]
    public async Task ATradingSiteIsPrivateByDefault()
    {
        _inspector.Window = Window("zen", "Trade AAPL - Broker - Zen Browser");
        _pages.Probe = new PageProbe(true, "broker.example.com", null, null, new object());
        _pages.Text = "AAPL 187.42\nOrder filled · Buy 10 AAPL";

        var outcome = await Coordinator().ScanAsync();

        Assert.Equal(ActivityMode.Private, outcome.Mode);
        var scan = Assert.Single(_database.GetRecentManualScans(10));
        Assert.Null(scan.RedactedInputText);
        Assert.Equal("confirmed", scan.EventKind);
    }

    [Fact]
    public async Task AGameIsNeverReadAndCostsOneLookAMinute()
    {
        _inspector.Window = Window("osu!", "osu! - Main Menu", fullscreen: true);
        var coordinator = Coordinator();

        var first = await coordinator.ScanAsync();
        _now += 10_000;
        _inspector.Window = Window("osu!", "osu! - Camellia [Extra]", fullscreen: true);
        var menu = await coordinator.ScanAsync();
        _now += ActivityScanCoordinator.VisualLookEveryMilliseconds;
        _frames.Shift++;
        var later = await coordinator.ScanAsync();

        Assert.Equal(ActivityMode.Play, first.Mode);
        Assert.Equal(ManualScanOutcomeKind.Unchanged, menu.Kind);
        Assert.True(later.ScreenMoving);
        Assert.False(_automation.TextWasRead);
        Assert.Equal(0, _pages.Probes);
        Assert.Equal(0, _frames.Recognitions);
        Assert.Equal(2, _frames.Captures);
        var scan = Assert.Single(_database.GetRecentManualScans(10));
        Assert.Equal(T0 + 10_000 + ActivityScanCoordinator.VisualLookEveryMilliseconds, scan.LastSeenMilliseconds);
    }

    [Fact]
    public async Task ABlockedAppIsTimeOnly()
    {
        _inspector.Window = Window("KeePass", "Database.kdbx - KeePass");

        var outcome = await Coordinator().ScanAsync();

        Assert.Equal(ManualScanOutcomeKind.Suppressed, outcome.Kind);
        var scan = Assert.Single(_database.GetRecentManualScans(10));
        Assert.Equal(string.Empty, scan.WindowTitle);
        Assert.Equal(ActivityMode.Private, scan.Mode);
        Assert.False(_automation.TextWasRead);
        Assert.Equal(0, _frames.Captures);
    }

    [Fact]
    public async Task APrivateSiteKeepsAConfirmationButNoText()
    {
        _inspector.Window = Window("zen", "Pay bill - City Power - Zen Browser");
        _pages.Probe = new PageProbe(true, "billpay.citypower.example", null, null, new object());
        _pages.Text = "Account 4111 1111 1111 1111\nPayment successful. Thank you for your payment.";

        var outcome = await Coordinator().ScanAsync();

        Assert.Equal(ActivityMode.Private, outcome.Mode);
        var scan = Assert.Single(_database.GetRecentManualScans(10));
        Assert.Null(scan.RedactedInputText);
        Assert.Equal(string.Empty, scan.WindowTitle);
        Assert.Equal("confirmed", scan.EventKind);
    }

    [Fact]
    public async Task TitlesAreRedactedBeforeTheyBecomeIdentity()
    {
        _inspector.Window = Window("Code", "notes-for-bob@example.com.md - glint - Visual Studio Code");
        _automation.Text = "Some notes about the project roadmap and next steps";

        await Coordinator().ScanAsync();

        var scan = Assert.Single(_database.GetRecentManualScans(10));
        Assert.DoesNotContain("bob@example.com", scan.WindowTitle, StringComparison.Ordinal);
        Assert.DoesNotContain("bob@example.com", scan.PageKey!, StringComparison.Ordinal);
        Assert.DoesNotContain("bob@example.com", scan.Phase ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PhotoshopSavesAndExportsBecomeEvents()
    {
        var coordinator = Coordinator();
        _inspector.Window = Window("Photoshop", "poster.psd @ 50% (RGB/8) *");
        await coordinator.ScanAsync();
        _now += 60_000;
        _inspector.Window = Window("Photoshop", "poster.psd @ 66.7% (RGB/8)");
        var saved = await coordinator.ScanAsync();
        _now += 60_000;
        _inspector.Window = Window("Photoshop", "poster.psd @ 66.7% (RGB/8)") with { DialogTitle = "Export As" };
        var exported = await coordinator.ScanAsync();

        Assert.Contains("saved", saved.Detail, StringComparison.Ordinal);
        Assert.Contains("exported", exported.Detail, StringComparison.Ordinal);
        Assert.False(_automation.TextWasRead);
        Assert.Equal(0, _frames.Captures);
    }

    [Fact]
    public async Task SessionsBecomeSeparateCheckedActivities()
    {
        var coordinator = Coordinator();
        // Email for six minutes, in two looks with a reply typed.
        _inspector.Window = Window("zen", "Re: Venue deposit - Gmail - Zen Browser");
        _pages.Probe = new PageProbe(true, "mail.google.com", null, null, new object());
        _pages.Text = "Priya Sharma: Venue deposit\nHi, can you send the receipt for the deposit by Friday? Thanks, Priya";
        await coordinator.ScanAsync();
        _now += 180_000;
        _idle = 100;
        _frames.Shift++;
        _pages.Text += "\nYou: Sure, I will send it tonight.";
        await coordinator.ScanAsync();
        // Then a game for twenty minutes.
        _now += 180_000;
        _idle = 60_000;
        _inspector.Window = Window("osu!", "osu!", fullscreen: true);
        await coordinator.ScanAsync();
        _now += 1_200_000;
        await coordinator.ScanAsync();

        var sessions = new SessionBuilder(_database, new UnusedSummarizer(), summarizeSessions: false);
        await sessions.RunAsync(_now + Sessionizer.QuietTailMilliseconds);
        var narrator = new FakeNarrator(new Narration(
            "Replied to Priya about deposit",
            "Priya Sharma asked for the deposit receipt by Friday. The user said they would send it tonight. Arjun approved the budget.",
            "Send Priya the deposit receipt by Friday"));
        var result = await new ActivityBuilder(_database, _database, narrator).RunAsync();

        Assert.Equal(2, result.Activities);
        Assert.Equal(1, result.Narrated);
        var activities = _database.GetRecentActivities();
        var email = Assert.Single(activities, activity => activity.Category == ActivityCategory.Email);
        Assert.Equal(SummaryCheck.Partial, email.Check);
        Assert.DoesNotContain("Arjun", email.Summary, StringComparison.Ordinal);
        Assert.Equal(ActivityTaskStatus.Open, email.TaskStatus);
        Assert.Equal("Gmail", email.Subject);
        // The model saw only the email, never the game.
        Assert.DoesNotContain("osu", narrator.LastText, StringComparison.OrdinalIgnoreCase);
        var game = Assert.Single(activities, activity => activity.Mode == ActivityMode.Play);
        Assert.Equal(SummaryCheck.Rule, game.Check);
        Assert.Equal("Played osu!", game.Label);

        var session = Assert.Single(_database.GetRecentSessions(10));
        Assert.Equal(SessionOutcome.Open, session.Outcome);
        Assert.Contains("Played osu!", session.Summary, StringComparison.Ordinal);

        // The user's verdict survives a rebuild.
        _database.SetActivityTaskStatus(email.Id, ActivityTaskStatus.Done);
        _database.SetAppModeByUser(ActivityIdentityResolver.AppKeyOf("osu!"), ActivityMode.Play, ActivityCategory.Game, _now);
        await new ActivityBuilder(_database, _database, narrator).RunAsync();
        var rebuilt = Assert.Single(_database.GetRecentActivities(), activity => activity.Category == ActivityCategory.Email);
        Assert.Equal(ActivityTaskStatus.Done, rebuilt.TaskStatus);
        Assert.Equal(SummaryCheck.Partial, rebuilt.Check);
    }

    [Fact]
    public async Task AnEpisodeOnAnUnknownSiteIsOneWatchRecord()
    {
        // The case that broke: a full-screen episode on a site no catalog
        // lists. Windows reports it playing under an opaque id; the title
        // is what ties it to the window.
        const string Title = "Cyberpunk: Edgerunners Episode 2 English Sub/Dub - AnimeX";
        _inspector.Window = Window("zen", Title + " — Zen Browser", fullscreen: true);
        _pages.Probe = new PageProbe(true, "animex.one", null, null, new object());
        _pages.Text = "Pause\nExit fullscreen\n3 seconds of 22 minutes, 27 seconds\nI'm here to install.";
        _media.Playing.Add(new MediaPlayback("F0DC299D809B9700", Title, string.Empty, true));
        _frames.Video = true;
        _idle = 300;
        var coordinator = Coordinator();

        var outcomes = new List<ManualScanOutcome>();
        for (var look = 0; look < 12; look++)
        {
            outcomes.Add(await coordinator.ScanAsync());
            _now += 30_000;
            _pages.Text = $"Pause\nExit fullscreen\n{look * 30} seconds of 22 minutes, 27 seconds\nSubtitle line number {look}";
        }

        Assert.All(outcomes, outcome => Assert.Equal(ActivityMode.Watch, outcome.Mode));
        Assert.Equal(0, _pages.Reads);
        Assert.Equal(0, _frames.Recognitions);
        var scan = Assert.Single(_database.GetRecentManualScans(50));
        Assert.Equal(ActivityMode.Watch, scan.Mode);
        Assert.Equal(ActivityCategory.Video, scan.Category);
        Assert.Null(scan.RedactedInputText);
        Assert.Equal(T0 + (11 * 30_000), scan.LastSeenMilliseconds);
        Assert.Equal("Cyberpunk: Edgerunners Episode 2 English Sub/Dub", scan.Subject);
        // The site is remembered, so the next visit is Watch from the start.
        Assert.Equal(ActivityMode.Watch, _database.GetAppProfile("site:animex.one")!.Mode);
    }

    [Fact]
    public async Task AFullScreenVideoWithoutMediaInfoIsWatchedOnceItKeepsMoving()
    {
        _inspector.Window = Window("zen", "Some Player — Zen Browser", fullscreen: true);
        _pages.Probe = new PageProbe(true, "player.example.org", null, null, new object());
        _pages.Text = "Episode 2\nSubtitle one here";
        _frames.Video = true;
        var coordinator = Coordinator();

        var modes = new List<ActivityMode?>();
        for (var look = 0; look < 6; look++)
        {
            modes.Add((await coordinator.ScanAsync()).Mode);
            _now += 60_000;
            _pages.Text = $"Episode 2\nSubtitle line {look} with new words";
        }

        // Read until the motion has lasted a couple of looks, then Watch.
        Assert.Equal(ActivityMode.Watch, modes[^1]);
        Assert.Equal(ActivityMode.Watch, modes[^2]);
        var stored = _database.GetRecentManualScans(50);
        Assert.True(stored.Count <= 3, $"stored {stored.Count} looks");
    }

    [Fact]
    public async Task MusicInABackgroundTabDoesNotMakeTheInboxAVideo()
    {
        _inspector.Window = Window("zen", "Inbox - Gmail — Zen Browser");
        _pages.Probe = new PageProbe(true, "mail.google.com", null, null, new object());
        _pages.Text = "Inbox\nPriya Sharma: Venue deposit\nCan you send the receipt by Friday?";
        _media.Playing.Add(new MediaPlayback("F0DC299D809B9700", "Lo-fi beats to study to", string.Empty, true));

        var outcome = await Coordinator().ScanAsync();

        Assert.Equal(ActivityMode.Read, outcome.Mode);
        Assert.Equal(CaptureChange.Keyframe, outcome.Change);
    }

    [Fact]
    public async Task MostlyWatchedPagesBecomeWatchActivities()
    {
        // A couple of reads while the page loaded, then twenty minutes watched.
        const string Title = "Episode 3 - AnimeX";
        _inspector.Window = Window("zen", Title + " — Zen Browser");
        _pages.Probe = new PageProbe(true, "animex.one", null, null, new object());
        _pages.Text = "Episode 3\nComments\nRelated episodes and recommendations";
        var coordinator = Coordinator();
        await coordinator.ScanAsync();
        _now += 5_000;
        _media.Playing.Add(new MediaPlayback("x", Title, string.Empty, true));
        await coordinator.ScanAsync();
        _now += 1_200_000;
        await coordinator.ScanAsync();

        await new SessionBuilder(_database, new UnusedSummarizer(), summarizeSessions: false)
            .RunAsync(_now + Sessionizer.QuietTailMilliseconds);
        await new ActivityBuilder(_database, _database, null).RunAsync();

        var activity = Assert.Single(_database.GetRecentActivities());
        Assert.Equal(ActivityMode.Watch, activity.Mode);
        Assert.Equal(ActivityCategory.Video, activity.Category);
        Assert.Equal(SummaryCheck.Rule, activity.Check);
        Assert.StartsWith("Watched Episode 3", activity.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PausingAndResumingOnTheSameEpisodeNeverExtendsASealedRecord()
    {
        // The reported case: watch, pause at 16:51, resume at 16:52 on the
        // same episode, keep watching past 17:00, stop at 17:11.
        const string Title = "Cyberpunk: Edgerunners Episode 3 English Sub/Dub - AnimeX";
        _inspector.Window = Window("zen", Title + " — Zen Browser", fullscreen: true);
        _pages.Probe = new PageProbe(true, "animex.one", null, null, new object());
        _media.Playing.Add(new MediaPlayback("F0DC299D809B9700", Title, string.Empty, true));
        var coordinator = Coordinator();

        await coordinator.ScanAsync();                       // 16:51:03
        _database.RecordMarker("run.stopped", _now + 3_000); // paused
        await new SessionBuilder(_database, new UnusedSummarizer(), summarizeSessions: false)
            .RunAsync(_now + 3_000 + Sessionizer.QuietTailMilliseconds);
        await new ActivityBuilder(_database, _database, null).RunAsync();

        _now += 71_000;                                      // resumed 16:52:14
        _database.RecordMarker("run.started", _now);
        for (var minute = 0; minute < 19; minute++)
        {
            await coordinator.ScanAsync();
            _now += 60_000;
        }

        var scans = _database.GetRecentManualScans(50);
        Assert.Equal(2, scans.Count);
        // The sealed record kept its own end; the new one carries the evening.
        Assert.InRange(scans[1].LastSeenMilliseconds ?? 0, T0, T0 + 5_000);
        Assert.True(scans[0].LastSeenMilliseconds - scans[0].CapturedAtMilliseconds >= 17 * 60_000);

        _database.RecordMarker("run.stopped", _now);
        await new SessionBuilder(_database, new UnusedSummarizer(), summarizeSessions: false)
            .RunAsync(_now + Sessionizer.QuietTailMilliseconds);
        await new ActivityBuilder(_database, _database, null).RunAsync();

        var later = _database.GetRecentActivities().OrderBy(activity => activity.StartedAtMilliseconds).Last();
        Assert.Equal(ActivityMode.Watch, later.Mode);
        Assert.Equal(ActivityCategory.Video, later.Category);
        Assert.True(later.ActiveMilliseconds >= 18 * 60_000, $"{later.ActiveMilliseconds} ms");
        var session = _database.GetRecentSessions(5).First();
        Assert.Equal(later.EndedAtMilliseconds, session.EndedAtMilliseconds);
    }

    [Fact]
    public async Task ARecordIsNotStretchedAcrossALongGap()
    {
        _inspector.Window = Window("osu!", "osu!", fullscreen: true);
        var coordinator = Coordinator();

        await coordinator.ScanAsync();
        _now += 600_000; // the app was closed, or the machine slept
        await coordinator.ScanAsync();

        var scans = _database.GetRecentManualScans(10);
        Assert.Equal(2, scans.Count);
        Assert.Equal(T0, scans[1].LastSeenMilliseconds);
    }

    [Fact]
    public async Task AGameRunningAsAdministratorIsPlayWithItsName()
    {
        _inspector.Window = Window("P4G", "P4G") with
        {
            IsElevated = true,
            ExecutablePath = @"D:\SteamLibrary\steamapps\common\Persona 4 Golden\P4G.exe"
        };
        var coordinator = Coordinator();

        var first = await coordinator.ScanAsync();
        _now += 30_000;
        var again = await coordinator.ScanAsync();

        Assert.Equal(ActivityMode.Play, first.Mode);
        Assert.Equal(ManualScanOutcomeKind.Unchanged, again.Kind);
        // Windows keeps an admin app's screen from a Glint that isn't: nothing is tried.
        Assert.Equal(0, _frames.Captures);
        Assert.Equal(0, _pages.Probes);
        Assert.False(_automation.TextWasRead);
        var scan = Assert.Single(_database.GetRecentManualScans(10));
        Assert.Equal("P4G", scan.WindowTitle);
        Assert.Equal(T0 + 30_000, scan.LastSeenMilliseconds);
    }

    [Fact]
    public async Task TimeKeptPrivateBeforeAGameWasRecognizedBecomesPlay()
    {
        // Recorded as private time (the game hid its window from capture).
        _inspector.Window = Window("P4G", "P4G") with { IsDisplayProtected = true };
        var coordinator = Coordinator();
        await coordinator.ScanAsync();

        // Later the same game is seen with its Steam folder.
        _now += 60_000;
        _inspector.Window = Window("P4G", "P4G") with
        {
            ExecutablePath = @"D:\SteamLibrary\steamapps\common\Persona 4 Golden\P4G.exe"
        };
        await coordinator.ScanAsync();
        _now += 600_000;
        await coordinator.ScanAsync();

        await new SessionBuilder(_database, new UnusedSummarizer(), summarizeSessions: false)
            .RunAsync(_now + Sessionizer.QuietTailMilliseconds);
        await new ActivityBuilder(_database, _database, null).RunAsync();

        var game = Assert.Single(_database.GetRecentActivities());
        Assert.Equal(ActivityMode.Play, game.Mode);
        Assert.Equal(ActivityCategory.Game, game.Category);
    }

    [Fact]
    public async Task AnAndroidEmulatorIsAGameAndNeverRead()
    {
        _inspector.Window = Window("MuMuNxDevice", "Pokémon TCG Pocket");
        _pages.Text = "Open booster pack\nGenetic Apex";

        var outcome = await Coordinator().ScanAsync();

        Assert.Equal(ActivityMode.Play, outcome.Mode);
        Assert.Equal(0, _pages.Reads);
        Assert.False(_automation.TextWasRead);
        Assert.Equal(ActivityMode.Play, ActivityCatalog.ForProcess("MuMuNxDevice")?.Mode);
        Assert.True(ActivityCatalog.IsEmulator("duckstation-qt-x64-ReleaseLTCG"));
        Assert.False(ActivityCatalog.IsEmulator("MuMuVMMSVC"));
    }

    [Fact]
    public async Task OldCapturesFromAGameFolderAreRecognizedAsPlay()
    {
        // Stored by the original coordinator: no facets, only process, title
        // and the raw event's executable path.
        foreach (var (offset, title) in new[] { (0L, "Stellar Blade"), (600_000L, "Stellar Blade - Map") })
        {
            var hash = Guid.NewGuid().ToString("N");
            _database.SaveManualScan(
                new RawCaptureEvent(
                    Guid.NewGuid().ToString("N"),
                    T0 + offset,
                    "SB-Win64-Shipping",
                    @"D:\SteamLibrary\steamapps\common\Stellar Blade\SB-Win64-Shipping.exe",
                    title,
                    hash,
                    "HP 120 Save Load",
                    0),
                new ManualScanRecord(
                    Guid.NewGuid().ToString("N"),
                    T0 + offset,
                    "SB-Win64-Shipping",
                    title,
                    title,
                    null,
                    ManualScanStatus.Completed,
                    null,
                    hash,
                    string.Empty,
                    0,
                    16,
                    0,
                    0,
                    0,
                    0));
        }

        await new SessionBuilder(_database, new UnusedSummarizer(), summarizeSessions: false)
            .RunAsync(T0 + 3_600_000);
        await new ActivityBuilder(_database, _database, null).RunAsync();

        var game = Assert.Single(_database.GetRecentActivities());
        Assert.Equal(ActivityMode.Play, game.Mode);
        Assert.Equal(600_000, game.ActiveMilliseconds);
    }

    [Fact]
    public void AUserModeBeatsTheCatalogAndLearning()
    {
        var resolver = new ActivityIdentityResolver(_database);
        _database.SetAppModeByUser(ActivityIdentityResolver.AppKeyOf("zen"), ActivityMode.Watch, null, T0);

        var profile = resolver.ResolveApp(Window("zen", "Anything"), T0);
        var learned = resolver.Learn(profile, 0, true, T0);

        Assert.Equal(ActivityMode.Watch, profile.Mode);
        Assert.Equal(ModeSource.User, learned.Source);
        Assert.Equal(ActivityMode.Watch, learned.Mode);
    }

    [Fact]
    public void AnUnknownSparseFullScreenAppIsLearnedAsAGame()
    {
        var resolver = new ActivityIdentityResolver(_database);
        var window = Window("MysteryGame", "Mystery", fullscreen: true);
        var profile = resolver.ResolveApp(window, T0);
        Assert.Equal(ModeSource.Provisional, profile.Source);

        for (var look = 0; look < ActivityIdentityResolver.SamplesToDecide; look++)
        {
            profile = resolver.Learn(profile, 40, true, T0);
        }

        Assert.Equal(ActivityMode.Play, profile.Mode);
        Assert.Equal(ModeSource.Learned, profile.Source);
        Assert.Equal(ActivityMode.Play, resolver.ResolveApp(window, T0).Mode);
    }

    public void Dispose()
    {
        _database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static ForegroundWindowInfo Window(string process, string title, bool fullscreen = false) =>
        new(
            42,
            4242,
            process,
            null,
            title,
            new WindowBounds(0, 0, 1920, 1080),
            false,
            false,
            true,
            false,
            false,
            true,
            false,
            IsFullscreen: fullscreen);

    private sealed class FakeInspector : IForegroundWindowInspector
    {
        public ForegroundWindowInfo? Window { get; set; }

        public ForegroundWindowInfo? Inspect() => Window;
    }

    private sealed class FakeAutomation : IUiAutomationService
    {
        public string Text { get; set; } = string.Empty;

        public bool TextWasRead { get; private set; }

        public AutomationSecurityProbe ProbeSecurity(ForegroundWindowInfo window) =>
            new(true, false, true, "fake");

        public AutomationTextResult ExtractText(ForegroundWindowInfo window)
        {
            TextWasRead = true;
            return new(Text, 1, false, TimeSpan.Zero);
        }
    }

    private sealed class FakePages : IPageReader
    {
        public PageProbe Probe { get; set; } = PageProbe.None;

        public string Text { get; set; } = string.Empty;

        public int Probes { get; private set; }

        public int Reads { get; set; }

        public PageProbe ProbePage(ForegroundWindowInfo window)
        {
            Probes++;
            return Probe;
        }

        public AutomationTextResult ExtractPageText(ForegroundWindowInfo window, PageProbe probe)
        {
            Reads++;
            return new(Text, 1, false, TimeSpan.Zero);
        }
    }

    private sealed class FakeFrames : IFrameCaptureService
    {
        public int Shift { get; set; }

        public int Captures { get; private set; }

        public int Recognitions { get; private set; }

        /// When set, every cell changes between looks, like a playing video.
        public bool Video { get; set; }

        public Task<IScreenFrame> CaptureFrameAsync(ForegroundWindowInfo window, CancellationToken cancellationToken = default)
        {
            Captures++;
            var shift = Video ? Captures : Shift;
            var signature = ChangeDetectionTests.Frame(cell => (byte)((cell * 7 + (shift * 37)) % 251));
            return Task.FromResult<IScreenFrame>(new Frame(signature, () => Recognitions++));
        }

        private sealed class Frame(FrameSignature signature, Action recognized) : IScreenFrame
        {
            public FrameSignature Signature => signature;

            public TimeSpan CaptureElapsed => TimeSpan.Zero;

            public Task<OcrCaptureResult> RecognizeAsync(WindowBounds? cropScreenBounds, bool[]? ignoreCells = null, CancellationToken cancellationToken = default)
            {
                recognized();
                return Task.FromResult(new OcrCaptureResult(string.Empty, 100, 100, TimeSpan.Zero, TimeSpan.Zero));
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeMedia : IMediaSessionReader
    {
        public List<MediaPlayback> Playing { get; } = [];

        public IReadOnlyList<MediaPlayback> Read() => Playing;
    }

    private sealed class FakeNarrator(Narration narration) : IActivityNarrator
    {
        public string LastText { get; private set; } = string.Empty;

        public Task<Narration> NarrateAsync(NarrationRequest request, CancellationToken cancellationToken = default)
        {
            LastText = request.Text;
            return Task.FromResult(narration);
        }
    }

    private sealed class UnusedSummarizer : IActivitySummarizer
    {
        public string ModelId => "unused";

        public Task<ActivitySummary> SummarizeAsync(
            DateTimeOffset capturedAt,
            string processName,
            string windowTitle,
            string redactedText,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Sessions are described per activity.");
    }
}
