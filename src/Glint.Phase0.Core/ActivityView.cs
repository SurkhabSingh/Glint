using System.Globalization;
using System.Text;

namespace Glint.Phase0.Core;

/// <summary>
/// Sessions and activities for a stretch of time, worked out from the looks
/// and markers when they are asked for.
/// </summary>
/// <param name="Markers">The markers the grouping read, for callers that judge whether an activity has ended.</param>
public sealed record ActivitySnapshot(
    IReadOnlyList<ActivitySession> Sessions,
    IReadOnlyList<ActivityRecord> Activities,
    IReadOnlyDictionary<string, string> SessionByLook,
    IReadOnlyList<ActivityMarker> Markers);

/// <summary>
/// Works out sessions and activities when they are read, never stores them.
/// </summary>
/// <remarks>
/// Grouping is a pure function of what was recorded (looks, markers) and what
/// the user said (app modes): the same input always gives the same sessions
/// and activities, with the same ids. So there is nothing to seal, nothing to
/// rebuild after a correction, and nothing for a crash to leave half done. The
/// only things kept alongside are what cannot be worked out again for free:
/// summaries the model wrote, and the user's verdicts on tasks and sessions.
///
/// Most activities need no model at all. A game, a video, a file being edited
/// or a private site is described from facts Glint observed directly: the app,
/// the title, the time, saves, exports and confirmations. Only reading work
/// waits for a summary (<see cref="SummaryQueue"/>).
/// </remarks>
public sealed class ActivityView
{
    /// Sessions this short in total are glances, kept but not listed.
    internal const long MinorSessionMilliseconds = 30_000;

    /// How far back one step looks for the start of a session that reaches
    /// into the window being read.
    private const long LookbackStepMilliseconds = 86_400_000;

    private const int MaxLookbackSteps = 14;

    /// Looks past the end of the window are read too, so an activity near
    /// the edge is grouped exactly as it is when the next window is asked for.
    private const long TailMilliseconds = 3_600_000;

    private readonly IActivityViewStore _store;
    private readonly Func<long> _clock;

