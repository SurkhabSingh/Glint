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
    public void AGapLongerThanTheIdleThresholdSplits()
    {
        List<CaptureRow> captures =
        [
            Capture(0),
            Capture(Sessionizer.IdleGapMilliseconds + 1)
        ];

        var sessions = Sessionizer.Cluster(
            captures,
            Quiet(Sessionizer.IdleGapMilliseconds + 1));

        Assert.Equal(2, sessions.Count);
        Assert.Single(sessions[0].ScanIds);
        Assert.Single(sessions[1].ScanIds);
    }

    [Fact]
    public void AGapExactlyAtTheThresholdDoesNotSplit()
    {
        List<CaptureRow> captures = [Capture(0), Capture(Sessionizer.IdleGapMilliseconds)];

        var session = Assert.Single(
            Sessionizer.Cluster(captures, Quiet(Sessionizer.IdleGapMilliseconds)));

        Assert.Equal(2, session.ScanIds.Count);
    }

    [Fact]
    public void ALongStretchIsCappedIntoSeveralSessions()
    {
        // One capture a minute for two hours, never idle.
        List<CaptureRow> captures = [];
        for (var minute = 0; minute <= 120; minute++)
        {
            captures.Add(Capture(minute * 60_000));
        }

        var sessions = Sessionizer.Cluster(captures, Quiet(120 * 60_000));

        Assert.True(sessions.Count >= 2, "a two-hour stretch should be split by the cap");
        foreach (var session in sessions)
        {
            Assert.True(
                session.EndedAtMilliseconds - session.StartedAtMilliseconds
                    <= Sessionizer.MaxSessionMilliseconds,
                "no session may exceed the cap");
        }
    }

    [Fact]
    public void AnOverlongStretchIsCutAtItsWidestPause()
    {
        // Half an hour of work, a four-minute pause (short of the idle
        // threshold, so it is not a boundary on its own), then another half
        // hour. The whole thing outruns the cap and must be cut at the pause,
        // not at whatever was happening 45 minutes in.
        List<CaptureRow> captures = [];
        for (var minute = 0; minute <= 30; minute += 2)
        {
            captures.Add(Capture(minute * 60_000));
        }

        const long resumeMinute = 34;
        for (var minute = resumeMinute; minute <= 64; minute += 2)
        {
            captures.Add(Capture(minute * 60_000));
        }

        var sessions = Sessionizer.Cluster(captures, Quiet(64 * 60_000));

        Assert.Equal(2, sessions.Count);
        Assert.Equal(Start + (30 * 60_000), sessions[0].EndedAtMilliseconds);
        Assert.Equal(Start + (resumeMinute * 60_000), sessions[1].StartedAtMilliseconds);
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
            // After a long gap, new work that just started.
            Capture(Sessionizer.IdleGapMilliseconds + 10_000)
        ];

        var session = Assert.Single(
            Sessionizer.Cluster(captures, Start + Sessionizer.IdleGapMilliseconds + 11_000));

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

    [Theory]
    [InlineData(new[] { "a", "a", "b" }, "a\n---\nb")]
    [InlineData(new[] { "a", "a", "a" }, "a")]
    public void ConsecutiveRepeatsAreDroppedFromTheSummaryText(
        string[] texts,
        string expected)
    {
        Assert.Equal(expected, SessionBuilder.BuildSessionText(texts));
    }

    [Fact]
    public void BlankCapturesProduceNoSummaryText()
    {
        Assert.Equal(string.Empty, SessionBuilder.BuildSessionText(["", "   "]));
    }

    [Fact]
    public void ALongSessionIsSampledAcrossItsSpanAndStaysInBudget()
    {
        var texts = Enumerable.Range(0, 200)
            .Select(index => new string((char)('a' + (index % 26)), 500))
            .ToList();

        var built = SessionBuilder.BuildSessionText(texts, budget: 6_000);

        Assert.True(built.Length <= 6_000, $"budget exceeded: {built.Length}");
        // Sampled across the whole session rather than truncated to its start.
        Assert.Contains(new string('a', 500), built, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('b', 500), built, StringComparison.Ordinal);
    }
}
