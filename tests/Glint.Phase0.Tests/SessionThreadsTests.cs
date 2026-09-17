using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class SessionThreadsTests
{
    private const long Start = 1_700_000_000_000;
    private const long Hour = 3_600_000;

    private static ActivitySession Session(
        string id,
        long at,
        string process,
        string? reminder,
        SessionOutcome outcome = SessionOutcome.Open,
        SessionOutcomeSource source = SessionOutcomeSource.Rule,
        string? threadId = null) =>
        new(
            id,
            at,
            at + 60_000,
            process,
            "Window",
            [$"scan-{id}"],
            "Label",
            "Summary",
            ActivitySessionStatus.Closed,
            null,
            reminder,
            Outcome: outcome,
            OutcomeSource: source,
            ThreadId: threadId);

    [Fact]
    public void TheSameConversationReturnedToLaterIsOneThread()
    {
        // The real pair this was built for: two Discord sittings the same
        // day, both about the same meeting.
        var earlier = Session(
            "a", Start, "Discord", "Job meeting tomorrow at 9 pm with unemployed job meeting");
        var later = Session(
            "b", Start + (14 * Hour), "Discord", "Jobless meeting tomorrow at 10 pm with kiyopon");

        Assert.True(SessionThreads.IsSameThread(later, earlier));
    }

    [Fact]
    public void ADifferentCommitmentInTheSameAppIsNotLinked()
    {
        // Shares only "meeting" and "tomorrow" — below the bar.
        var logs = Session(
            "a", Start, "Discord", "Send logs tonight before our meeting at 10pm tomorrow");
        var job = Session(
            "b", Start + Hour, "Discord", "Jobless meeting tomorrow at 10 pm with kiyopon");

        Assert.False(SessionThreads.IsSameThread(job, logs));
    }

    [Fact]
    public void ModerateOverlapAcrossDifferentAppsIsNotLinked()
    {
        // Measured at 0.29 against a genuinely different commitment's 0.18 —
        // too small a margin to merge on, so a different app must clear the
        // much higher near-identical bar instead.
        var discord = Session(
            "a", Start, "Discord", "Jobless meeting tomorrow at 10 pm with kiyopon");
        var opera = Session("b", Start + Hour, "opera", "meeting shifted to 9 pm now");

        Assert.False(SessionThreads.IsSameThread(opera, discord));
    }

    [Fact]
    public void TheSameReminderCapturedTwiceIsOneThreadEvenAcrossApps()
    {
        var first = Session("a", Start, "Discord", "Send Alex the deployment logs tonight");
        var second = Session(
            "b", Start + (48 * Hour), "chrome", "Send Alex the deployment logs tonight");

        Assert.True(SessionThreads.IsSameThread(second, first));
    }

    [Fact]
    public void TheSameConversationMonthsApartIsNotOneThread()
    {
        var earlier = Session("a", Start, "Discord", "Jobless meeting tomorrow at 10 pm");
        var later = Session(
            "b", Start + (60L * 24 * Hour), "Discord", "Jobless meeting tomorrow at 10 pm with kiyopon");

        Assert.False(SessionThreads.IsSameThread(later, earlier));
    }

    [Fact]
    public void SessionsWithoutReminderTextNeverLink()
    {
        var a = Session("a", Start, "Discord", null);
        var b = Session("b", Start + Hour, "Discord", null);

        Assert.False(SessionThreads.IsSameThread(b, a));
    }

    [Fact]
    public void AnEarlierSessionNeverJoinsALaterOne()
    {
        var earlier = Session("a", Start, "Discord", "Job meeting tomorrow at 9 pm");
        var later = Session("b", Start + Hour, "Discord", "Job meeting tomorrow at 9 pm");

        Assert.True(SessionThreads.IsSameThread(later, earlier));
        Assert.False(SessionThreads.IsSameThread(earlier, later));
    }

    [Fact]
    public void FindThreadPicksTheMostRecentMatch()
    {
        var oldest = Session("a", Start, "Discord", "Job meeting tomorrow at 9 pm");
        var newer = Session("b", Start + Hour, "Discord", "Job meeting tomorrow at 9 pm sharp");
        var session = Session("c", Start + (2 * Hour), "Discord", "Job meeting tomorrow at 9 pm");

        // Candidates arrive newest first, as the store returns them.
        var found = SessionThreads.FindThread(session, [newer, oldest]);

        Assert.Equal("b", found?.Id);
    }

    [Fact]
    public void StopwordsAndPunctuationDoNotCountAsOverlap()
    {
        Assert.Equal(0, SessionThreads.Similarity("to the at of", "on for with and"));
    }

    [Fact]
    public void SimilarityIsSymmetric()
    {
        const string left = "Send Alex the logs tonight";
        const string right = "Send the logs to Alex";

        Assert.Equal(
            SessionThreads.Similarity(left, right),
            SessionThreads.Similarity(right, left),
            3);
    }
}
