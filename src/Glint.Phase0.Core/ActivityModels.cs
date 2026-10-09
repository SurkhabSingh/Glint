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
/// What a look at the screen added, decided by comparing its lines with the
/// lines the same page already holds.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CaptureChange
{
    /// A full copy of the page. Only looks stored before page lines existed
    /// carry this; their text was moved into page lines.
    Keyframe,

    /// Lines the page did not hold yet were stored.
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
    int FactsDropped,
    // The pages this activity's looks were on: where its text is found.
    IReadOnlyList<string>? PageKeys = null);

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
    // So history stored before games were recognized can still be
    // recognized as a game by its install folder.
    string? ExecutablePath = null);

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
    // When this page's text was last read. Lines seen then are the ones
    // still on screen when a later picture check finds nothing changed.
    long TextReadAtMilliseconds,
    long UpdatedAtMilliseconds,
    // Whether the screen moved at the last picture check: in Play and Watch
    // that means the user is watching, not away.
    bool Moving = false);

/// <summary>
/// One distinct line of a page, as a look read it. Lines are compared by
/// <see cref="Key"/> (tidied: numbers folded, case evened) and stored once
/// per page, however many looks see them.
/// </summary>
public sealed record PageLine(string Key, string Text, bool Typed);

/// <summary>
/// A stored line of page text: the unit search, summaries and Ask read.
/// Seen from <see cref="FirstSeenMilliseconds"/> to <see cref="LastSeenMilliseconds"/>,
/// first stored by the look <see cref="ScanId"/>.
/// </summary>
public sealed record PageChunk(
    long Id,
    string PageKey,
    string Text,
    long FirstSeenMilliseconds,
    long LastSeenMilliseconds,
    string? ScanId,
    bool Typed,
    bool UserCaused);

/// <summary>
/// One stretch of time a window was in front, from the moment it came to the
/// front until something else did, the user left, or recording stopped.
/// </summary>
public sealed record FocusRow(
    long Id,
    long StartedAtMilliseconds,
    long? EndedAtMilliseconds,
    long LastSeenMilliseconds,
    string ProcessName,
    // Null when the privacy gate kept the title out.
    string? Title,
    string? Suppressed,
    string? SuppressedDetail);

/// <summary>
/// A description written for an activity, kept so it is never paid for twice.
/// Found again by the activity's key and start, or by its text when the same
/// activity comes out of a rebuild starting elsewhere.
/// </summary>
public sealed record StoredSummary(
    string ActivityKey,
    long StartedAtMilliseconds,
    string? TextHash,
    string Label,
    string? Summary,
    string? Task,
    SummaryCheck Check,
    int FactsKept,
    int FactsDropped);

/// What one look needs from storage.
public interface IActivityStore
{
    AppProfile? GetAppProfile(string key);

    void UpsertAppProfile(AppProfile profile);

    IReadOnlyList<AppProfile> GetAppProfiles();

    PageState? GetPageState(string pageKey);

    void UpsertPageState(PageState state);

    /// The most recent stored look, any window.
    ScanFacet? GetLatestFacet();

    /// When the newest marker that ends a stretch (stop, away, lock, sleep,
    /// shutdown) was recorded; 0 if there is none. A look older than that is
    /// never extended across it.
    long GetLatestBreakMilliseconds();

    /// Recent titles of one app, newest first, so the noise in its titles
    /// (the app name, counters) can be learned.
    IReadOnlyList<string> GetRecentTitles(string processName, int limit = 40);

    /// Which of these line keys the page already holds.
    IReadOnlySet<string> GetKnownLines(string pageKey, IReadOnlyCollection<string> lineKeys);

    /// Stores a look, and the page lines it was the first to see.
    void SaveLook(ManualScanRecord look, IReadOnlyList<PageLine>? newLines = null);

    /// Marks lines the page already holds as on screen now.
    void TouchLines(string pageKey, IReadOnlyCollection<string> lineKeys, long nowMilliseconds);

    /// Marks the lines read at <paramref name="readAtMilliseconds"/> as still
    /// on screen now: a picture check found nothing had changed.
    void TouchLinesSeenAt(string pageKey, long readAtMilliseconds, long nowMilliseconds);

    /// Extends a stored look instead of saving a duplicate.
    void TouchScan(string scanId, long lastSeenMilliseconds);
}

/// What working out activities on demand reads. Nothing here is written.
public interface IActivityViewStore
{
    /// Looks in front at some point in [from, to), oldest first.
    IReadOnlyList<ScanFacet> GetFacetsBetween(long fromMilliseconds, long toMilliseconds);

    /// When the newest look that ended before <paramref name="beforeMilliseconds"/> was last seen, if any.
    long? GetLastLookEndBefore(long beforeMilliseconds);

    IReadOnlyList<ActivityMarker> GetMarkers(long fromMilliseconds, long toMilliseconds);

    IReadOnlyList<AppProfile> GetAppProfiles();

    IReadOnlyList<StoredSummary> GetSummaries(IReadOnlyCollection<string> activityKeys);

    IReadOnlyDictionary<string, ActivityTaskStatus> GetTaskStatuses(IReadOnlyCollection<string> activityIds);

    IReadOnlyDictionary<string, SessionOutcome> GetSessionOutcomes(IReadOnlyCollection<string> sessionIds);

    /// Lines of these pages on screen at some point in [from, to), oldest first.
    IReadOnlyList<PageChunk> GetPageChunks(IReadOnlyCollection<string> pageKeys, long fromMilliseconds, long toMilliseconds);
}

/// Stable ids for things worked out on demand, so a user's choice about an
/// activity or a session finds it again however often it is rebuilt.
public static class ActivityIds
{
    public static string Activity(string key, long startedAtMilliseconds) =>
        "a" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"{key}@{startedAtMilliseconds}")))[..24]
            .ToLowerInvariant();

    /// A session is named after its first look, which never changes.
    public static string Session(string firstLookId) => "s" + firstLookId;

    public static string TextHash(string text) =>
        Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..32]
            .ToLowerInvariant();
}