    public ActivityView(IActivityViewStore store, Func<long>? clock = null)
    {
        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Sessions and activities overlapping [from, to). Sessions that began
    /// earlier are worked out from their own start, so they come out the same
    /// whatever window is asked for.
    /// </summary>
    public ActivitySnapshot Build(long fromMilliseconds, long toMilliseconds)
    {
        var now = _clock();
        var start = fromMilliseconds;
        IReadOnlyList<ScanFacet> facets = [];
        IReadOnlyList<ActivityMarker> markers = [];
        for (var step = 0; ; step++)
        {
            facets = _store.GetFacetsBetween(start, toMilliseconds + TailMilliseconds);
            var earliest = facets.Count == 0 ? start : Math.Min(start, facets[0].CapturedAtMilliseconds);
            markers = _store.GetMarkers(earliest - Sessionizer.UnexplainedGapMilliseconds, toMilliseconds + TailMilliseconds + ActivitySegmenter.ResumeWithinMilliseconds);
            if (facets.Count == 0 || step >= MaxLookbackSteps)
            {
                break;
            }

            // Does a session cross the start? Only if the look before the
            // earliest one is close enough to belong with it.
            var before = _store.GetLastLookEndBefore(facets[0].CapturedAtMilliseconds);
            if (before is null || Sessionizer.IsBoundary(before.Value, facets[0].CapturedAtMilliseconds, markers))
            {
                break;
            }

            start = before.Value - LookbackStepMilliseconds;
        }

        if (facets.Count == 0)
        {
            return new ActivitySnapshot([], [], new Dictionary<string, string>(), markers);
        }

        var profiles = _store.GetAppProfiles().ToDictionary(profile => profile.Key, StringComparer.Ordinal);
        var byId = facets.ToDictionary(facet => facet.Id, StringComparer.Ordinal);
        var rows = facets
            .Select(facet => new CaptureRow(facet.Id, facet.CapturedAtMilliseconds, facet.ProcessName, facet.WindowTitle, facet.LastSeenMilliseconds))
            .ToList();
        var drafts = Sessionizer.Group(rows, now, markers);

        var sessions = new List<(ActivitySession Session, List<ActivityRecord> Activities)>();
        var sessionByLook = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var draft in drafts)
        {
            var sessionId = ActivityIds.Session(draft.ScanIds[0]);
            var sessionFacets = draft.ScanIds.Select(id => byId[id]).ToList();
            foreach (var id in draft.ScanIds)
            {
                sessionByLook[id] = sessionId;
            }

            var session = new ActivitySession(
                sessionId,
                draft.StartedAtMilliseconds,
                draft.EndedAtMilliseconds,
                draft.ProcessName,
                draft.WindowTitle,
                draft.ScanIds,
                null,
                null,
                draft.StillOpen ? ActivitySessionStatus.Active : ActivitySessionStatus.Closed);
            sessions.Add((session, BuildActivities(sessionId, sessionFacets, markers, profiles).ToList()));
        }

        // What was paid for, or said by the user, laid over what was worked out.
        var all = sessions.SelectMany(entry => entry.Activities).ToList();
        var summaries = _store.GetSummaries(all.Select(activity => activity.Key).ToList())
            .GroupBy(summary => (summary.ActivityKey, summary.StartedAtMilliseconds))
            .ToDictionary(group => group.Key, group => group.First());
        var tasks = _store.GetTaskStatuses(all.Select(activity => activity.Id).ToList());
        var outcomes = _store.GetSessionOutcomes(sessions.Select(entry => entry.Session.Id).ToList());

        var resultSessions = new List<ActivitySession>();
        var resultActivities = new List<ActivityRecord>();
        foreach (var (session, activities) in sessions)
        {
            for (var index = 0; index < activities.Count; index++)
            {
                var activity = activities[index];
                if (summaries.TryGetValue((activity.Key, activity.StartedAtMilliseconds), out var summary))
                {
                    activity = WithSummary(activity, summary);
                }

                if (tasks.TryGetValue(activity.Id, out var status))
                {
                    activity = activity with { TaskStatus = status, TaskSetByUser = true };
                }

                activities[index] = activity;
            }

            var rolled = RollUp(session, activities);
            if (outcomes.TryGetValue(session.Id, out var outcome))
            {
                rolled = rolled with { Outcome = outcome, OutcomeSource = SessionOutcomeSource.User };
            }

            if (rolled.StartedAtMilliseconds < toMilliseconds && rolled.EndedAtMilliseconds >= fromMilliseconds)
            {
                resultSessions.Add(rolled);
            }

            resultActivities.AddRange(activities.Where(activity =>
                activity.StartedAtMilliseconds < toMilliseconds
                && Math.Max(activity.EndedAtMilliseconds, activity.StartedAtMilliseconds + 1) > fromMilliseconds));
        }

        return new ActivitySnapshot(resultSessions, resultActivities, sessionByLook, markers);
    }

    private IEnumerable<ActivityRecord> BuildActivities(
        string sessionId,
        IReadOnlyList<ScanFacet> facets,
        IReadOnlyList<ActivityMarker> markers,
        IReadOnlyDictionary<string, AppProfile> profiles)
    {
        var constantsByProcess = facets
            .GroupBy(facet => facet.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => TitleNormalizer.LearnConstantSegments(group.Select(facet => facet.WindowTitle).ToList()),
                StringComparer.OrdinalIgnoreCase);
        var first = facets.Min(facet => facet.CapturedAtMilliseconds);
        var last = facets.Max(facet => facet.LastSeenMilliseconds);
        var endings = markers
            .Where(marker => marker.EndsAStretch
                && marker.TimestampMilliseconds >= first
                && marker.TimestampMilliseconds <= last + ActivitySegmenter.FillGapUpToMilliseconds)
            .Select(marker => marker.TimestampMilliseconds)
            .ToList();
        var closings = markers
            .Where(marker => marker.ClosesApp
                && marker.TimestampMilliseconds >= first
                && marker.TimestampMilliseconds <= last + ActivitySegmenter.ResumeWithinMilliseconds)
            .Select(marker => (marker.TimestampMilliseconds, marker.Detail!))
            .ToList();
        var byId = facets.ToDictionary(facet => facet.Id, StringComparer.Ordinal);
        var drafts = ActivitySegmenter.Segment(
            facets,
            facet => Identify(facet, constantsByProcess, profiles),
            endings,
            closings);
        return drafts.Select(draft => ToRecord(sessionId, draft, byId));
    }

