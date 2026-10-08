using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

/// Ask answers from activities, exactly where it matters.
public sealed class AskTests
{
    // Thursday, October 8, 2026, 6:00 PM, at a fixed offset so tests do not
    // depend on the machine's time zone.
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 18, 0, 0, TimeSpan.FromHours(5.5));
    private static readonly DateTimeOffset Today = new(2026, 10, 8, 0, 0, 0, TimeSpan.FromHours(5.5));

    [Theory]
    [InlineData("what did I do today?", "2026-10-08T00:00", "2026-10-09T00:00")]
    [InlineData("summarize my work on this day", "2026-10-08T00:00", "2026-10-09T00:00")]
    [InlineData("what did I do yesterday", "2026-10-07T00:00", "2026-10-08T00:00")]
    [InlineData("what about the day before yesterday", "2026-10-06T00:00", "2026-10-07T00:00")]
    [InlineData("what did I watch this morning", "2026-10-08T05:00", "2026-10-08T12:00")]
    [InlineData("what did I do last night", "2026-10-07T18:00", "2026-10-08T04:00")]
    [InlineData("what was I doing around 5 pm", "2026-10-08T16:30", "2026-10-08T18:00")]
    [InlineData("what did I do on monday", "2026-10-05T00:00", "2026-10-06T00:00")]
    [InlineData("what did I read on Oct 3", "2026-10-03T00:00", "2026-10-04T00:00")]
    [InlineData("what did I do in the last 2 hours", "2026-10-08T16:00", "2026-10-08T18:01")]
    [InlineData("what am I doing right now", "2026-10-08T17:45", "2026-10-08T18:01")]
    [InlineData("how was this week", "2026-10-05T00:00", "2026-10-08T18:01")]
    public void PeriodsComeFromTheWordsUsed(string question, string from, string to)
    {
        var intent = AskIntentParser.Parse(question, "all", null, Now, hasHistory: false);

        Assert.Equal(AskKind.Activity, intent.Kind);
        Assert.Equal(At(from), intent.Period.FromMilliseconds);
        Assert.Equal(At(to), intent.Period.ToMilliseconds);
    }

    [Fact]
    public void ThisDayMeansThePickedDayWhenOneIsPicked()
    {
        var intent = AskIntentParser.Parse("summarize this day", "day", "2026-09-18", Now, hasHistory: false);

        Assert.Equal(At("2026-09-18T00:00"), intent.Period.FromMilliseconds);
        Assert.True(intent.WantsSummary);
    }

    [Theory]
    [InlineData("hi", AskKind.SmallTalk)]
    [InlineData("thanks!", AskKind.SmallTalk)]
    [InlineData("what can you do?", AskKind.SmallTalk)]
    [InlineData("what is the capital of France?", AskKind.General)]
    [InlineData("explain closures in javascript", AskKind.General)]
    [InlineData("what did I watch?", AskKind.Activity)]
    [InlineData("did I play any games", AskKind.Activity)]
    [InlineData("how long was I on discord", AskKind.Activity)]
    public void MessagesAreRoutedByWhatTheyAreAbout(string question, AskKind kind)
    {
        Assert.Equal(kind, AskIntentParser.Parse(question, "all", null, Now, hasHistory: false).Kind);
    }

    [Fact]
    public void KindsAreReadFromTheQuestion()
    {
        var listen = AskIntentParser.Parse("what did I listen to today", "all", null, Now, false);
        var watch = AskIntentParser.Parse("what anime did I watch", "all", null, Now, false);

        Assert.Contains(new AskKindFilter(ActivityMode.Watch, ActivityCategory.Music), listen.Kinds);
        Assert.DoesNotContain(new AskKindFilter(ActivityMode.Watch, ActivityCategory.Video), listen.Kinds);
        Assert.Contains(new AskKindFilter(ActivityMode.Watch, ActivityCategory.Video), watch.Kinds);
    }

    [Fact]
    public async Task WithoutAModelASummaryIsGlintsOwnList()
    {
        var answer = await new AskEngine(new FakeSource(Afternoon()), null, () => Now)
            .AnswerAsync(new AskRequest("Summarize my work on this day.", "all"));

        Assert.Equal("facts", answer.Source);
        Assert.Equal(
            """
            Here's how today went:

            • Navigating anime episode options: 6 min in Zen, from 4:22 PM
            • Watched Cyberpunk: Edgerunners on animex.one: 2 episodes, 25 min
               ◦ Episode 2: 4 min, from 4:47 PM
               ◦ Episode 3: 21 min, from 4:51 PM
            • Chat: Planned Saturday match with Sam: 6 min in Discord, from 5:20 PM

            That's 37 min in all, mostly watching video (25 min).
            """.ReplaceLineEndings("\n"),
            answer.Answer);
        Assert.Equal(4, answer.Citations.Count);
    }

    [Fact]
    public async Task AListGetsANaturalOpeningFromTheModelAndNoBrackets()
    {
        var model = new FakeModel("Mostly anime today, plus a quick chat with Sam about Saturday [4].\n\n• Navigating anime episode options: 6 min");

        var answer = await Engine(model, Afternoon()).AnswerAsync(new AskRequest("Summarize my day", "all"));

        Assert.Equal("model", answer.Source);
        Assert.StartsWith("Mostly anime today, plus a quick chat with Sam about Saturday.\n\n• Navigating", answer.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain("[", answer.Answer, StringComparison.Ordinal);
        // The opening prompt carries highlights and totals, never the list.
        Assert.DoesNotContain("• ", model.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("Cyberpunk: Edgerunners (2 episodes, 25 min)", model.LastPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskingForAKindThatDidNotHappenSaysSoPlainly()
    {
        var model = new FakeModel("unused");
        var answer = await Engine(model, Afternoon()).AnswerAsync(new AskRequest("Did I play any games today?", "all"));

        Assert.Equal(0, model.Calls);
        Assert.StartsWith("I didn't record you playing games today. Here's what I did record:", answer.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InventedFactsAreRemovedFromTheOpening()
    {
        var model = new FakeModel("You watched two episodes, 25 min in all. You also called Priya at 9 pm.");

        var answer = await Engine(model, Afternoon()).AnswerAsync(new AskRequest("What did I watch around 5 pm?", "all"));

        Assert.Equal(1, answer.Removed);
        Assert.DoesNotContain("Priya", answer.Answer, StringComparison.Ordinal);
        Assert.StartsWith("You watched two episodes, 25 min in all.\n\n• Cyberpunk: Edgerunners on animex.one: 2 episodes, 25 min", answer.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASpecificQuestionGetsADirectCheckedAnswer()
    {
        var model = new FakeModel("You started episode 3 at 4:51 PM [2]. Arjun joined you at 9 pm.");

        var answer = await Engine(model, Afternoon()).AnswerAsync(new AskRequest("When did I start episode 3?", "all"));

        Assert.Equal("You started episode 3 at 4:51 PM.", answer.Answer);
        Assert.Equal(1, answer.Removed);
        var cited = Assert.Single(answer.Citations);
        Assert.Contains("Episode 3", cited.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenNothingTheModelSaidHoldsUpGlintAnswersFromTheFacts()
    {
        var model = new FakeModel("You spent the evening with Arjun at the Colosseum.");

        var answer = await Engine(model, Afternoon()).AnswerAsync(new AskRequest("Who did I meet this evening?", "all"));

        Assert.Equal("facts", answer.Source);
        Assert.Equal(1, answer.Removed);
        Assert.Contains("Episode 3", answer.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DurationsAreGivenToTheModelAlreadyAddedUp()
    {
        var model = new FakeModel("You spent 25 min watching anime today [1][2].");

        var answer = await Engine(model, Afternoon()).AnswerAsync(new AskRequest("How long did I watch anime today?", "all"));

        Assert.Contains("Of the kind asked about: 2 activities, 25 min in total.", model.LastPrompt, StringComparison.Ordinal);
        Assert.Equal("You spent 25 min watching anime today.", answer.Answer);
        Assert.Equal(0, answer.Removed);
    }

    [Fact]
    public async Task ComparisonsAreWorkedOutBeforeTheModelAnswers()
    {
        var model = new FakeModel("Episode 3 was longer, at 21 min [2].");

        var answer = await Engine(model, Afternoon()).AnswerAsync(new AskRequest("Which episode was longer today?", "all"));

        Assert.Contains("Longest: Cyberpunk: Edgerunners Episode 3 English Sub/Dub (21 min).", model.LastPrompt, StringComparison.Ordinal);
        Assert.Equal("Episode 3 was longer, at 21 min.", answer.Answer);
    }

    [Fact]
    public async Task SmallTalkInsideAQuestionIsAlwaysAnswered()
    {
        // The model skipped the "how are you"; Glint answers it anyway.
        var model = new FakeModel("You watched two episodes of Cyberpunk: Edgerunners, 25 min in all.");

        var answer = await Engine(model, Afternoon()).AnswerAsync(new AskRequest("What did i watch and how long? Also how are you", "all"));

        Assert.StartsWith("I'm doing well, thanks for asking! You watched two episodes", answer.Answer, StringComparison.Ordinal);
        Assert.Contains("◦ Episode 3: 21 min", answer.Answer, StringComparison.Ordinal);
        Assert.Contains("They also asked how you are", model.LastPrompt, StringComparison.Ordinal);
        // "how are you" is not looked up as a keyword.
        Assert.DoesNotContain("Nothing recorded mentions", model.LastPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlySmallTalkIsSmallTalkEvenWithToday()
    {
        var model = new FakeModel("I'm doing well, thanks! How about you?");

        var answer = await Engine(model, Afternoon()).AnswerAsync(new AskRequest("how are you doing today?", "all"));

        Assert.Equal("smalltalk", answer.Kind);
        Assert.DoesNotContain("Cyberpunk", model.LastPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatSittingsAreOneLine()
    {
        var activities = Afternoon();
        activities.Add(Activity("a5", "Watched Cyberpunk: Edgerunners Episode 4 English Sub/Dub", ActivityMode.Watch, ActivityCategory.Video, 17, 30, 17, 36, subject: "Cyberpunk: Edgerunners Episode 4 English Sub/Dub", site: "animex.one"));
        activities.Add(Activity("a6", "Watched Cyberpunk: Edgerunners Episode 4 English Sub/Dub", ActivityMode.Watch, ActivityCategory.Video, 17, 40, 17, 46, subject: "Cyberpunk: Edgerunners Episode 4 English Sub/Dub", site: "animex.one"));

        var answer = await new AskEngine(new FakeSource(activities), null, () => Now)
            .AnswerAsync(new AskRequest("What did I watch today?", "all"));

        Assert.Contains("• Cyberpunk: Edgerunners on animex.one: 3 episodes, 37 min", answer.Answer, StringComparison.Ordinal);
        Assert.Contains("◦ Episode 4: 12 min (two sittings)", answer.Answer, StringComparison.Ordinal);
        Assert.Single(answer.Answer.Split('\n'), line => line.Contains("Episode 4", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GeneralQuestionsNeverSeeTheLog()
    {
        var model = new FakeModel("Paris.");

        var answer = await Engine(model, Afternoon()).AnswerAsync(new AskRequest("What is the capital of France?", "all"));

        Assert.Equal("general", answer.Kind);
        Assert.Equal("Paris.", answer.Answer);
        Assert.DoesNotContain("NOTES", model.LastPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Cyberpunk", model.LastPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SmallTalkWorksWithoutAModel()
    {
        var answer = await new AskEngine(new FakeSource(Afternoon()), null, () => Now).AnswerAsync(new AskRequest("hi"));

        Assert.Equal("smalltalk", answer.Kind);
        Assert.Contains("what you watched, read, played", answer.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFollowUpKeepsThePeriodTheConversationWasAbout()
    {
        var model = new FakeModel("That was 21 min [1].");
        var history = new[]
        {
            new AskTurn("user", "What did I watch around 5 pm today?"),
            new AskTurn("agent", "You watched Cyberpunk: Edgerunners Episode 3 English Sub/Dub.")
        };

        var answer = await Engine(model, Afternoon()).AnswerAsync(new AskRequest("and how long was that?", "all", null, history));

        Assert.StartsWith("around 5:00 PM", answer.Period, StringComparison.Ordinal);
        Assert.Contains("Conversation so far", model.LastPrompt, StringComparison.Ordinal);
        Assert.Equal("That was 21 min.", answer.Answer);
    }

    [Fact]
    public async Task WhatIsBeingRecordedRightNowCounts()
    {
        var live = Activity("live", "Watched Cyberpunk: Edgerunners Episode 4", ActivityMode.Watch, ActivityCategory.Video, 17, 50, 18, 0, subject: "Cyberpunk: Edgerunners Episode 4");
        var answer = await new AskEngine(new FakeSource([], [live]), null, () => Now)
            .AnswerAsync(new AskRequest("what am I doing right now?", "all"));

        Assert.Contains("Episode 4", answer.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingRecordedPointsToTheLatestThingThereIs()
    {
        var answer = await Engine(new FakeModel("unused"), Afternoon()).AnswerAsync(new AskRequest("what did I do yesterday?", "all"));

        Assert.StartsWith("I don't have anything recorded yesterday.", answer.Answer, StringComparison.Ordinal);
        Assert.Contains("The most recent thing I have is", answer.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public void SpelledOutNumbersAreCheckedLikeDigits()
    {
        var (text, removed) = SummaryVerifier.FilterAnswer(
            "You did fourteen things. It took twenty-six minutes. You watched for forty minutes.",
            "14 things, 26 min in total");

        Assert.Equal(1, removed);
        Assert.Equal("You did fourteen things. It took twenty-six minutes.", text);
    }

    [Fact]
    public void AnswerCheckingKeepsBulletsAndOrdinaryOpenings()
    {
        var (text, removed) = SummaryVerifier.FilterAnswer(
            "Here's what you did:\n- You watched Episode 3 [2].\n- You met Arjun for lunch.\nOverall, mostly video.",
            "[2] 4:51 PM–5:11 PM (21 min): You watched Episode 3 on animex.one.");

        Assert.Equal(1, removed);
        Assert.Equal("Here's what you did:\n- You watched Episode 3 [2].\nOverall, mostly video.", text);
    }

    private static AskEngine Engine(FakeModel model, IReadOnlyList<ActivityRecord> activities) =>
        new(new FakeSource(activities), model, () => Now);

    private static List<ActivityRecord> Afternoon() =>
    [
        Activity("a1", "Navigating anime episode options", ActivityMode.Read, ActivityCategory.Browsing, 16, 22, 16, 28, app: "Zen"),
        Activity("a2", "Watched Cyberpunk: Edgerunners Episode 2 English Sub/Dub", ActivityMode.Watch, ActivityCategory.Video, 16, 47, 16, 51, subject: "Cyberpunk: Edgerunners Episode 2 English Sub/Dub", site: "animex.one"),
        Activity("a3", "Watched Cyberpunk: Edgerunners Episode 3 English Sub/Dub", ActivityMode.Watch, ActivityCategory.Video, 16, 51, 17, 12, subject: "Cyberpunk: Edgerunners Episode 3 English Sub/Dub", site: "animex.one"),
        Activity("a4", "Planned Saturday match with Sam", ActivityMode.Read, ActivityCategory.Chat, 17, 20, 17, 26, app: "Discord", subject: "Discord")
    ];

    private static ActivityRecord Activity(
        string id,
        string label,
        ActivityMode mode,
        ActivityCategory category,
        int startHour,
        int startMinute,
        int endHour,
        int endMinute,
        string app = "Zen",
        string? subject = null,
        string? site = null)
    {
        var start = Today.AddHours(startHour).AddMinutes(startMinute).ToUnixTimeMilliseconds();
        var end = Today.AddHours(endHour).AddMinutes(endMinute).ToUnixTimeMilliseconds();
        return new ActivityRecord(
            id, "s1", $"{app}|{site}|{subject ?? label}", app, site, subject ?? label, mode, category,
            start, end, end - start,
            [new ActivitySegment(start, end)], [], [], [], [],
            label, null, null, ActivityTaskStatus.None, false, SummaryCheck.Rule, 0, 0);
    }

    private static long At(string local) =>
        new DateTimeOffset(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromHours(5.5)).ToUnixTimeMilliseconds();

    private sealed class FakeSource(IReadOnlyList<ActivityRecord> stored, IReadOnlyList<ActivityRecord>? live = null) : IAskSource
    {
        public IReadOnlyList<ActivityRecord> GetActivitiesBetween(long fromMilliseconds, long toMilliseconds) =>
            stored.Where(activity => activity.StartedAtMilliseconds < toMilliseconds && activity.EndedAtMilliseconds >= fromMilliseconds).ToList();

        public IReadOnlyList<ActivityRecord> GetLiveActivities() => live ?? [];

        public ActivityRecord? GetLatestActivity() => stored.OrderBy(activity => activity.StartedAtMilliseconds).LastOrDefault();
    }

    private sealed class FakeModel(string reply) : ILiteRtGenerator
    {
        public int Calls { get; private set; }

        public string LastPrompt { get; private set; } = string.Empty;

        public Task<LiteRtGenerationResult> GenerateAsync(LiteRtGenerationRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastPrompt = request.Prompt;
            return Task.FromResult(new LiteRtGenerationResult(reply, TimeSpan.Zero, TimeSpan.Zero));
        }
    }
}
