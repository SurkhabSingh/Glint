using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class SessionizerTests
{
    private const long Start = 1_700_000_000_000;

    private static CaptureRow Capture(
        long offsetMs,
        string process = "Code",
        string title = "roadmap.md - Code") =>
        new($"scan-{offsetMs}", Start + offsetMs, process, title);

    private static long Quiet(long lastOffsetMs) =>
        Start + lastOffsetMs + Sessionizer.QuietTailMilliseconds;

    [Fact]
    public void NoCapturesProduceNoSessions()
    {
        Assert.Empty(Sessionizer.Cluster([], Start));
    }

    [Fact]
    public void ASingleQuietStretchBecomesOneSession()
    {
        List<CaptureRow> captures = [Capture(0), Capture(3_000), Capture(6_000)];

        var session = Assert.Single(Sessionizer.Cluster(captures, Quiet(6_000)));

        Assert.Equal(Start, session.StartedAtMilliseconds);
        Assert.Equal(Start + 6_000, session.EndedAtMilliseconds);
        Assert.Equal(3, session.ScanIds.Count);
    }

    [Fact]
    public void TitleChangesDoNotSplitASession()
    {
        // Every browser tab used to start a new session.
        List<CaptureRow> captures =
        [
            Capture(0, "chrome", "Inbox"),
            Capture(2_000, "chrome", "Pull request #12"),
            Capture(4_000, "chrome", "Docs")
        ];

        var session = Assert.Single(Sessionizer.Cluster(captures, Quiet(4_000)));

        Assert.Equal(3, session.ScanIds.Count);
    }

    [Fact]
    public void WorkSpanningTwoAppsStaysOneSession()
    {
        // Replying in Outlook and finishing in Gmail is one piece of work.
        List<CaptureRow> captures =
        [
            Capture(0, "outlook", "Re: Warranty"),
            Capture(30_000, "outlook", "Re: Warranty"),
            Capture(60_000, "chrome", "Gmail")
        ];

        var session = Assert.Single(Sessionizer.Cluster(captures, Quiet(60_000)));

        Assert.Equal(3, session.ScanIds.Count);
        // Named after where most of the time went.
        Assert.Equal("outlook", session.ProcessName);
    }

    [Fact]
    public void AQuietGapAloneDoesNotSplit()
    {
        // Reading one page produces a capture and then silence, because
        // nothing changes. By time alone that is indistinguishable from
        // leaving the room, so it must not end the session on its own.
        List<CaptureRow> captures =
        [
            Capture(0),
            Capture(Sessionizer.IdleGapMilliseconds * 2)
        ];

        var session = Assert.Single(
            Sessionizer.Cluster(captures, Quiet(Sessionizer.IdleGapMilliseconds * 2)));

        Assert.Equal(2, session.ScanIds.Count);
    }

    [Fact]
    public void AGapTheUserExplainsByLeavingSplits()
    {
        List<CaptureRow> captures =
        [
            Capture(0),
            Capture(Sessionizer.IdleGapMilliseconds * 2)
        ];
        List<ActivityMarker> markers =
        [
            new(Start + Sessionizer.IdleGapMilliseconds, "user.away")
        ];

        var sessions = Sessionizer.Cluster(
            captures,
            Quiet(Sessionizer.IdleGapMilliseconds * 2),
            markers);

        Assert.Equal(2, sessions.Count);
    }

    [Fact]
    public void StoppingRecordingSplits()
    {
        List<CaptureRow> captures =
        [
            Capture(0),
            Capture(Sessionizer.IdleGapMilliseconds * 2)
        ];
        List<ActivityMarker> markers =
        [
            new(Start + Sessionizer.IdleGapMilliseconds, "run.stopped")
        ];

        Assert.Equal(
            2,
            Sessionizer.Cluster(
                captures,
                Quiet(Sessionizer.IdleGapMilliseconds * 2),
                markers).Count);
    }

    [Fact]
    public void ComingBackDoesNotSplit()
    {
        // Only marks that end a stretch are boundaries.
        List<CaptureRow> captures =
        [
            Capture(0),
            Capture(Sessionizer.IdleGapMilliseconds * 2)
        ];
        List<ActivityMarker> markers =
        [
            new(Start + Sessionizer.IdleGapMilliseconds, "user.returned")
        ];

        Assert.Single(
            Sessionizer.Cluster(
                captures,
                Quiet(Sessionizer.IdleGapMilliseconds * 2),
                markers));
    }

    [Fact]
    public void AnUnexplainedGapStillSplitsEventually()
    {
        // The fallback for missing evidence: a crash leaves no stop marker,
        // and history recorded before markers existed has none at all.
        List<CaptureRow> captures =
        [
            Capture(0),
            Capture(Sessionizer.UnexplainedGapMilliseconds + 1)
        ];

        var sessions = Sessionizer.Cluster(
            captures,
            Quiet(Sessionizer.UnexplainedGapMilliseconds + 1));

        Assert.Equal(2, sessions.Count);
    }

    [Fact]
    public void AMarkerOutsideTheGapIsIgnored()
    {
        // A break that happened before this stretch must not split it.
        List<CaptureRow> captures = [Capture(60_000), Capture(120_000)];
        List<ActivityMarker> markers = [new(Start, "user.away")];

        Assert.Single(Sessionizer.Cluster(captures, Quiet(120_000), markers));
    }

    [Fact]
    public void AThreeHourStretchIsOneSessionHoweverLong()
    {
        // A game played for three hours, one look a minute, never idle.
        List<CaptureRow> captures = [];
        for (var minute = 0; minute <= 180; minute++)
        {
            captures.Add(Capture(minute * 60_000));
        }

        var session = Assert.Single(Sessionizer.Cluster(captures, Quiet(180 * 60_000)));
        Assert.Equal(Start + (180 * 60_000), session.EndedAtMilliseconds);
    }

    [Theory]
    [InlineData("user.locked")]
    [InlineData("system.sleep")]
    [InlineData("system.shutdown")]
    public void LockingSleepingOrShuttingDownEndsTheSitting(string kind)
    {
        List<CaptureRow> captures = [Capture(0), Capture(60_000), Capture(20 * 60_000)];
        List<ActivityMarker> markers = [new(Start + 61_000, kind)];

        Assert.Equal(2, Sessionizer.Cluster(captures, Quiet(20 * 60_000), markers).Count);
    }

    [Fact]
    public void ClosingAnAppIsNotTheEndOfTheSitting()
    {
        List<CaptureRow> captures = [Capture(0), Capture(60_000), Capture(20 * 60_000)];
        List<ActivityMarker> markers = [new(Start + 61_000, "app.closed", "p4g")];

        Assert.Single(Sessionizer.Cluster(captures, Quiet(20 * 60_000), markers));
    }

    [Fact]
    public void TheNewestStretchIsLeftAloneWhileItMayStillBeGrowing()
    {
        List<CaptureRow> captures = [Capture(0), Capture(1_000)];

        // Only a second of quiet: this work is probably still going.
        Assert.Empty(Sessionizer.Cluster(captures, Start + 2_000));

        // Once it has been quiet long enough, it seals.
        Assert.Single(Sessionizer.Cluster(captures, Quiet(1_000)));
    }

    [Fact]
    public void AnEarlierStretchSealsEvenWhileTheNewestIsStillGrowing()
    {
        List<CaptureRow> captures =
        [
            Capture(0),
            Capture(1_000),
            // The user stepped away, came back, and started something new.
            Capture(Sessionizer.IdleGapMilliseconds + 10_000)
        ];
        List<ActivityMarker> markers = [new(Start + 5_000, "user.away")];

        var session = Assert.Single(
            Sessionizer.Cluster(
                captures,
                Start + Sessionizer.IdleGapMilliseconds + 11_000,
                markers));

        Assert.Equal(2, session.ScanIds.Count);
    }

    [Fact]
    public void CapturesOutOfOrderAreSortedFirst()
    {
        List<CaptureRow> captures = [Capture(6_000), Capture(0), Capture(3_000)];

        var session = Assert.Single(Sessionizer.Cluster(captures, Quiet(6_000)));

        Assert.Equal(Start, session.StartedAtMilliseconds);
        Assert.Equal(Start + 6_000, session.EndedAtMilliseconds);
    }

    [Fact]
    public void StoppingEndsTheSittingHoweverSoonTheNextOneStarts()
    {
        const long minute = 60_000;
        var captures = new List<CaptureRow>
        {
            new("before", 0, "Code", "a.cs", 2 * minute),
            new("after", 3 * minute, "Code", "a.cs", 4 * minute)
        };

        var drafts = Sessionizer.Group(
            captures,
            60 * minute,
            [new ActivityMarker(2 * minute + 10_000, "run.stopped"), new ActivityMarker(3 * minute - 5_000, "run.started")]);

        Assert.Equal(2, drafts.Count);
    }

    [Fact]
    public void TheSittingStillInProgressIsKeptAndFlagged()
    {
        const long minute = 60_000;
        var captures = new List<CaptureRow> { new("video", 0, "zen", "Episode 3", 30 * minute) };

        var draft = Assert.Single(Sessionizer.Group(captures, (30 * minute) + 30_000));

        Assert.True(draft.StillOpen);
    }

    [Fact]
    public void ARecordExtendedByWatchingCountsUntilItWasLastSeen()
    {
        // One video record from 0 to 20 minutes, then a new capture at 22:
        // the gap is two minutes, not twenty-two, so an away marker in it
        // cannot split them, and the session ends when the record was last seen.
        const long minute = 60_000;
        var captures = new List<CaptureRow>
        {
            new("video", 0, "zen", "Episode 3", 20 * minute),
            new("chat", 22 * minute, "Discord", "@Sam", 24 * minute)
        };

        var drafts = Sessionizer.Cluster(
            captures,
            60 * minute,
            [new ActivityMarker(21 * minute, "user.away")]);

        var session = Assert.Single(drafts);
        Assert.Equal(24 * minute, session.EndedAtMilliseconds);
    }

    [Fact]
    public void AStillWatchedRecordIsNotSealedEarly()
    {
        const long minute = 60_000;
        var captures = new List<CaptureRow> { new("video", 0, "zen", "Episode 3", 30 * minute) };

        Assert.Empty(Sessionizer.Cluster(captures, (30 * minute) + 30_000));
    }
}
