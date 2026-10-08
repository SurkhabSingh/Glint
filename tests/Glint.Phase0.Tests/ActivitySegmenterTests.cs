using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

/// The universal rule: only identity can start an activity, never content.
public sealed class ActivitySegmenterTests
{
    private const long Second = 1_000;
    private const long Minute = 60_000;
    private const long T0 = 1_760_000_000_000;

    [Fact]
    public void AGameEveningIsOneActivityThroughMenusGlancesAndAChat()
    {
        var facets = new List<ScanFacet>
        {
            // Launcher for a few seconds: a glance before the game.
            Facet("steamwebhelper", "Steam", 0, 8 * Second),
            // The game: its title changes with every screen; it does not matter.
            Facet("SB-Win64-Shipping", "Stellar Blade - Main Menu", 10 * Second, 4 * Minute, ActivityMode.Play),
            Facet("SB-Win64-Shipping", "Stellar Blade - Inventory", 4 * Minute, 9 * Minute, ActivityMode.Play),
            Facet("SB-Win64-Shipping", "Stellar Blade - Map", 9 * Minute, 19 * Minute, ActivityMode.Play),
            // A 12 second look at Discord.
            Facet("Discord", "#general | Friends - Discord", 19 * Minute, 19 * Minute + 12 * Second),
            Facet("SB-Win64-Shipping", "Stellar Blade", 19 * Minute + 12 * Second, 30 * Minute, ActivityMode.Play),
            // Three minutes replying to a friend.
            Facet("Discord", "@Sam - Discord", 30 * Minute, 33 * Minute),
            Facet("SB-Win64-Shipping", "Stellar Blade", 33 * Minute, 70 * Minute, ActivityMode.Play)
        };

        var drafts = Segment(facets);

        var game = Assert.Single(drafts, draft => draft.Identity.Mode == ActivityMode.Play);
        // The launcher glance leads into the game; the 12 s Discord look is
        // inside it, so the game's time runs straight through.
        Assert.Equal(T0, game.StartedAtMilliseconds);
        Assert.Equal(67 * Minute, game.ActiveMilliseconds);
        Assert.Equal(T0 + (70 * Minute), game.EndedAtMilliseconds);
        // The 12 second look is a glance inside the game, not a split.
        Assert.Contains(game.Glances, glance => glance.App == "Discord");
        Assert.Contains(drafts.SelectMany(draft => draft.Glances), glance => glance.App == "steamwebhelper");
        // The real chat is its own activity and an interruption of the game.
        var chat = Assert.Single(drafts, draft => draft.Identity.Category == ActivityCategory.Chat);
        Assert.Equal(3 * Minute, chat.ActiveMilliseconds);
        Assert.Contains(game.Events, item => item.Kind == "interrupted");
        Assert.Equal(2, game.Segments.Count);
        Assert.Equal(2, drafts.Count);
    }

    [Fact]
    public void PhotoshopSavesAndExportsAreEventsNotSwitches()
    {
        var facets = new List<ScanFacet>
        {
            Facet("Photoshop", "poster.psd @ 50% (RGB/8)", 0, 5 * Minute, ActivityMode.Make),
            Facet("Photoshop", "poster.psd @ 66.7% (RGB/8) *", 5 * Minute, 20 * Minute, ActivityMode.Make),
            Facet("Photoshop", "poster.psd @ 66.7% (RGB/8)", 20 * Minute, 25 * Minute, ActivityMode.Make, eventKind: "saved"),
            Facet("Photoshop", "logo.psd @ 100% (RGB/8)", 25 * Minute, 25 * Minute + 30 * Second, ActivityMode.Make),
            Facet("Photoshop", "poster.psd @ 66.7% (RGB/8)", 25 * Minute + 30 * Second, 38 * Minute, ActivityMode.Make),
            Facet("Photoshop", "poster.psd @ 66.7% (RGB/8)", 38 * Minute, 40 * Minute, ActivityMode.Make, eventKind: "exported", dialog: "Export As")
        };

        var drafts = Segment(facets);

        var poster = Assert.Single(drafts, draft => draft.Identity.Subject == "poster.psd");
        Assert.Contains(poster.Events, item => item.Kind == "saved");
        Assert.Contains(poster.Events, item => item.Kind == "exported" && item.Detail == "Export As");
        // 30 seconds on another file is long enough to count, and short
        // enough that the poster resumes rather than restarting.
        Assert.Contains(poster.Events, item => item.Kind == "interrupted" && item.Detail!.StartsWith("logo.psd", StringComparison.Ordinal));
        Assert.Equal(T0 + (40 * Minute), poster.EndedAtMilliseconds);
    }