    /// <summary>
    /// The identity of a stored look. Looks stored by the activity model carry
    /// their own; older captures are identified from their process and title,
    /// which is how history recorded before this model gets separated too. A
    /// mode the user set always wins, so a correction applies retroactively.
    /// </summary>
    private static PageIdentity Identify(
        ScanFacet facet,
        IReadOnlyDictionary<string, IReadOnlySet<string>> constants,
        IReadOnlyDictionary<string, AppProfile> profiles)
    {
        var userSite = facet.Site is null ? null : profiles.GetValueOrDefault(ActivityIdentityResolver.SiteKeyOf(facet.Site));
        var userApp = profiles.GetValueOrDefault(ActivityIdentityResolver.AppKeyOf(facet.ProcessName));
        var userOverride = userSite?.Source == ModeSource.User
            ? userSite
            : userApp?.Source == ModeSource.User && facet.Site is null ? userApp : null;

        // Recognized as a game since this look was stored: an emulator, a
        // launcher-started game, or a game that ran as administrator and was
        // kept as private time. Shown as play now, named by the app.
        var gameNow = userOverride is null
            && facet.Site is null
            && facet.Mode is { } recorded
            && recorded != ActivityMode.Play
            && (ActivityCatalog.ForProcess(facet.ProcessName) is { Mode: ActivityMode.Play }
                || ActivityCatalog.IsEmulator(facet.ProcessName)
                || userApp is { Mode: ActivityMode.Play, Source: not ModeSource.Provisional });
        if (gameNow)
        {
            return ActivityIdentityResolver.Compose(
                facet.ProcessName,
                userApp?.DisplayName ?? facet.AppName ?? facet.ProcessName,
                facet.Mode == ActivityMode.Private ? string.Empty : facet.WindowTitle,
                null,
                ActivityMode.Play,
                ActivityCategory.Game,
                constants.GetValueOrDefault(facet.ProcessName));
        }

        if (facet.PageKey is not null && facet.Mode is { } storedMode && userOverride is null)
        {
            return new PageIdentity(
                facet.PageKey,
                facet.ProcessName.ToLowerInvariant(),
                facet.AppName ?? facet.ProcessName,
                facet.Site,
                facet.Subject ?? facet.AppName ?? facet.ProcessName,
                facet.Phase,
                storedMode,
                facet.Category ?? ActivityCategory.Other,
                facet.Unsaved);
        }

        // Private looks never stored a title, and must not be re-derived into
        // anything more specific than they were.
        if (facet.Mode == ActivityMode.Private && userOverride is null)
        {
            return new PageIdentity(
                facet.PageKey ?? $"{facet.ProcessName.ToLowerInvariant()}|private|",
                facet.ProcessName.ToLowerInvariant(),
                facet.AppName ?? facet.ProcessName,
                facet.Site,
                facet.Subject ?? facet.AppName ?? facet.ProcessName,
                null,
                ActivityMode.Private,
                facet.Category ?? ActivityCategory.Other,
                false);
        }

        var inGameFolder = ActivityCatalog.IsInGameFolder(facet.ExecutablePath)
            || ActivityCatalog.IsKnownToGameBar(facet.ExecutablePath)
            || ActivityCatalog.IsEmulator(facet.ProcessName)
            || GameSignals.LooksLikeGameInstall(facet.ExecutablePath);
        var known = userOverride is not null
            ? (userOverride.Mode, userOverride.Category)
            : ActivityCatalog.ForSite(facet.Site)
              ?? ActivityCatalog.ForProcess(facet.ProcessName)
              ?? (inGameFolder ? ((ActivityMode, ActivityCategory)?)(ActivityMode.Play, ActivityCategory.Game) : null)
              ?? (userApp is not null ? (userApp.Mode, userApp.Category) : (ActivityMode.Read, ActivityCategory.Other));
        return ActivityIdentityResolver.Compose(
            facet.ProcessName,
            facet.AppName
                ?? userApp?.DisplayName
                ?? ActivityIdentityResolver.DisplayNameOf(facet.ProcessName, facet.ExecutablePath),
            facet.WindowTitle,
            facet.Site,
            known.Item1,
            known.Item2,
            constants.GetValueOrDefault(facet.ProcessName));
    }

