using System.Security.Cryptography;
using System.Text;

namespace Glint.Phase0.Core;

/// <summary>
/// One look at the screen under the activity model. Works out which activity
/// the look belongs to first, then treats it the way that kind of app needs:
/// text for reading, title and saves for making, time and motion for games
/// and video, time alone for private apps.
/// </summary>
/// <remarks>
/// Privacy order is unchanged from the original coordinator: the gate runs
/// before any text or pixel is read, secret frames are dropped whole, and
/// text is redacted before anything is compared or stored. What changed is
/// what gets stored. A look that adds nothing extends the previous record
/// instead of saving a copy, and a look that adds a few lines saves only
/// those lines.
/// </remarks>
public sealed class ActivityScanCoordinator
{
    /// Games and video need one look a minute: the app and the time say
    /// almost everything, and the screen is only checked for movement.
    internal const long VisualLookEveryMilliseconds = 60_000;

    /// Gate decisions about the app itself, where the time spent there is
    /// still worth recording with no title and no content.
    private static readonly HashSet<SuppressReason> RecordAsPrivate =
    [
        SuppressReason.BlocklistedApplication,
        SuppressReason.PrivateBrowsing,
        SuppressReason.SensitiveWindowTitle,
        SuppressReason.DisplayProtected,
        SuppressReason.PasswordField
    ];

    private readonly IForegroundWindowInspector _windowInspector;
    private readonly IUiAutomationService _automation;
    private readonly IPageReader _pages;
    private readonly PrivacyGate _privacyGate;
    private readonly IFrameCaptureService _frames;
    private readonly DeterministicRedactor _redactor;
    private readonly IActivityStore _store;
    private readonly ActivityIdentityResolver _resolver;
    private readonly Func<long> _clock;
    private readonly Func<long> _inputIdle;
    private readonly IMediaSessionReader _media;

    public ActivityScanCoordinator(
        IForegroundWindowInspector windowInspector,
        IUiAutomationService automation,
        IPageReader pages,
        PrivacyGate privacyGate,
        IFrameCaptureService frames,
        DeterministicRedactor redactor,
        IActivityStore store,
        Func<long>? clock = null,
        Func<long>? inputIdleMilliseconds = null,
        IMediaSessionReader? media = null)
    {
        _media = media ?? new WindowsMediaSessionReader();
        _windowInspector = windowInspector;
        _automation = automation;
        _pages = pages;
        _privacyGate = privacyGate;
        _frames = frames;
        _redactor = redactor;
        _store = store;
        _resolver = new ActivityIdentityResolver(store);
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _inputIdle = inputIdleMilliseconds ?? UserInput.IdleMilliseconds;
    }

    public async Task<ManualScanOutcome> ScanAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var now = _clock();
            var window = _windowInspector.Inspect();
            if (window is null)
            {
                return new(
                    ManualScanOutcomeKind.Suppressed,
                    "Windows did not report a foreground window.",
                    SuppressReason: SuppressReason.NoForegroundWindow);
            }

            var security = _automation.ProbeSecurity(window);
            var decision = _privacyGate.Evaluate(window, security);
            if (!decision.Allowed && decision.Reason == SuppressReason.ElevatedProcess && !window.IsSelf)
            {
                return LookElevated(window, now);
            }

            if (!decision.Allowed)
            {
                if (decision.Reason is { } reason && RecordAsPrivate.Contains(reason) && !window.IsSelf)
                {
                    RecordPrivatePresence(window, now, eventKind: null);
                }

                return new(
                    ManualScanOutcomeKind.Suppressed,
                    decision.Detail,
                    SuppressReason: decision.Reason,
                    Mode: ActivityMode.Private);
            }

