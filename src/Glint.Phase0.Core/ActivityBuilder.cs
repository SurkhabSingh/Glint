using System.Globalization;
using System.Text;

namespace Glint.Phase0.Core;

/// <summary>
/// Turns sealed sessions into activities, then describes each activity on
/// its own. Runs after the session builder has sealed sessions.
/// </summary>
/// <remarks>
/// Most activities need no model at all. A game, a video, a file being edited
/// or a private site is described from facts Glint observed directly: the app,
/// the title, the time, saves, exports and confirmations. Only reading work
/// (email, chat, documents, pages) gets a model call, one per activity, from
/// that activity's text alone, and its output is checked against that text
/// before it is kept.
/// </remarks>
public sealed class ActivityBuilder
{
    /// Below this much text there is nothing for a model to add.
    internal const int MinimumNarrationCharacters = 160;

    /// Bound on one run's rebuilding, far above any real history.
    internal const int MaxSessionsPerRun = 5_000;

    /// Sessions this short in total are glances, kept but not listed.
    internal const long MinorSessionMilliseconds = 30_000;

    private readonly IActivityWorkStore _store;
    private readonly IActivityStore _profiles;
    private readonly IActivityNarrator? _narrator;
    private readonly int _maxNarrations;

    public ActivityBuilder(
        IActivityWorkStore store,
        IActivityStore profiles,
        IActivityNarrator? narrator,
        int maxNarrations = 8)
    {
        _store = store;
        _profiles = profiles;
        _narrator = narrator;
        _maxNarrations = maxNarrations;
    }

    public async Task<ActivityBuildResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var sessions = 0;
        var built = 0;
        // Every session waiting, in batches: a rebuild after an upgrade or a
        // mode correction can cover the whole history. Each batch marks its
        // sessions built, so the loop always ends.
        for (var batch = _store.GetSessionsWithoutActivities();
             batch.Count > 0 && sessions < MaxSessionsPerRun;
             batch = _store.GetSessionsWithoutActivities())
        {
            foreach (var session in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var activities = BuildActivities(session);
                _store.ReplaceSessionActivities(RollUp(session, activities), activities);
                sessions++;
                built += activities.Count;
            }
        }

        var narrated = 0;
        var failed = 0;
        var verified = 0;
        var partial = 0;
        var fallback = 0;
        if (_narrator is not null && _maxNarrations > 0)
        {
            foreach (var activity in _store.GetActivitiesPendingNarration(_maxNarrations))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var described = await NarrateAsync(activity, cancellationToken).ConfigureAwait(false);
                    _store.UpdateActivity(described);
                    RefreshSession(described.SessionId);
                    narrated++;
                    switch (described.Check)
                    {
                        case SummaryCheck.Verified:
                            verified++;
                            break;
                        case SummaryCheck.Partial:
                            partial++;
                            break;
                        case SummaryCheck.Fallback:
                            fallback++;
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (InvalidDataException)
                {
                    // The model answered but not in the required shape. The
                    // rule-based label stands; retrying would loop forever.
                    _store.UpdateActivity(activity with { Check = SummaryCheck.Fallback });
                    RefreshSession(activity.SessionId);
                    fallback++;
                }
                catch
                {
                    // The worker failed outright: left pending for the next run.
                    failed++;
                }
            }
        }