    private static ActivityRecord ToRecord(string sessionId, ActivityDraft draft, IReadOnlyDictionary<string, ScanFacet> facets)
    {
        var identity = draft.DominantIdentity;
        var looks = draft.ScanIds.Select(id => facets[id]).ToList();
        // Reading work with text waits for a summary; looks stored before
        // page lines (no change kind) always carried text.
        var hasText = identity.Mode == ActivityMode.Read
            && looks.Any(look => look.Change is null or CaptureChange.Keyframe or CaptureChange.Delta);
        var confirmed = draft.Events.Any(item => item.Kind == "confirmed");
        string? task = null;
        var taskStatus = ActivityTaskStatus.None;
        if (identity.Category == ActivityCategory.Finance && confirmed)
        {
            task = $"Payment on {identity.Subject}";
            taskStatus = ActivityTaskStatus.LooksDone;
        }

        return new ActivityRecord(
            ActivityIds.Activity(identity.Key, draft.StartedAtMilliseconds),
            sessionId,
            identity.Key,
            identity.AppName,
            identity.Site,
            identity.Subject,
            identity.Mode,
            identity.Category,
            draft.StartedAtMilliseconds,
            draft.EndedAtMilliseconds,
            draft.ActiveMilliseconds,
            draft.Segments,
            draft.Glances,
            draft.Events,
            draft.Phases,
            draft.ScanIds,
            RuleLabel(identity),
            identity.Mode == ActivityMode.Read ? null : RuleSummary(identity, draft),
            task,
            taskStatus,
            false,
            hasText ? SummaryCheck.Pending : SummaryCheck.Rule,
            0,
            0,
            looks.Select(look => look.PageKey).OfType<string>().Distinct(StringComparer.Ordinal).ToList());
    }

    private static ActivityRecord WithSummary(ActivityRecord activity, StoredSummary summary)
    {
        // A summary only describes reading work; for anything else (the user
        // has since called it a game) the rule label stands.
        if (activity.Mode != ActivityMode.Read)
        {
            return activity;
        }

        var confirmed = activity.Events.Any(item => item.Kind == "confirmed");
        return activity with
        {
            Label = string.IsNullOrWhiteSpace(summary.Label) ? activity.Label : summary.Label,
            Summary = summary.Summary,
            Task = summary.Task ?? activity.Task,
            TaskStatus = summary.Task is null
                ? activity.TaskStatus
                : confirmed ? ActivityTaskStatus.LooksDone : ActivityTaskStatus.Open,
            Check = summary.Check,
            FactsKept = summary.FactsKept,
            FactsDropped = summary.FactsDropped
        };
    }

    /// <summary>
    /// The session's own label and summary, written from its activities, so
    /// everything that reads sessions sees the same separated picture.
    /// </summary>
    internal static ActivitySession RollUp(ActivitySession session, IReadOnlyList<ActivityRecord> activities)
    {
        if (activities.Count == 0)
        {
            return session with { IsMinor = true, Summary = session.Summary ?? "Nothing recorded." };
        }

        var byTime = activities.OrderBy(activity => activity.StartedAtMilliseconds).ToList();
        var end = Math.Max(session.EndedAtMilliseconds, activities.Max(activity => activity.EndedAtMilliseconds));
        var label = string.Join(
            " · ",
            activities
                .OrderByDescending(activity => activity.ActiveMilliseconds)
                .Take(3)
                .Select(activity => activity.Label));
        var summary = new StringBuilder();
        foreach (var activity in byTime)
        {
            summary.Append(Clock(activity.StartedAtMilliseconds))
                .Append('–')
                .Append(Clock(activity.EndedAtMilliseconds))
                .Append(' ')
                .Append(activity.Label)
                .Append(" (")
                .Append(Duration(activity.ActiveMilliseconds))
                .Append(')');
            // A model summary adds what was said and done. A rule summary
            // restates the label, so only its events are worth adding.
            var detail = activity.Check is SummaryCheck.Verified or SummaryCheck.Partial
                ? activity.Summary
                : EventsSentence(activity.Events);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                summary.Append(": ").Append(detail);
            }

            summary.AppendLine();
        }

