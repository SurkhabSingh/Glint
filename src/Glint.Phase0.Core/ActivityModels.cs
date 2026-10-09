using System.Text.Json.Serialization;

namespace Glint.Phase0.Core;

/// <summary>
/// How Glint watches an app. Decided once per app (or per site inside a
/// browser) and remembered, so one text-heavy game menu can never flip a
/// game into reading mode.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActivityMode
{
    /// Text-first work: browsing, email, chat, documents, code. Page text is
    /// read and compared line by line.
    Read,

    /// Visual work on a file: Photoshop, Lightroom, Blender. Tracked by the
    /// file, saves, exports and the picture check; text is not the signal.
    Make,

    /// Games. App and time only, plus a cheap motion check.
    Play,

    /// Video and music. Title and time; no text reading.
    Watch,

    /// Bills, banking, trading, blocked apps. App or site and time only.
    Private
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActivityCategory
{
    Other,
    Browsing,
    Email,
    Chat,
    Learning,
    Video,
    Music,
    Game,
    Finance,
    Coding,
    Docs,
    Design,
    Photo,
    Files,
    // Appended: stored by name, but keep the order stable anyway.
    Assistant
}

/// <summary>
/// What a look at the screen added, decided by comparing it with the same
/// page's previous looks rather than with whatever window came last.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CaptureChange
{
    /// A full copy of the page: first visit, a different screen, or the
    /// periodic refresh.
    Keyframe,

    /// Only the lines that were new since the page's last copy.
    Delta,

    /// A record that the app was in front, with no text: games, video,
    /// creative apps, private apps, or a return to an unchanged page.
    Presence
}

/// <summary>
/// Where an app's mode came from. A weaker source never overwrites a
/// stronger one, and the user's choice is absolute.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModeSource
{
    /// Nothing known yet; Read until samples say otherwise.
    Provisional,

    /// Learned from how much text the app shows and whether it runs full screen.
    Learned,

    /// The built-in list of known apps, sites and game folders.
    Catalog,

    /// The user said so.
    User
}

/// The remembered decision about one app or one site.
public sealed record AppProfile(
    string Key,
    string DisplayName,
    ActivityMode Mode,
    ActivityCategory Category,
    ModeSource Source,
    int Samples = 0,
    int SparseSamples = 0,
    long UpdatedAtMilliseconds = 0);

/// <summary>
/// Which activity a look belongs to. Only these fields can start a new
/// activity; screen content never can.
/// </summary>
public sealed record PageIdentity(
    string Key,
    string AppKey,
    string AppName,
    string? Site,
    string Subject,
    string? Phase,
    ActivityMode Mode,
    ActivityCategory Category,
    bool Unsaved);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActivityTaskStatus
{
    None,
    Open,

    /// Glint saw evidence it was finished ("Message sent", "Payment
    /// successful"). A proposal until the user confirms.
    LooksDone,

    Done
}

/// How far a summary can be trusted.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SummaryCheck
{
    /// A Read activity still waiting for its model call.
    Pending,

    /// Written by rules from facts Glint observed directly; no model involved.
    Rule,

    /// Every name, number and date in it was found in the activity's own text.
    Verified,

    /// Some sentences were removed because their facts were not on screen.
    Partial,

    /// Nothing the model wrote survived the check; the rule-based label stands.
    Fallback
}

public sealed record ActivitySegment(long StartMilliseconds, long EndMilliseconds);

/// A short visit to something else that did not interrupt the activity.
public sealed record ActivityGlance(
    string App,
    string Subject,
    long StartMilliseconds,
    long EndMilliseconds);

/// Something that happened inside an activity: saved, exported, sent,
/// confirmed, interrupted.
public sealed record ActivityEvent(
    long AtMilliseconds,
    string Kind,
    string? Detail = null);

public sealed record ActivityRecord(
    string Id,
    string SessionId,
    string Key,
    string App,
    string? Site,
    string Subject,
    ActivityMode Mode,
    ActivityCategory Category,
    long StartedAtMilliseconds,
    long EndedAtMilliseconds,
    long ActiveMilliseconds,
    IReadOnlyList<ActivitySegment> Segments,
    IReadOnlyList<ActivityGlance> Glances,
    IReadOnlyList<ActivityEvent> Events,
    IReadOnlyList<string> Phases,
    IReadOnlyList<string> ScanIds,
    string Label,
    string? Summary,
    string? Task,
    ActivityTaskStatus TaskStatus,
    bool TaskSetByUser,
    SummaryCheck Check,
    int FactsKept,
    int FactsDropped);

/// <summary>
/// What the segmenter needs to know about one stored look. Text is not
/// included: grouping never reads captured content.
/// </summary>
public sealed record ScanFacet(
    string Id,
    long CapturedAtMilliseconds,
    long LastSeenMilliseconds,
    string ProcessName,
    string WindowTitle,
    string? PageKey,
    string? AppName,
    string? Site,
    string? Subject,
    string? Phase,
    ActivityMode? Mode,
    ActivityCategory? Category,
    CaptureChange? Change,
    bool Unsaved,
    string? DialogTitle,
    string? EventKind,
    bool UserCaused,
    // From the capture's raw event, so history stored before activities
    // existed can still be recognized as a game by its install folder.
    string? ExecutablePath = null,
    // Set once the record is sealed into a session; a sealed record is
    // history and is never extended again.
    string? SessionId = null);

/// One stored look with its text, for building a summary prompt.
public sealed record ScanText(
    string Id,
    long CapturedAtMilliseconds,
    CaptureChange Change,
    bool UserCaused,
    string Text);

/// What the picture check remembers about a page between looks.
public sealed record PageState(
    string PageKey,
    FrameSignature? Signature,
    byte[] LiveCounts,
    long SignatureAtMilliseconds,
    long KeyframeAtMilliseconds,
    long UpdatedAtMilliseconds,
    // Whether the screen moved at the last picture check: in Play and Watch
    // that means the user is watching, not away.
    bool Moving = false);

public sealed record ActivityBuildResult(
    int SessionsProcessed,
    int Activities,
    int Narrated,
    int NarrationFailed,
    int Verified,
    int Partial,
    int Fallback);

public interface IActivityStore
{
    AppProfile? GetAppProfile(string key);

    void UpsertAppProfile(AppProfile profile);

    IReadOnlyList<AppProfile> GetAppProfiles();

    PageState? GetPageState(string pageKey);

    void UpsertPageState(PageState state);

    /// The most recent stored look, any window.
    ScanFacet? GetLatestFacet();

    /// Recent titles of one app, newest first, so the noise in its titles
    /// (the app name, counters) can be learned.
    IReadOnlyList<string> GetRecentTitles(string processName, int limit = 40);

    /// Text of a page since (and including) its latest full copy, oldest first.
    IReadOnlyList<string> GetPageBasis(string pageKey, int limit = 60);

    void SaveFacetScan(RawCaptureEvent? captureEvent, ManualScanRecord scan);

    /// Extends a stored look instead of saving a duplicate.
    void TouchScan(string scanId, long lastSeenMilliseconds);
}

/// What startup recovery found: whether an unfinished recording was closed,
/// and how much is waiting to be grouped or summarized.
public sealed record RecoveryResult(bool ClosedRun, long? ClosedAtMilliseconds, long Pending);

public interface IActivityWorkStore
{
    IReadOnlyList<CaptureRow> GetUnassignedCaptures(int limit = 1_000);

    IReadOnlyList<ActivityMarker> GetMarkers(long fromMilliseconds, long toMilliseconds);

    IReadOnlyList<ActivitySession> GetSessionsWithoutActivities(int limit = 50);

    IReadOnlyList<ScanFacet> GetScanFacets(IReadOnlyList<string> scanIds);

    IReadOnlyList<ScanText> GetScanTexts(IReadOnlyList<string> scanIds);

    void ReplaceSessionActivities(
        ActivitySession session,
        IReadOnlyList<ActivityRecord> activities);

    IReadOnlyList<ActivityRecord> GetActivitiesPendingNarration(int limit = 10);

    IReadOnlyList<ActivityRecord> GetSessionActivities(string sessionId);

    void UpdateActivity(ActivityRecord activity);

    void UpsertSession(ActivitySession session);

    ActivitySession? GetSession(string id);

    /// A summary written while recording, kept until its session is sealed.
    void SaveEarlyNarration(ActivityRecord activity, long nowMilliseconds);

    bool HasEarlyNarration(ActivityRecord activity);
}