        return new ActivityBuildResult(sessions, built, narrated, failed, verified, partial, fallback);
    }

    /// <summary>
    /// Activities for what is being recorded right now: captures not yet
    /// sealed into a session, segmented the same way but not stored. Labels
    /// come from rules; summaries arrive once the session is sealed.
    /// </summary>
    public IReadOnlyList<ActivityRecord> BuildLive()
    {
        var captures = _store.GetUnassignedCaptures(5_000);
        if (captures.Count == 0)
        {
            return [];
        }

        var live = new ActivitySession(
            "live",
            captures.Min(capture => capture.CapturedAtMilliseconds),
            captures.Max(capture => capture.EndMilliseconds),
            captures[0].ProcessName,
            string.Empty,
            captures.Select(capture => capture.Id).ToList(),
            null,
            null,
            ActivitySessionStatus.Active);
        return BuildActivities(live);
    }

    private IReadOnlyList<ActivityRecord> BuildActivities(ActivitySession session)
    {
        var facets = _store.GetScanFacets(session.ScanIds);
        var constantsByProcess = facets
            .GroupBy(facet => facet.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => TitleNormalizer.LearnConstantSegments(group.Select(facet => facet.WindowTitle).ToList()),
                StringComparer.OrdinalIgnoreCase);
        var endings = facets.Count == 0
            ? []
            : _store.GetMarkers(
                    facets.Min(facet => facet.CapturedAtMilliseconds),
                    facets.Max(facet => facet.LastSeenMilliseconds) + ActivitySegmenter.FillGapUpToMilliseconds)
                .Where(marker => marker.EndsAStretch)
                .Select(marker => marker.TimestampMilliseconds)
                .ToList();
        var drafts = ActivitySegmenter.Segment(facets, facet => Identify(facet, constantsByProcess), endings);
        return drafts.Select(draft => ToRecord(session.Id, draft)).ToList();
    }

    /// <summary>
    /// The identity of a stored look. Looks stored by the activity model carry
    /// their own; older captures are identified from their process and title,
    /// which is how history recorded before this model gets separated too. A
    /// mode the user set always wins, so a correction applies retroactively.
    /// </summary>
    private PageIdentity Identify(ScanFacet facet, IReadOnlyDictionary<string, IReadOnlySet<string>> constants)
    {
        var userSite = facet.Site is null ? null : _profiles.GetAppProfile(ActivityIdentityResolver.SiteKeyOf(facet.Site));
        var userApp = _profiles.GetAppProfile(ActivityIdentityResolver.AppKeyOf(facet.ProcessName));
        var userOverride = userSite?.Source == ModeSource.User
            ? userSite
            : userApp?.Source == ModeSource.User && facet.Site is null ? userApp : null;

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
            || ActivityCatalog.IsKnownToGameBar(facet.ExecutablePath);
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

    private ActivityRecord ToRecord(string sessionId, ActivityDraft draft)
    {
        var identity = draft.DominantIdentity;
        var texts = identity.Mode == ActivityMode.Read ? _store.GetScanTexts(draft.ScanIds) : [];
        var hasText = texts.Count > 0;

        // A confirmation on a page whose stored text holds none is a false
        // one (an earlier check matched mentions of the phrase): dropped on
        // every rebuild. Private looks store no text, so theirs stand.
        if (hasText && texts.All(text => ActivityEvidence.FindConfirmation(text.Text) is null))
        {
            draft.Events.RemoveAll(item => item.Kind == "confirmed");
        }

        var confirmed = draft.Events.Any(item => item.Kind == "confirmed");
        string? task = null;
        var taskStatus = ActivityTaskStatus.None;
        if (identity.Category == ActivityCategory.Finance && confirmed)
        {
            task = $"Payment on {identity.Subject}";
            taskStatus = ActivityTaskStatus.LooksDone;
        }

        return new ActivityRecord(
            Guid.NewGuid().ToString("N"),
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
            0);
    }

    private async Task<ActivityRecord> NarrateAsync(ActivityRecord activity, CancellationToken cancellationToken)
    {
        var text = ActivityText.Build(_store.GetScanTexts(activity.ScanIds));
        if (text.Length < MinimumNarrationCharacters)
        {
            return activity with { Check = SummaryCheck.Rule };
        }

        var narration = await _narrator!.NarrateAsync(
                new NarrationRequest(
                    activity.Category,
                    activity.App,
                    activity.Site,
                    activity.Subject,
                    activity.Phases,
                    DateTimeOffset.FromUnixTimeMilliseconds(activity.StartedAtMilliseconds),
                    text),
                cancellationToken)
            .ConfigureAwait(false);

        var others = _store.GetSessionActivities(activity.SessionId)
            .Where(other => other.Id != activity.Id && other.Key != activity.Key)
            .Select(other => $"{other.Subject} {string.Join(' ', other.Phases)}");
        var metadata = new List<string> { activity.App, activity.Subject };
        if (activity.Site is not null)
        {
            metadata.Add(activity.Site);
            metadata.Add(TitleNormalizer.SiteBrand(activity.Site));
        }

        metadata.AddRange(activity.Phases);
        var verified = SummaryVerifier.Verify(narration, text, metadata, others);

        var task = activity.TaskSetByUser ? activity.Task : verified.Task;
        var taskStatus = activity.TaskSetByUser
            ? activity.TaskStatus
            : task is null
                ? ActivityTaskStatus.None
                : DoneAfterTask(activity) ? ActivityTaskStatus.LooksDone : ActivityTaskStatus.Open;
        return activity with
        {
            Label = verified.Label ?? activity.Label,
            Summary = verified.Summary,
            Task = task,
            TaskStatus = taskStatus,
            Check = verified.Check,
            FactsKept = verified.Kept,
            FactsDropped = verified.Dropped
        };
    }

    /// <summary>
    /// Whether the activity itself shows the task got done: a confirmation
    /// ("Message sent", "Payment successful") seen in it. A proposal only.
    /// </summary>
    private static bool DoneAfterTask(ActivityRecord activity) =>
        activity.Events.Any(item => item.Kind == "confirmed");

    private void RefreshSession(string sessionId)
    {
        var session = _store.GetSession(sessionId);
        if (session is null)
        {
            return;
        }

        _store.UpsertSession(RollUp(session, _store.GetSessionActivities(sessionId)));
    }

    /// <summary>
    /// The session's own label and summary, written from its activities, so
    /// everything that reads sessions (Ask, search, the tray) sees the same
    /// separated picture.
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
            ActivityMode.Watch when identity.Phases(draft) is { } track => $"{track} on {identity.Subject}, {active}.",
            ActivityMode.Watch => $"Watched {identity.Subject} for {active}.",
            ActivityMode.Make when identity.Subject.Equals(identity.AppName, StringComparison.OrdinalIgnoreCase) =>
                $"Worked in {identity.AppName} for {active}.",
            ActivityMode.Make => $"Edited {identity.Subject} in {identity.AppName} for {active}.",
            _ => $"{identity.Subject}, {active}. No page content was kept."
        });

        var counts = draft.Events
            .Where(item => item.Kind != "interrupted")
            .GroupBy(item => item.Kind)
            .Select(group => EventPhrase(group.Key, group.Count()));
        var events = string.Join(", ", counts);
        if (events.Length > 0)
        {
            parts.Add(char.ToUpperInvariant(events[0]) + events[1..] + ".");
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

internal static class PageIdentityExtensions
{
    /// The most recent track or section for music, if the title named one.
    public static string? Phases(this PageIdentity identity, ActivityDraft draft) =>
        identity.Category == ActivityCategory.Music && draft.Phases.Count > 0 ? draft.Phases[^1] : null;
}