        var openTask = byTime.FirstOrDefault(activity => activity.TaskStatus == ActivityTaskStatus.Open)?.Task;
        var rolled = session with
        {
            EndedAtMilliseconds = end,
            Label = label.Length > 160 ? label[..160] : label,
            Summary = summary.ToString().TrimEnd(),
            IsMinor = activities.Sum(activity => activity.ActiveMilliseconds) < MinorSessionMilliseconds,
            ReminderCandidate = openTask
        };
        if (session.OutcomeSource != SessionOutcomeSource.User)
        {
            rolled = rolled with
            {
                Outcome = openTask is null ? SessionOutcome.Unknown : SessionOutcome.Open,
                OutcomeSource = SessionOutcomeSource.Rule
            };
        }

        return rolled;
    }

    internal static string RuleLabel(PageIdentity identity) => identity.Mode switch
    {
        ActivityMode.Play => $"Played {identity.Subject}",
        ActivityMode.Watch when identity.Category == ActivityCategory.Music => $"Music on {identity.Subject}",
        ActivityMode.Watch => $"Watched {identity.Subject}",
        ActivityMode.Make when identity.Subject.Equals(identity.AppName, StringComparison.OrdinalIgnoreCase) => $"Worked in {identity.AppName}",
        ActivityMode.Make => $"Edited {identity.Subject}",
        ActivityMode.Private => $"{identity.Subject} (private)",
        _ => identity.Category switch
        {
            ActivityCategory.Email => $"Email in {identity.Subject}",
            ActivityCategory.Chat => $"Chat in {identity.Subject}",
            ActivityCategory.Files => "Browsed files",
            _ => identity.Subject
        }
    };

    internal static string RuleSummary(PageIdentity identity, ActivityDraft draft)
    {
        var parts = new List<string>();
        var active = Duration(draft.ActiveMilliseconds);
        parts.Add(identity.Mode switch
        {
            ActivityMode.Play => $"Played {identity.Subject} for {active}.",
            ActivityMode.Watch when identity.Category == ActivityCategory.Music && draft.Phases.Count > 0 =>
                $"{draft.Phases[^1]} on {identity.Subject}, {active}.",
            ActivityMode.Watch => $"Watched {identity.Subject} for {active}.",
            ActivityMode.Make when identity.Subject.Equals(identity.AppName, StringComparison.OrdinalIgnoreCase) =>
                $"Worked in {identity.AppName} for {active}.",
            ActivityMode.Make => $"Edited {identity.Subject} in {identity.AppName} for {active}.",
            _ => $"{identity.Subject}, {active}. No page content was kept."
        });

        var events = EventsSentence(draft.Events);
        if (events.Length > 0)
        {
            parts.Add(events);
        }

        if (identity.Mode == ActivityMode.Make && draft.Phases.Count > 1)
        {
            parts.Add($"Moved through {string.Join(", ", draft.Phases.Take(5))}.");
        }

        return string.Join(" ", parts);
    }

    private static string EventsSentence(IEnumerable<ActivityEvent> events)
    {
        var phrases = string.Join(
            ", ",
            events
                .Where(item => item.Kind != "interrupted")
                .GroupBy(item => item.Kind)
                .Select(group => EventPhrase(group.Key, group.Count())));
        return phrases.Length == 0 ? string.Empty : char.ToUpperInvariant(phrases[0]) + phrases[1..] + ".";
    }

    private static string EventPhrase(string kind, int count)
    {
        var times = count == 1 ? "once" : count == 2 ? "twice" : $"{count} times";
        return kind switch
        {
            "saved" => $"saved {times}",
            "saved-as" => $"saved a copy {times}",
            "exported" => $"exported {times}",
            "printed" => $"printed {times}",
            "rendered" => $"rendered {times}",
            "published" => $"published {times}",
            "confirmed" => "confirmation seen",
            _ => $"{kind} {times}"
        };
    }

    internal static string Duration(long milliseconds)
    {
        var minutes = (int)Math.Round(milliseconds / 60_000.0);
        if (minutes < 1)
        {
            return "under a minute";
        }

        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
    }

    private static string Clock(long milliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
}
