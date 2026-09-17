using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class OutcomeRulesTests
{
    private static ActivitySession Session(
        string? reminder = null,
        string? important = null,
        SessionOutcome outcome = SessionOutcome.Unknown,
        SessionOutcomeSource source = SessionOutcomeSource.None) =>
        new(
            "session-1",
            1_000,
            2_000,
            "Discord",
            "Project chat",
            ["scan-1"],
            "Chatting",
            "The user chatted.",
            ActivitySessionStatus.Closed,
            important,
            reminder,
            Outcome: outcome,
            OutcomeSource: source);

    [Theory]
    [InlineData("Send Alex the logs before the 10 PM meeting tomorrow")]
    [InlineData("Run the app using the portable version")]
    [InlineData("meeting shifted to 9 pm now")]
    public void ARealReminderMeansSomethingWasLeftOutstanding(string reminder)
    {
        var (outcome, source) = OutcomeRules.Evaluate(Session(reminder: reminder));

        Assert.Equal(SessionOutcome.Open, outcome);
        Assert.Equal(SessionOutcomeSource.Rule, source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NONE")]
    [InlineData("n/a")]
    // The model usually declines in prose rather than with the literal NONE
    // the parser strips, and six of eleven real sessions carried exactly this.
    [InlineData("No explicit commitments, requests, deadlines, decisions, "
        + "blockers, risks, or urgent tone were found.")]
    [InlineData("Nothing outstanding was mentioned.")]
    public void ADeclinedReminderIsNotEvidence(string? reminder)
    {
        var (outcome, source) = OutcomeRules.Evaluate(Session(reminder: reminder));

        Assert.Equal(SessionOutcome.Unknown, outcome);
        // Recorded as "the rule looked and found nothing", which is distinct
        // from "not looked at yet" and stops it being re-examined forever.
        Assert.Equal(SessionOutcomeSource.Rule, source);
    }

    [Fact]
    public void ImportantSignalsAloneDoNotOpenASession()
    {
        // IMPORTANT mixes outstanding things with settled ones — a decision
        // already taken is not an open loop — so it is not treated as
        // evidence on its own.
        var (outcome, _) = OutcomeRules.Evaluate(
            Session(important: "The user decided to use a self-signed certificate."));

        Assert.Equal(SessionOutcome.Unknown, outcome);
    }

    [Fact]
    public void NothingIsEverMarkedDoneByRule()
    {
        // Work stopping looks exactly like work finishing, so no rule may
        // claim a loop is closed.
        foreach (var reminder in new string?[] { null, "NONE", "Send the logs tonight" })
        {
            var (outcome, _) = OutcomeRules.Evaluate(Session(reminder: reminder));
            Assert.NotEqual(SessionOutcome.Settled, outcome);
        }
    }

    [Fact]
    public void AUserVerdictIsNeverOverwritten()
    {
        // The session carries a reminder, which would otherwise open it.
        var settledByUser = Session(
            reminder: "Send the logs tonight",
            outcome: SessionOutcome.Settled,
            source: SessionOutcomeSource.User);

        var (outcome, source) = OutcomeRules.Evaluate(settledByUser);

        Assert.Equal(SessionOutcome.Settled, outcome);
        Assert.Equal(SessionOutcomeSource.User, source);
    }

    [Fact]
    public void ARuleVerdictCanStillBeRecomputed()
    {
        // Unlike a user's verdict, a rule's own earlier answer is not
        // protected: re-running it on a session whose reminder has gone
        // returns to unknown.
        var openedByRule = Session(
            reminder: null,
            outcome: SessionOutcome.Open,
            source: SessionOutcomeSource.Rule);

        var (outcome, source) = OutcomeRules.Evaluate(openedByRule);

        Assert.Equal(SessionOutcome.Unknown, outcome);
        Assert.Equal(SessionOutcomeSource.Rule, source);
    }

    [Theory]
    [InlineData("Send the logs", true)]
    [InlineData("No explicit commitments were found", false)]
    [InlineData("none", false)]
    [InlineData("  ", false)]
    [InlineData(null, false)]
    public void MeaningfulSeparatesContentFromDeclining(string? text, bool expected)
    {
        Assert.Equal(expected, OutcomeRules.IsMeaningful(text));
    }
}