            // From here on the title is redacted: the activity key, the
            // subject and every stored field are built from it, so nothing
            // downstream can store a title the redactor would have masked.
            window = window with
            {
                Title = _redactor.Redact(window.Title).Text,
                DialogTitle = window.DialogTitle is null ? null : _redactor.Redact(window.DialogTitle).Text
            };
            var app = _resolver.ResolveApp(window, now);
            // Games are never walked with UI Automation: nothing useful is
            // there and some engines answer slowly.
            var probe = app.Mode == ActivityMode.Play ? PageProbe.None : _pages.ProbePage(window);
            var siteProfile = _resolver.ResolveSite(probe.Site, now);
            var identity = _resolver.Identify(window, app, siteProfile, probe.Site);

            // Something playing in this very window is watching, whatever the
            // site: the cheapest and strongest signal, checked before any
            // frame or text is read.
            if (identity.Mode == ActivityMode.Read
                && (probe.IsBrowser || app.Source is ModeSource.Provisional or ModeSource.Learned)
                && WatchDetector.MediaMatches(window, identity.Subject, probe.IsBrowser, _media.Read()))
            {
                identity = AsWatching(window, app, probe, identity, now, learnSite: true);
            }

            return identity.Mode switch
            {
                ActivityMode.Play or ActivityMode.Watch =>
                    await LookVisualAsync(window, identity, now, cancellationToken).ConfigureAwait(false),
                ActivityMode.Make => LookMake(window, identity, now),
                ActivityMode.Private => LookPrivate(window, identity, probe, now),
                _ => await LookReadAsync(window, app, identity, probe, now, cancellationToken).ConfigureAwait(false)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return new(
                ManualScanOutcomeKind.Failed,
                $"{error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// An app running as administrator while Glint is not. Windows keeps its
    /// screen and text out of reach, but its name, file and title are enough
    /// to know what it is: a game is a game, a file being edited is that
    /// file. Recorded as presence with the title, never with content.
    /// </summary>
    private ManualScanOutcome LookElevated(ForegroundWindowInfo window, long now)
    {
        window = window with
        {
            Title = _redactor.Redact(window.Title).Text,
            DialogTitle = window.DialogTitle is null ? null : _redactor.Redact(window.DialogTitle).Text
        };
        var app = _resolver.ResolveApp(window, now);
        var identity = _resolver.Identify(window, app, null, null);
        if (identity.Mode == ActivityMode.Private)
        {
            RecordPrivatePresence(window, now, eventKind: null, identity);
            return new(
                ManualScanOutcomeKind.Completed,
                "Private: time only.",
                Mode: ActivityMode.Private,
                Change: CaptureChange.Presence);
        }

        if (identity.Mode == ActivityMode.Make)
        {
            // Saves and exports show in the title and dialogs, which are readable.
            return LookMake(window, identity, now);
        }

        var latest = _store.GetLatestFacet();
        var continuing = Continues(latest, identity.Key, now);
        if (continuing)
        {
            _store.TouchScan(latest!.Id, now);
        }
        else
        {
            SavePresence(window, identity, now, eventKind: null);
        }

        return new(
            continuing ? ManualScanOutcomeKind.Unchanged : ManualScanOutcomeKind.Completed,
            $"{identity.Mode}: {identity.Subject} (runs as administrator: name and title only).",
            Mode: identity.Mode,
            Change: CaptureChange.Presence);
    }

    /// <summary>
    /// The identity of this look as watching: same app and page, Watch mode,
    /// and media as its category. A site outside the catalog that keeps
    /// playing is remembered as a place for watching.
    /// </summary>
    private PageIdentity AsWatching(
        ForegroundWindowInfo window,
        AppProfile app,
        PageProbe probe,
        PageIdentity identity,
        long now,
        bool learnSite)
    {
        var learned = learnSite ? _resolver.LearnWatchSite(probe.Site, now) : null;
        var watching = learned is { Mode: ActivityMode.Watch }
            ? learned
            : new AppProfile(
                "watching",
                identity.AppName,
                ActivityMode.Watch,
                identity.Category == ActivityCategory.Music ? ActivityCategory.Music : ActivityCategory.Video,
                ModeSource.Learned);
        return _resolver.Identify(window, app, watching, probe.Site);
    }

    /// <summary>
    /// Games and video: the app or title and the time, with a motion check
    /// once a minute so a cutscene or a lecture is not mistaken for absence.
    /// No text is read, no OCR runs, and nothing slows the game down.
    /// </summary>
    private async Task<ManualScanOutcome> LookVisualAsync(
        ForegroundWindowInfo window,
        PageIdentity identity,
        long now,
        CancellationToken cancellationToken)
    {
        var latest = _store.GetLatestFacet();
        var continuing = Continues(latest, identity.Key, now);
        var state = _store.GetPageState(identity.Key);
        var moving = state?.Moving ?? true;
        if (!continuing || state is null || now - state.SignatureAtMilliseconds >= VisualLookEveryMilliseconds)
        {
            try
            {
                using var frame = await _frames.CaptureFrameAsync(window, cancellationToken).ConfigureAwait(false);
                var diff = FrameSignature.Compare(state?.Signature, frame.Signature, state?.LiveCounts);
                moving = diff.Moved;
                _store.UpsertPageState(new PageState(
                    identity.Key,
                    frame.Signature,
                    diff.LiveCounts,
                    now,
                    state?.KeyframeAtMilliseconds ?? 0,
                    now,
                    moving));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Exclusive full-screen games can refuse capture. The app and
                // the time are still known; that is all Play mode stores.
            }
        }

        // Exclusive full screen can hand back black frames; Windows still
        // knows the video is playing, and that is the user watching.
        if (!moving
            && identity.Mode == ActivityMode.Watch
            && WatchDetector.MediaMatches(window, identity.Subject, ActivityCatalog.IsBrowser(window.ProcessName), _media.Read()))
        {
            moving = true;
        }

        if (continuing)
        {
            _store.TouchScan(latest!.Id, now);
        }
        else
        {
            SavePresence(window, identity, now, eventKind: null);
        }

        return new(
            continuing ? ManualScanOutcomeKind.Unchanged : ManualScanOutcomeKind.Completed,
            $"{identity.Mode}: {identity.Subject}.",
            Mode: identity.Mode,
            ScreenMoving: moving,
            Change: CaptureChange.Presence);
    }

    /// <summary>
    /// Creative work: the file, its saves and the dialogs that export or
    /// print it. Brush strokes and tool panels are content and never split
    /// the activity.
    /// </summary>
    private ManualScanOutcome LookMake(ForegroundWindowInfo window, PageIdentity identity, long now)
    {
        var latest = _store.GetLatestFacet();
        var dialogEvent = ActivityEvidence.DialogEventOf(window.DialogTitle);
        string? eventKind = dialogEvent;
        if (eventKind is null
            && latest?.PageKey == identity.Key
            && latest.Unsaved
            && !identity.Unsaved)
        {
            eventKind = "saved";
        }

        var continuing = Continues(latest, identity.Key, now)
            && latest!.Unsaved == identity.Unsaved
            && (eventKind is null || latest.EventKind == eventKind);
        if (continuing)
        {
            _store.TouchScan(latest!.Id, now);
        }
        else
        {
            SavePresence(window, identity, now, eventKind);
        }

        return new(
            continuing ? ManualScanOutcomeKind.Unchanged : ManualScanOutcomeKind.Completed,
            eventKind is null ? $"Make: {identity.Subject}." : $"Make: {identity.Subject} ({eventKind}).",
            Mode: ActivityMode.Make,
            Change: CaptureChange.Presence);
    }

    /// <summary>
    /// Bills, banking, trading: the site and the time. The page is read in
    /// memory only to spot a confirmation, which is kept as an event; no
    /// text and no title are stored.
    /// </summary>
    private ManualScanOutcome LookPrivate(
        ForegroundWindowInfo window,
        PageIdentity identity,
        PageProbe probe,
        long now)
    {
        string? eventKind = null;
        try
        {
            var text = _pages.ExtractPageText(window, probe).Text;
            if (ActivityEvidence.FindConfirmation(text) is not null)
            {
                eventKind = "confirmed";
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Without the check the time is still recorded.
        }

        var latest = _store.GetLatestFacet();
        var continuing = Continues(latest, identity.Key, now)
            && (eventKind is null || latest!.EventKind == eventKind);
        if (continuing)
        {
            _store.TouchScan(latest!.Id, now);
        }
        else
        {
            RecordPrivatePresence(window, now, eventKind, identity);
        }

        return new(
            continuing ? ManualScanOutcomeKind.Unchanged : ManualScanOutcomeKind.Completed,
            eventKind is null ? "Private: time only." : "Private: confirmation seen.",
            Mode: ActivityMode.Private,
            Change: CaptureChange.Presence);
    }

    /// <summary>
    /// Text-first work. The picture check runs before any text is read; text
    /// is compared line by line with what this page already stored, and only
    /// what is new gets saved.
    /// </summary>
    private async Task<ManualScanOutcome> LookReadAsync(
        ForegroundWindowInfo window,
        AppProfile app,
        PageIdentity identity,
        PageProbe probe,
        long now,
        CancellationToken cancellationToken)
    {
        var latest = _store.GetLatestFacet();
        var continuing = Continues(latest, identity.Key, now);
        var state = _store.GetPageState(identity.Key);
        var userCaused = _inputIdle() <= UserInput.CausedWithinMilliseconds;

        IScreenFrame? frame = null;
        try
        {
            try
            {
                frame = await _frames.CaptureFrameAsync(window, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // No frame: no picture check and no OCR, but page text still works.
            }

            FrameDiff? diff = frame is null
                ? null
                : FrameSignature.Compare(state?.Signature, frame.Signature, state?.LiveCounts);
            // Only what this recording stored counts as known: after a stop the
            // session is sealed, and the next look starts from a fresh copy so
            // the new session has text of its own.
            var basisTexts = _store.GetPageBasis(identity.Key);
            var keyframeAt = basisTexts.Count > 0 ? state?.KeyframeAtMilliseconds ?? 0 : 0;
            void RememberPicture(long keyframe) =>
                _store.UpsertPageState(new PageState(
                    identity.Key,
                    frame?.Signature ?? state?.Signature,
                    diff?.LiveCounts ?? state?.LiveCounts ?? new byte[FrameSignature.CellCount],
                    frame is null ? state?.SignatureAtMilliseconds ?? 0 : now,
                    keyframe,
                    now,
                    diff?.Moved ?? false));

            // Full screen with most of the picture moving is a video, on any
            // site: watched, not read.
            if (WatchDetector.Detect(window, identity.Subject, probe.IsBrowser, [], diff, null) is not null)
            {
                RememberPicture(keyframeAt);
                return Watched(window, app, probe, identity, latest, now, diff);
            }

            // Picture check: nothing moved, or only the spots this page always
            // moves (a ticker, a clock, a video) while the user did nothing.
            if (state is not null
                && diff is not null
                && keyframeAt > 0
                && (diff.NothingMoved || (diff.OnlyLiveCells && !userCaused)))
            {
                RememberPicture(keyframeAt);
                return KeepPresence(window, identity, latest, continuing, now, "Picture check: nothing new on screen.");
            }

            var automationText = probe.Document is not null
                ? _pages.ExtractPageText(window, probe)
                : _automation.ExtractText(window);
            // A page with a media player's controls and a moving picture is
            // a video being watched, even out of full screen.
            if (WatchDetector.Detect(window, identity.Subject, probe.IsBrowser, [], diff, automationText.Text) is not null)
            {
                RememberPicture(keyframeAt);
                return Watched(window, app, probe, identity, latest, now, diff);
            }

            var ocr = frame is null
                ? null
                : await frame.RecognizeAsync(probe.DocumentBounds, diff?.LiveCells(), cancellationToken).ConfigureAwait(false);
            var combined = ChangeMeter.CleanText(
                CapturePipeline.CombineText(automationText.Text, ocr?.Text ?? string.Empty));
            if (identity.Site is null)
            {
                _resolver.Learn(app, combined.Length, window.IsFullscreen, now);
            }

            if (!string.IsNullOrWhiteSpace(probe.FocusedInput))
            {
                combined = combined.Length == 0
                    ? ChangeMeter.InputPrefix + probe.FocusedInput
                    : combined + Environment.NewLine + ChangeMeter.InputPrefix + probe.FocusedInput;
            }

            if (combined.Length == 0)
            {
                RememberPicture(keyframeAt);
                return KeepPresence(window, identity, latest, continuing, now, "No readable text in the window.");
            }

            if (SecretSniffer.ShouldDrop(combined) is { } dropReason)
            {
                RememberPicture(keyframeAt);
                KeepPresence(window, identity, latest, continuing, now, string.Empty);
                return new(
                    ManualScanOutcomeKind.DroppedSecretFrame,
                    $"Whole frame dropped before storage: {dropReason}.",
                    Mode: ActivityMode.Read);
            }

            var redacted = _redactor.Redact(combined);
            var verdict = ChangeMeter.Compare(
                redacted.Text,
                ChangeMeter.Basis(basisTexts),
                keyframeAt > 0 && basisTexts.Count > 0 ? now - keyframeAt : null);
            if (verdict.Kind == ChangeVerdictKind.Same)
            {
                RememberPicture(keyframeAt);
                return KeepPresence(window, identity, latest, continuing, now, "Nothing new on this page.");
            }

            var change = verdict.Kind == ChangeVerdictKind.Keyframe ? CaptureChange.Keyframe : CaptureChange.Delta;
            var text = change == CaptureChange.Keyframe ? redacted.Text : string.Join(Environment.NewLine, verdict.Lines);
            RememberPicture(change == CaptureChange.Keyframe ? now : keyframeAt);
            var record = Save(
                window,
                identity,
                now,
                change,
                text,
                redacted.Total,
                automationText.Text.Length,
                ocr,
                userCaused,
                ActivityEvidence.FindConfirmation(text) is null ? null : "confirmed");
            return new(
                ManualScanOutcomeKind.Completed,
                change == CaptureChange.Keyframe
                    ? "New screen: a full copy was redacted and stored."
                    : $"{verdict.Lines.Count} new line{(verdict.Lines.Count == 1 ? string.Empty : "s")} redacted and stored.",
                record,
                Mode: ActivityMode.Read,
                ScreenMoving: diff?.Moved ?? false,
                Change: change);
        }
        finally
        {
            frame?.Dispose();
        }
    }

    /// <summary>
    /// A look that turned out to be watching: no text is kept, the time lands
    /// on one Watch record for the page, and the host is told the screen is
    /// moving so a hands-off viewer is not taken for absent.
    /// </summary>
    private ManualScanOutcome Watched(
        ForegroundWindowInfo window,
        AppProfile app,
        PageProbe probe,
        PageIdentity identity,
        ScanFacet? latest,
        long now,
        FrameDiff? diff)
    {
        var watching = AsWatching(window, app, probe, identity, now, learnSite: false);
        if (Continues(latest, watching.Key, now) && latest!.Mode == ActivityMode.Watch)
        {
            _store.TouchScan(latest.Id, now);
        }
        else
        {
            SavePresence(window, watching, now, eventKind: null);
        }

        return new(
            ManualScanOutcomeKind.Unchanged,
            $"Watch: {watching.Subject}.",
            Mode: ActivityMode.Watch,
            ScreenMoving: diff?.Moved ?? true,
            Change: CaptureChange.Presence);
    }

    /// <summary>
    /// A record is extended only while it is the latest one, still open (not
    /// sealed into a session by a stop), and recently seen. A stop, a crash
    /// or a long absence always starts a new record, so time is never
    /// claimed across a gap nobody saw.
    /// </summary>
    internal static bool Continues(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] ScanFacet? latest,
        string pageKey,
        long now) =>
        latest is not null
        && latest.PageKey == pageKey
        && latest.SessionId is null
        && now - latest.LastSeenMilliseconds <= ContinueWithinMilliseconds;

    /// Longer than the slowest look interval (a minute, two on battery) with
    /// room to spare, shorter than the away threshold.
    internal const long ContinueWithinMilliseconds = 180_000;

    private ManualScanOutcome KeepPresence(
        ForegroundWindowInfo window,
        PageIdentity identity,
        ScanFacet? latest,
        bool continuing,
        long now,
        string detail)
    {
        if (continuing)
        {
            _store.TouchScan(latest!.Id, now);
        }
        else
        {
            // Back on a page whose text has not changed: no copy, but the
            // return is recorded so the time lands on the right activity.
            SavePresence(window, identity, now, eventKind: null);
        }

        return new(
            ManualScanOutcomeKind.Unchanged,
            detail,
            Mode: identity.Mode,
            Change: CaptureChange.Presence);
    }

    private void SavePresence(ForegroundWindowInfo window, PageIdentity identity, long now, string? eventKind) =>
        Save(window, identity, now, CaptureChange.Presence, null, 0, 0, null, false, eventKind);

    private void RecordPrivatePresence(
        ForegroundWindowInfo window,
        long now,
        string? eventKind,
        PageIdentity? identity = null)
    {
        var appName = ActivityIdentityResolver.DisplayNameOf(window.ProcessName, window.ExecutablePath);
        var privateIdentity = identity ?? new PageIdentity(
            $"{window.ProcessName.ToLowerInvariant()}|private|",
            window.ProcessName.ToLowerInvariant(),
            appName,
            null,
            appName,
            null,
            ActivityMode.Private,
            ActivityCategory.Other,
            false);
        var latest = _store.GetLatestFacet();
        if (Continues(latest, privateIdentity.Key, now) && (eventKind is null || latest!.EventKind == eventKind))
        {
            _store.TouchScan(latest!.Id, now);
            return;
        }

        Save(window, privateIdentity, now, CaptureChange.Presence, null, 0, 0, null, false, eventKind, storeTitle: false);
    }

    private ManualScanRecord Save(
        ForegroundWindowInfo window,
        PageIdentity identity,
        long now,
        CaptureChange change,
        string? text,
        int redactions,
        int automationCharacters,
        OcrCaptureResult? ocr,
        bool userCaused,
        string? eventKind,
        bool storeTitle = true)
    {
        var title = storeTitle && identity.Mode != ActivityMode.Private
            ? window.Title
            : string.Empty;
        var hashInput = text ?? $"presence|{identity.Key}|{now}|{Guid.NewGuid():N}";
        var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)));
        var scanId = Guid.NewGuid().ToString("N");
        var record = new ManualScanRecord(
            scanId,
            now,
            window.ProcessName,
            title,
            identity.Subject,
            null,
            ManualScanStatus.Completed,
            null,
            contentHash,
            string.Empty,
            automationCharacters,
            ocr?.Text.Length ?? 0,
            redactions,
            ocr?.CaptureElapsed.TotalMilliseconds ?? 0,
            ocr?.OcrElapsed.TotalMilliseconds ?? 0,
            0,
            OcrLanguage: ocr?.RecognizerLanguage,
            PageKey: identity.Key,
            AppName: identity.AppName,
            Site: identity.Site,
            Subject: identity.Subject,
            Phase: storeTitle ? identity.Phase : null,
            Mode: identity.Mode,
            Category: identity.Category,
            Change: change,
            LastSeenMilliseconds: now,
            UserCaused: userCaused,
            Unsaved: identity.Unsaved,
            DialogTitle: ActivityEvidence.DialogEventOf(window.DialogTitle) is null ? null : window.DialogTitle,
            EventKind: eventKind);
        var captureEvent = text is null
            ? null
            : new RawCaptureEvent(
                Guid.NewGuid().ToString("N"),
                now,
                window.ProcessName,
                window.ExecutablePath,
                title,
                contentHash,
                text,
                redactions);
        _store.SaveFacetScan(captureEvent, record);
        return record;
    }
}