    [Fact]
    public void ComingBackAfterLongerThanFiveMinutesIsANewActivity()
    {
        var facets = new List<ScanFacet>
        {
            Facet("osu!", "osu!", 0, 10 * Minute, ActivityMode.Play),
            Facet("Code", "Program.cs - glint - Visual Studio Code", 10 * Minute, 30 * Minute),
            Facet("osu!", "osu!", 30 * Minute, 40 * Minute, ActivityMode.Play)
        };

        var drafts = Segment(facets);

        Assert.Equal(3, drafts.Count);
        Assert.Equal(2, drafts.Count(draft => draft.Identity.Mode == ActivityMode.Play));
    }

    [Fact]
    public void ASessionOfOnlyQuickLooksStillHasActivities()
    {
        var facets = new List<ScanFacet>
        {
            Facet("Discord", "@Sam - Discord", 0, 5 * Second),
            Facet("Code", "Program.cs - glint - Visual Studio Code", 5 * Second, 15 * Second)
        };

        Assert.Equal(2, Segment(facets).Count);
    }

    [Fact]
    public void QuietGapsBetweenLooksBelongToTheEarlierOne()
    {
        // A page read for a minute without changes stores one look, then the
        // next look comes from the next window.
        var facets = new List<ScanFacet>
        {
            Facet("Code", "Program.cs - glint - Visual Studio Code", 0, 0),
            Facet("Discord", "@Sam - Discord", 70 * Second, 3 * Minute)
        };

        var code = Segment(facets).First();

        Assert.Equal(70 * Second, code.ActiveMilliseconds);
    }

    [Fact]
    public void WhereASessionEndsIsNeverAGlance()
    {
        // Episode 2 for four minutes, then Episode 3 had just begun when
        // recording paused: Episode 3 is its own activity, not a glance.
        var facets = new List<ScanFacet>
        {
            Facet("zen", "Episode 2 - AnimeX", 0, 3 * Minute + 37 * Second, ActivityMode.Watch),
            Facet("zen", "Episode 3 - AnimeX", 3 * Minute + 37 * Second, 3 * Minute + 37 * Second, ActivityMode.Watch)
        };

        var drafts = Segment(facets, endings: [T0 + (3 * Minute) + (40 * Second)]);

        Assert.Equal(2, drafts.Count);
        Assert.Empty(drafts[0].Glances);
        // The stop three seconds later closes it.
        Assert.Equal(3 * Second, drafts[1].ActiveMilliseconds);
    }

    [Fact]
    public void AStopLongAfterTheLastLookIsNotCounted()
    {
        var facets = new List<ScanFacet> { Facet("osu!", "osu!", 0, 10 * Minute, ActivityMode.Play) };

        var draft = Assert.Single(Segment(facets, endings: [T0 + (25 * Minute)]));

        Assert.Equal(10 * Minute, draft.ActiveMilliseconds);
    }

    [Fact]
    public void HistoryFromBeforeActivitiesStillCountsQuietReading()
    {
        // Old captures were stored only when the screen changed and carry no
        // "last seen": four quiet minutes on a page left one capture.
        var facets = new List<ScanFacet>
        {
            Facet("Code", "Program.cs - glint - Visual Studio Code", 0, 0, change: null),
            Facet("Discord", "@Sam - Discord", 4 * Minute, 4 * Minute, change: null)
        };

        Assert.Equal(4 * Minute, Segment(facets).First().ActiveMilliseconds);
    }

    private static IReadOnlyList<ActivityDraft> Segment(IReadOnlyList<ScanFacet> facets, IReadOnlyList<long>? endings = null) =>
        ActivitySegmenter.Segment(facets, facet =>
        {
            var known = ActivityCatalog.ForProcess(facet.ProcessName)
                ?? (facet.Mode ?? ActivityMode.Read, ActivityCategory.Other);
            return ActivityIdentityResolver.Compose(
                facet.ProcessName,
                facet.ProcessName,
                facet.WindowTitle,
                null,
                facet.Mode ?? known.Mode,
                known.Category,
                null);
        }, endings);

    private static ScanFacet Facet(
        string process,
        string title,
        long start,
        long lastSeen,
        ActivityMode? mode = null,
        string? eventKind = null,
        string? dialog = null,
        CaptureChange? change = CaptureChange.Presence) =>
        new(
            Guid.NewGuid().ToString("N"),
            T0 + start,
            T0 + lastSeen,
            process,
            title,
            null,
            null,
            null,
            null,
            null,
            mode,
            null,
            change,
            TitleNormalizer.IsUnsaved(title),
            dialog,
            eventKind,
            false);
}
