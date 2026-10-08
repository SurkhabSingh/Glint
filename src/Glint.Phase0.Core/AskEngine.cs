using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Glint.Phase0.Core;

public sealed record AskTurn(string Role, string Text);

public sealed record AskRequest(
    string Question,
    string? Scope = null,
    string? Day = null,
    IReadOnlyList<AskTurn>? History = null);

/// One activity an answer drew on, for the evidence list under it.
public sealed record AskCitation(
    int Number,
    string Id,
    string Label,
    ActivityMode Mode,
    ActivityCategory Category,
    long StartedAtMilliseconds,
    long EndedAtMilliseconds,
    long ActiveMilliseconds,
    string App,
    string? Site);

/// <param name="Source">"model" when the local model wrote any of it, "facts" when Glint wrote all of it.</param>
/// <param name="Removed">Sentences dropped because the activity record did not support them.</param>
public sealed record AskResponse(
    string Answer,
    IReadOnlyList<AskCitation> Citations,
    int ScopedCount,
    string Kind,
    string Period,
    string Source,
    int Removed);

/// Where Ask reads activities from.
public interface IAskSource
{
    IReadOnlyList<ActivityRecord> GetActivitiesBetween(long fromMilliseconds, long toMilliseconds);

    /// What is being recorded right now and not yet sealed into a session.
    IReadOnlyList<ActivityRecord> GetLiveActivities();

    ActivityRecord? GetLatestActivity();
}

/// <summary>
/// Answers anything the user types: questions about what they did, from
/// their activities, and everything else like an ordinary assistant.
/// </summary>
/// <remarks>
/// The work is split by what each side is good at. Glint does everything that
/// must be exact: which period is meant, which activities fall in it, every
/// time, total and comparison, and the list itself, grouped so a series reads
/// as one show and repeat sittings as one line. The local model does the
/// talking: the natural opening of a list, a direct answer to a specific
/// question, and any small talk in the message ("how are you"). Whatever the
/// model writes is checked against the record afterwards; a sentence naming
/// something that is not there is removed, and when nothing survives Glint's
/// own wording stands.
/// </remarks>
public sealed partial class AskEngine
{
    /// Characters of activity notes in one prompt, inside the model's
    /// fast-input budget with the instructions and recent turns added.
    internal const int NotesBudget = 2_800;

    private const int HistoryBudget = 900;
    private const int MaxCitations = 12;

    private readonly IAskSource _source;
    private readonly ILiteRtGenerator? _generator;
    private readonly Func<DateTimeOffset> _clock;

    public AskEngine(IAskSource source, ILiteRtGenerator? generator, Func<DateTimeOffset>? clock = null)
    {
        _source = source;
        _generator = generator;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public async Task<AskResponse> AnswerAsync(AskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var question = request.Question.Trim();
        var history = request.History ?? [];
        var now = _clock();
        // Small talk inside a question ("...also how are you") is answered,
        // but it must not be read as part of what to look up.
        var chitChat = ChitChat(question);
        var lookup = chitChat is null
            ? question
            : ChitChatRegex().Replace(question, " ").Trim(' ', ',', '.', '!', '?', ';', ':');
        var pureSmallTalk = chitChat is not null && Words(lookup).Count(word => !FillerWords.Contains(word)) == 0;
        if (Words(lookup).Count < 2)
        {
            lookup = question;
        }

        var intent = AskIntentParser.Parse(lookup, request.Scope, request.Day, now, history.Count > 0);
        if (pureSmallTalk)
        {
            intent = intent with { Kind = AskKind.SmallTalk };
        }
        if (intent.Kind == AskKind.Activity && !intent.PeriodFromQuestion)
        {
            // "And how long was that?" is about the period the conversation
            // was already on, not the scope picker's.
            var earlier = history
                .Where(turn => turn.Role == "user")
                .Reverse()
                .Select(turn => AskIntentParser.PeriodFromQuestion(turn.Text.ToLowerInvariant(), now))
                .FirstOrDefault(found => found is not null);
            if (earlier is not null)
            {
                intent = intent with { Period = earlier, PeriodFromQuestion = true };
            }
        }

        if (intent.Kind != AskKind.Activity)
        {
            return await AnswerGeneralAsync(question, history, intent, cancellationToken).ConfigureAwait(false);
        }

        var period = intent.Period;
        var greeting = chitChat is null ? string.Empty : ChitChatReply(chitChat) + " ";
        var activities = Collect(period);
        if (activities.Count == 0)
        {
            return Reply(greeting + NothingRecorded(period), [], 0, intent, "facts", 0);
        }

        var ofKind = intent.Kinds.Count == 0
            ? activities
            : activities.Where(activity => MatchesKind(activity, intent)).ToList();
        if (ofKind.Count == 0)
        {
            var everything = Group(activities, period);
            var text = $"{greeting}I didn't record you {KindPhrase(intent)} {PeriodPhrase(period)}. Here's what I did record:\n\n{RenderList(everything, period, withTimes: true)}";
            return Reply(text.Trim(), Cite(everything), activities.Count, intent, "facts", 0);
        }

        var items = Group(ofKind, period);
        // A list when the message asks for one ("what did I watch", "summarize
        // my day"); a direct answer when it asks something specific.
        var wantsList = intent.WantsSummary
            || (intent.Keywords.Count == 0 && ListQuestionRegex().IsMatch(lookup))
            || (intent.Keywords.Count == 0 && intent.Kinds.Count > 0 && items.Count >= 3);
        if (wantsList && items.Sum(block => block.Items.Count) >= 2)
        {
            return await AnswerWithListAsync(question, history, intent, chitChat, greeting, items, activities, ofKind, cancellationToken).ConfigureAwait(false);
        }

        return await AnswerInProseAsync(question, history, now, intent, chitChat, greeting, items, activities, ofKind, cancellationToken).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Lists: Glint's list, the model's opening
    // -----------------------------------------------------------------------

    private async Task<AskResponse> AnswerWithListAsync(
        string question,
        IReadOnlyList<AskTurn> history,
        AskIntent intent,
        string? chitChat,
        string greeting,
        IReadOnlyList<Block> blocks,
        IReadOnlyList<ActivityRecord> activities,
        IReadOnlyList<ActivityRecord> ofKind,
        CancellationToken cancellationToken)
    {
        var period = intent.Period;
        var list = RenderList(blocks, period, withTimes: intent.WantsSummary);
        var facts = ListFacts(blocks, ofKind, period, question, intent);
        var closing = intent.WantsSummary ? Closing(activities, period) : string.Empty;
        var fallbackOpening = greeting + ListIntro(intent, period);

        var opening = fallbackOpening;
        var removed = 0;
        var source = "facts";
        if (_generator is not null)
        {
            try
            {
                var raw = (await _generator.GenerateAsync(
                        new LiteRtGenerationRequest(BuildOpeningPrompt(question, history, chitChat, Highlights(blocks), facts), Temperature: 0.5, Seed: 1),
                        TimeSpan.FromMinutes(5),
                        cancellationToken)
                    .ConfigureAwait(false)).Text;
                Diagnose("opening", raw);
                var evidence = string.Join("\n", list, facts, Highlights(blocks), period.Label, question);
                var (checkedOpening, dropped) = SummaryVerifier.FilterAnswer(StripCitations(Clean(raw)), evidence);
                removed = dropped;
                checkedOpening = Opening(checkedOpening, Highlights(blocks), facts);
                if (checkedOpening.Length > 0)
                {
                    opening = checkedOpening;
                    source = "model";
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // The facts stand on their own.
            }
        }

        opening = EnsureChitChat(opening, chitChat);
        var answer = string.Join("\n\n", new[] { opening, list, closing }.Where(part => part.Length > 0));
        return Reply(answer, Cite(blocks), activities.Count, intent, source, removed);
    }

    /// <summary>
    /// The prompt for a list's opening. The model is not shown the list
    /// itself, only its highlights and totals: shown a list, a small model
    /// copies it back instead of summing it up.
    /// </summary>
    internal static string BuildOpeningPrompt(
        string question,
        IReadOnlyList<AskTurn> history,
        string? chitChat,
        string highlights,
        string facts)
    {
        var smallTalk = chitChat is null ? string.Empty : ChitChatInstruction(chitChat) + "\n";
        return
            $"""
            You are Glint, a warm, easygoing assistant built into the user's computer. You remember what they did on it.
            {Conversation(history)}The user wrote: "{question}"
            {smallTalk}
            Highlights: {highlights}
            Facts: {facts}

            Glint will list everything in detail right after your words. Write only the opening: one or two short, natural sentences that sum it up the way a friend would, using the highlights and facts. If the message asks something a list would not answer directly, like which one was longer, answer it here. Sound like a person, not a report. Do not restate their question, do not make a list, write numbers as digits, and mention nothing beyond the highlights and facts.
            Opening:
            """;
    }

    /// The few things that stand out: the biggest blocks, with their time.
    private static string Highlights(IReadOnlyList<Block> blocks) =>
        string.Join(
            "; ",
            blocks
                .OrderByDescending(block => block.Minutes)
                .Take(3)
                .Select(block => block.Series is not null
                    ? $"{block.Series} ({block.Items.Count} episodes, {MinutesText(block.Minutes)})"
                    : $"{block.Items[0].Title} ({DoingOf(block.Items[0].Mode, block.Items[0].Category)}, {MinutesText(block.Minutes)})"));

    /// What the opening may say: totals, counts and comparisons, all
    /// computed here so the model never adds anything up itself.
    private static string ListFacts(
        IReadOnlyList<Block> blocks,
        IReadOnlyList<ActivityRecord> ofKind,
        AskPeriod period,
        string question,
        AskIntent intent)
    {
        var items = blocks.SelectMany(block => block.Items).ToList();
        var builder = new StringBuilder();
        builder.Append(items.Count).Append(" thing").Append(items.Count == 1 ? string.Empty : "s")
            .Append(", ").Append(MinutesText(items.Sum(item => item.Minutes))).Append(" in total ")
            .Append(PeriodPhrase(period)).Append('.');
        foreach (var series in blocks.Where(block => block.Series is not null))
        {
            builder.Append(' ').Append(series.Series).Append(": ").Append(series.Items.Count)
                .Append(" episodes, ").Append(MinutesText(series.Minutes)).Append('.');
        }

        if (ComparisonRegex().IsMatch(question) && items.Count >= 2)
        {
            var ranked = items.OrderByDescending(item => item.Minutes).ToList();
            builder.Append(" Longest: ").Append(ranked[0].Title).Append(" (").Append(MinutesText(ranked[0].Minutes)).Append(").")
                .Append(" Shortest: ").Append(ranked[^1].Title).Append(" (").Append(MinutesText(ranked[^1].Minutes)).Append(").");
        }

        if (intent.AboutWork)
        {
            var work = ofKind.Where(IsWork).ToList();
            builder.Append(work.Count == 0
                ? " No work (coding, documents, email, chat, studying or editing) was recorded."
                : $" Work took {MinutesText(TotalMinutes(work, period))}.");
        }

        return builder.ToString();
    }

    private static string ListIntro(AskIntent intent, AskPeriod period)
    {
        var when = PeriodPhrase(period);
        if (intent.AboutWork)
        {
            return $"Here's how {when} went:";
        }

        return intent.Kinds.Count > 0
            ? $"Here's what you {KindVerb(intent)} {when}:"
            : $"Here's what you got up to {when}:";
    }

    private static string Closing(IReadOnlyList<ActivityRecord> activities, AskPeriod period)
    {
        var top = activities
            .GroupBy(activity => DoingOf(activity.Mode, activity.Category))
            .Select(group => (Doing: group.Key, Minutes: TotalMinutes(group, period)))
            .OrderByDescending(entry => entry.Minutes)
            .ToList();
        var total = MinutesText(TotalMinutes(activities, period));
        return top.Count > 1 && top[0].Minutes > 0
            ? $"That's {total} in all, mostly {top[0].Doing} ({MinutesText(top[0].Minutes)})."
            : $"That's {total} in all.";
    }

    // -----------------------------------------------------------------------
    // Specific questions: the model answers, Glint checks
    // -----------------------------------------------------------------------

    private async Task<AskResponse> AnswerInProseAsync(
        string question,
        IReadOnlyList<AskTurn> history,
        DateTimeOffset now,
        AskIntent intent,
        string? chitChat,
        string greeting,
        IReadOnlyList<Block> blocks,
        IReadOnlyList<ActivityRecord> activities,
        IReadOnlyList<ActivityRecord> ofKind,
        CancellationToken cancellationToken)
    {
        var period = intent.Period;
        var relevant = Relevance(ofKind, intent.Keywords);
        var (chosen, notListed) = Select(ofKind, relevant, period);
        var notes = BuildNotes(chosen, ofKind, activities, notListed, period, intent, relevant)
            + Comparisons(question, chosen, period);
        var fallback = (greeting + FactsSentence(blocks, period, intent)).Trim();

        if (_generator is null)
        {
            return Reply(fallback, Cite(blocks), activities.Count, intent, "facts", 0);
        }

        string raw;
        try
        {
            raw = (await _generator.GenerateAsync(
                    new LiteRtGenerationRequest(BuildProsePrompt(question, history, now, period, notes, intent, chitChat), Temperature: history.Count > 0 ? 0.5 : 0.3, Seed: 1),
                    TimeSpan.FromMinutes(5),
                    cancellationToken)
                .ConfigureAwait(false)).Text;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Reply(fallback, Cite(blocks), activities.Count, intent, "facts", 0);
        }

        Diagnose("answer", raw);
        var evidence = string.Join(
            "\n",
            notes,
            period.Label,
            now.ToString("dddd MMMM d yyyy h:mm tt", CultureInfo.InvariantCulture),
            question,
            string.Join("\n", history.Where(turn => turn.Role == "user").Select(turn => turn.Text)));
        var (answer, removed) = SummaryVerifier.FilterAnswer(StripCitations(Clean(raw)), evidence);
        if (answer.Length == 0)
        {
            return Reply(fallback, Cite(blocks), activities.Count, intent, "facts", removed);
        }

        answer = EnsureChitChat(answer, chitChat);

        // Evidence shown is what the answer talked about, else what matched
        // the question. An answer that found nothing shows nothing.
        var mentioned = chosen.Where(activity => Mentions(answer, activity)).ToList();
        var cited = mentioned.Count > 0 ? mentioned : chosen.Where(activity => relevant.Contains(activity.Id)).ToList();
        return Reply(answer, ToCitations(cited), activities.Count, intent, "model", removed);
    }

    internal static string BuildProsePrompt(
        string question,
        IReadOnlyList<AskTurn> history,
        DateTimeOffset now,
        AskPeriod period,
        string notes,
        AskIntent intent,
        string? chitChat)
    {
        var hints = new StringBuilder();
        if (chitChat is not null)
        {
            hints.Append("- ").Append(ChitChatInstruction(chitChat)).Append('\n');
        }

        if (intent.AboutWork)
        {
            hints.Append("- They asked about work. Work means coding, documents, email, chatting, studying or editing. If the notes have none of that, say no work was recorded, then say briefly what they did instead.\n");
        }

        if (intent.AboutTasks)
        {
            hints.Append("- They asked about things to do: give the open tasks from the notes, or say there are none.\n");
        }

        return
            $"""
            You are Glint, a warm, down-to-earth assistant built into the user's computer. You remember what they did on it, from the notes below. Reply the way a thoughtful friend who kept those notes would: natural, specific and short. Match their tone.
            - Only say things about the user's activity that the notes show. Never invent titles, people, times or numbers.
            - Times and durations are already worked out; use them exactly as written, and never add them up yourself.
            - Answer every part of the message.
            - Use a short bulleted list only when naming three or more things; otherwise write plain sentences.
            - Never mention "notes" or "the log", never put numbers in brackets, and write numbers as digits.
            - Do not restate their question, and do not greet them or say how you are unless the message asks.
            {hints}
            Now: {now.ToString("dddd, MMMM d, h:mm tt", CultureInfo.InvariantCulture)}. The message is about {period.Label}.

            NOTES
            {notes}
            {Conversation(history)}User: {question}
            Glint:
            """;
    }

    /// A one-line answer from the facts, when the model is unavailable.
    private static string FactsSentence(IReadOnlyList<Block> blocks, AskPeriod period, AskIntent intent)
    {
        var items = blocks.SelectMany(block => block.Items).ToList();
        if (items.Count == 1)
        {
            var item = items[0];
            return $"{Sentence(item.Activities[0])} That was {MinutesText(item.Minutes)}{Sittings(item)}, {Span(item.Start, item.End)}.";
        }

        return $"{ListIntro(intent, period)}\n\n{RenderList(blocks, period, withTimes: true)}";
    }

    // -----------------------------------------------------------------------
    // Grouping: one line per thing, one block per series
    // -----------------------------------------------------------------------

    /// One thing the user did in the period, however many sittings it took.
    private sealed record Item(
        string Title,
        ActivityMode Mode,
        ActivityCategory Category,
        string Where,
        int Minutes,
        int Sittings,
        long Start,
        long End,
        IReadOnlyList<ActivityRecord> Activities,
        string? Series,
        string? Episode);

    /// Items shown together: several episodes of one show, or a single item.
    private sealed record Block(string? Series, IReadOnlyList<Item> Items)
    {
        public long Start => Items.Min(item => item.Start);

        public int Minutes => Items.Sum(item => item.Minutes);
    }

    private static IReadOnlyList<Block> Group(IReadOnlyList<ActivityRecord> activities, AskPeriod period)
    {
        var items = activities
            .GroupBy(activity => activity.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var ordered = group.OrderBy(activity => activity.StartedAtMilliseconds).ToList();
                var first = ordered[0];
                var title = TitleOf(first);
                string? series = null;
                string? episode = null;
                if (first.Mode == ActivityMode.Watch && EpisodeRegex().Match(title) is { Success: true } match)
                {
                    series = match.Groups["series"].Value.Trim(' ', '-', '–', '—', ':', '|');
                    episode = match.Groups["n"].Value;
                    if (series.Length < 3)
                    {
                        series = null;
                    }
                }

                return new Item(
                    title,
                    first.Mode,
                    first.Category,
                    first.Site ?? first.App,
                    ordered.Sum(activity => Minutes(Inside(activity, period))),
                    ordered.Count,
                    first.StartedAtMilliseconds,
                    ordered.Max(activity => activity.EndedAtMilliseconds),
                    ordered,
                    series,
                    episode);
            })
            .OrderBy(item => item.Start)
            .ToList();

        // Two or more episodes of the same show on the same site become one
        // block; everything else stands alone.
        var seriesGroups = items
            .Where(item => item.Series is not null)
            .GroupBy(item => (item.Series!.ToLowerInvariant(), item.Where))
            .Where(group => group.Count() >= 2)
            .ToDictionary(group => group.Key, group => group.ToList());
        var blocks = new List<Block>();
        var placed = new HashSet<Item>();
        foreach (var item in items)
        {
            if (placed.Contains(item))
            {
                continue;
            }

            if (item.Series is not null && seriesGroups.TryGetValue((item.Series.ToLowerInvariant(), item.Where), out var episodes))
            {
                var ordered = episodes
                    .OrderBy(entry => int.TryParse(entry.Episode, out var number) ? number : int.MaxValue)
                    .ThenBy(entry => entry.Start)
                    .ToList();
                blocks.Add(new Block(item.Series, ordered));
                placed.UnionWith(ordered);
            }
            else
            {
                blocks.Add(new Block(null, [item]));
                placed.Add(item);
            }
        }

        return blocks.OrderBy(block => block.Start).ToList();
    }

    private static string TitleOf(ActivityRecord activity) => activity.Mode switch
    {
        ActivityMode.Watch when activity.Category == ActivityCategory.Music => activity.Subject,
        ActivityMode.Watch or ActivityMode.Play => activity.Subject,
        ActivityMode.Make when activity.Subject.Equals(activity.App, StringComparison.OrdinalIgnoreCase) => activity.App,
        ActivityMode.Make or ActivityMode.Private => activity.Subject,
        _ => activity.Label
    };

    /// <summary>
    /// The list as people write them: plain bullets, a show's episodes
    /// nested under it, durations in words, sittings merged.
    /// </summary>
    private static string RenderList(IReadOnlyList<Block> blocks, AskPeriod period, bool withTimes)
    {
        var mixed = blocks.SelectMany(block => block.Items).Select(item => (item.Mode, item.Category)).Distinct().Count() > 1;
        var multiDay = blocks.SelectMany(block => block.Items).Select(item => Day(item.Start)).Distinct().Count() > 1;
        var shown = blocks.Count <= 12
            ? blocks
            : blocks.OrderByDescending(block => block.Minutes).Take(10).OrderBy(block => block.Start).ToList();
        var lines = new List<string>();
        foreach (var block in shown)
        {
            if (block.Series is not null)
            {
                var where = block.Items[0].Where;
                lines.Add($"• {(mixed ? "Watched " : string.Empty)}{block.Series} on {where}: {block.Items.Count} episodes, {MinutesText(block.Minutes)}");
                foreach (var episode in block.Items)
                {
                    var time = withTimes ? $", {When(episode.Start, multiDay)}" : string.Empty;
                    lines.Add($"   ◦ Episode {episode.Episode}: {MinutesText(episode.Minutes)}{Sittings(episode)}{time}");
                }

                continue;
            }

            var item = block.Items[0];
            var detail = new StringBuilder(MinutesText(item.Minutes));
            if (item.Mode is ActivityMode.Watch or ActivityMode.Read && !item.Title.Contains(item.Where, StringComparison.OrdinalIgnoreCase))
            {
                detail.Append(item.Mode == ActivityMode.Read ? " in " : " on ").Append(item.Where);
            }

            detail.Append(Sittings(item));
            if (withTimes)
            {
                detail.Append(", ").Append(When(item.Start, multiDay));
            }

            lines.Add($"• {(mixed ? LeadVerb(item) : string.Empty)}{item.Title}: {detail}");
        }

        if (shown.Count < blocks.Count)
        {
            var hidden = blocks.Except(shown).ToList();
            lines.Add($"• …and {hidden.Count} shorter thing{(hidden.Count == 1 ? string.Empty : "s")}, {MinutesText(hidden.Sum(block => block.Minutes))} together");
        }

        return string.Join("\n", lines);
    }

    private static string LeadVerb(Item item) => (item.Mode, item.Category) switch
    {
        (ActivityMode.Watch, ActivityCategory.Music) => "Listened to music on ",
        (ActivityMode.Watch, _) => "Watched ",
        (ActivityMode.Play, _) => "Played ",
        (ActivityMode.Make, _) when item.Title.Equals(item.Where, StringComparison.OrdinalIgnoreCase) => "Worked in ",
        (ActivityMode.Make, _) => "Edited ",
        (ActivityMode.Private, _) => "Private time on ",
        (_, ActivityCategory.Email) => "Email: ",
        (_, ActivityCategory.Chat) => "Chat: ",
        (_, ActivityCategory.Coding) => "Coding: ",
        (_, ActivityCategory.Docs) => "Documents: ",
        (_, ActivityCategory.Learning) => "Studied: ",
        (_, ActivityCategory.Assistant) => "AI assistant: ",
        (_, ActivityCategory.Files) => "Files: ",
        _ => string.Empty
    };

    private static string Sittings(Item item) =>
        item.Sittings switch
        {
            1 => string.Empty,
            2 => " (two sittings)",
            _ => $" ({item.Sittings} sittings)"
        };

    private static string When(long start, bool withDay) =>
        withDay ? $"{Day(start)} at {Clock(start)}" : $"from {Clock(start)}";

    private static IReadOnlyList<AskCitation> Cite(IReadOnlyList<Block> blocks) =>
        ToCitations(blocks.SelectMany(block => block.Items).SelectMany(item => item.Activities).OrderBy(activity => activity.StartedAtMilliseconds).ToList());

    // -----------------------------------------------------------------------
    // Gathering
    // -----------------------------------------------------------------------

    private List<ActivityRecord> Collect(AskPeriod period)
    {
        var stored = _source.GetActivitiesBetween(period.FromMilliseconds, period.ToMilliseconds);
        var live = _source.GetLiveActivities()
            .Where(activity => Overlaps(activity, period))
            .Where(activity => !stored.Any(existing => existing.Key == activity.Key
                && existing.StartedAtMilliseconds == activity.StartedAtMilliseconds));
        return stored.Concat(live)
            .Where(activity => Overlaps(activity, period))
            .OrderBy(activity => activity.StartedAtMilliseconds)
            .ToList();
    }

    private static bool Overlaps(ActivityRecord activity, AskPeriod period) =>
        activity.StartedAtMilliseconds < period.ToMilliseconds
        && Math.Max(activity.EndedAtMilliseconds, activity.StartedAtMilliseconds + 1) > period.FromMilliseconds;

    private static bool MatchesKind(ActivityRecord activity, AskIntent intent) =>
        intent.Kinds.Any(kind =>
            (kind.Mode is null || kind.Mode == activity.Mode)
            && (kind.Category is null || kind.Category == activity.Category));

    /// Ids of activities whose own words match the message's keywords.
    private static HashSet<string> Relevance(IReadOnlyList<ActivityRecord> activities, IReadOnlyList<string> keywords)
    {
        var relevant = new HashSet<string>(StringComparer.Ordinal);
        if (keywords.Count == 0)
        {
            return relevant;
        }

        foreach (var activity in activities)
        {
            var words = Words(string.Join(' ', activity.Label, activity.Subject, activity.Summary, activity.App, activity.Site, activity.Task, string.Join(' ', activity.Phases)));
            if (keywords.Any(keyword => words.Contains(keyword)
                || (keyword.Length >= 4 && words.Any(word => word.StartsWith(keyword, StringComparison.Ordinal)))))
            {
                relevant.Add(activity.Id);
            }
        }

        return relevant;
    }

    /// Whether an answer talks about an activity: most of its title's words appear.
    private static bool Mentions(string answer, ActivityRecord activity)
    {
        if (EpisodeRegex().Match(TitleOf(activity)) is { Success: true } episode
            && Regex.IsMatch(answer, $@"\b(?:episode|ep\.?)\s*{episode.Groups["n"].Value}\b", RegexOptions.IgnoreCase))
        {
            return true;
        }

        var said = Words(answer);
        var title = Words(TitleOf(activity)).Where(word => word.Length >= 3).ToList();
        return title.Count > 0 && title.Count(said.Contains) * 10 >= title.Count * 6;
    }

    private static HashSet<string> Words(string text) =>
        WordRegex().Matches(text.ToLowerInvariant()).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);

    /// Matches first, then the longest, kept in time order.
    private static (IReadOnlyList<ActivityRecord> Chosen, IReadOnlyList<ActivityRecord> NotListed) Select(
        IReadOnlyList<ActivityRecord> activities,
        IReadOnlySet<string> relevant,
        AskPeriod period)
    {
        var multiDay = activities.Select(activity => Day(activity.StartedAtMilliseconds)).Distinct().Count() > 1;
        var chosen = new List<ActivityRecord>();
        var used = 0;
        foreach (var activity in activities
                     .OrderByDescending(activity => relevant.Contains(activity.Id))
                     .ThenByDescending(activity => activity.ActiveMilliseconds))
        {
            var line = Note(activity, multiDay, period);
            if (used + line.Length > NotesBudget || chosen.Count >= 40)
            {
                continue;
            }

            chosen.Add(activity);
            used += line.Length;
        }

        return (chosen.OrderBy(activity => activity.StartedAtMilliseconds).ToList(), activities.Except(chosen).ToList());
    }

    // -----------------------------------------------------------------------
    // Notes the model reads for a specific question
    // -----------------------------------------------------------------------

    private static string Note(ActivityRecord activity, bool withDate, AskPeriod period)
    {
        var builder = new StringBuilder("- ");
        builder.Append(withDate ? Day(activity.StartedAtMilliseconds) + " " : string.Empty)
            .Append(Span(activity.StartedAtMilliseconds, activity.EndedAtMilliseconds))
            .Append(" (").Append(MinutesText(Minutes(activity.ActiveMilliseconds)));
        var inside = Minutes(Inside(activity, period));
        if (inside != Minutes(activity.ActiveMilliseconds))
        {
            builder.Append(", ").Append(MinutesText(inside)).Append(" of it in this period");
        }

        builder.Append("): ").Append(Sentence(activity));

        var details = new List<string>();
        if (activity.Check is SummaryCheck.Verified or SummaryCheck.Partial && !string.IsNullOrWhiteSpace(activity.Summary))
        {
            details.Add("What happened: " + Clip(activity.Summary!, 260));
        }

        if (activity.Phases.Count > 1 || (activity.Phases.Count == 1 && !activity.Label.Contains(activity.Phases[0], StringComparison.OrdinalIgnoreCase)))
        {
            details.Add("Inside: " + Clip(string.Join("; ", activity.Phases.Take(5)), 140));
        }

        var events = activity.Events
            .Where(item => item.Kind != "interrupted")
            .GroupBy(item => item.Kind)
            .Select(group => group.Count() == 1 ? group.Key : $"{group.Key} x{group.Count()}")
            .ToList();
        if (events.Count > 0)
        {
            details.Add("Events: " + string.Join(", ", events));
        }

        if (activity.Task is not null && activity.TaskStatus != ActivityTaskStatus.None)
        {
            details.Add($"Task ({TaskWord(activity.TaskStatus)}): {Clip(activity.Task, 140)}");
        }

        if (details.Count > 0)
        {
            builder.Append("\n  ").Append(string.Join(" | ", details));
        }

        return builder.Append('\n').ToString();
    }

    private static string BuildNotes(
        IReadOnlyList<ActivityRecord> chosen,
        IReadOnlyList<ActivityRecord> ofKind,
        IReadOnlyList<ActivityRecord> all,
        IReadOnlyList<ActivityRecord> notListed,
        AskPeriod period,
        AskIntent intent,
        IReadOnlySet<string> relevant)
    {
        var multiDay = chosen.Select(activity => Day(activity.StartedAtMilliseconds)).Distinct().Count() > 1;
        var notes = new StringBuilder();
        notes.Append("Totals for ").Append(period.Label).Append(": ").Append(Totals(all, period)).Append('\n');
        if (intent.Kinds.Count > 0)
        {
            notes.Append("Of the kind asked about: ").Append(ofKind.Count).Append(" activit")
                .Append(ofKind.Count == 1 ? "y" : "ies").Append(", ")
                .Append(MinutesText(TotalMinutes(ofKind, period))).Append(" in total.\n");
        }

        foreach (var activity in chosen)
        {
            notes.Append(Note(activity, multiDay, period));
        }

        if (notListed.Count > 0)
        {
            notes.Append("Not listed: ").Append(notListed.Count).Append(" shorter activit")
                .Append(notListed.Count == 1 ? "y" : "ies").Append(", ")
                .Append(MinutesText(TotalMinutes(notListed, period))).Append(" in total.\n");
        }

        if (intent.Keywords.Count > 0 && relevant.Count == 0)
        {
            notes.Append("Nothing recorded mentions: ").Append(string.Join(", ", intent.Keywords)).Append(".\n");
        }

        if (intent.AboutTasks && !all.Any(activity => activity.TaskStatus is ActivityTaskStatus.Open or ActivityTaskStatus.LooksDone && activity.Task is not null))
        {
            notes.Append("Open tasks: none recorded.\n");
        }

        return notes.ToString();
    }

    /// "Which was longer": worked out here so the model only has to say it.
    private static string Comparisons(string question, IReadOnlyList<ActivityRecord> chosen, AskPeriod period)
    {
        if (chosen.Count < 2 || !ComparisonRegex().IsMatch(question))
        {
            return string.Empty;
        }

        var ranked = chosen
            .Select(activity => (Activity: activity, Minutes: Minutes(Inside(activity, period))))
            .OrderByDescending(entry => entry.Minutes)
            .ToList();
        return $"Longest: {TitleOf(ranked[0].Activity)} ({MinutesText(ranked[0].Minutes)}). "
            + $"Shortest: {TitleOf(ranked[^1].Activity)} ({MinutesText(ranked[^1].Minutes)}).\n";
    }

    /// Recorded time in the period by kind, as sums of the minutes shown.
    private static string Totals(IReadOnlyList<ActivityRecord> activities, AskPeriod period)
    {
        var byKind = activities
            .GroupBy(activity => DoingOf(activity.Mode, activity.Category))
            .Select(group => (Doing: group.Key, Minutes: TotalMinutes(group, period)))
            .OrderByDescending(entry => entry.Minutes)
            .Select(entry => $"{entry.Doing} {MinutesText(entry.Minutes)}");
        var first = activities.Min(activity => activity.StartedAtMilliseconds);
        var last = activities.Max(activity => activity.EndedAtMilliseconds);
        return $"{activities.Count} activit{(activities.Count == 1 ? "y" : "ies")}, {MinutesText(TotalMinutes(activities, period))} recorded between {Clock(first)} and {Clock(last)}. Time by kind: {string.Join("; ", byKind)}.";
    }

    /// <summary>
    /// The activity as a sentence about the user. A small model copies the
    /// shape of what it is shown, so it is shown sentences it can reuse, with
    /// titles exactly as recorded.
    /// </summary>
    internal static string Sentence(ActivityRecord activity)
    {
        var where = activity.Site ?? activity.App;
        var phase = activity.Phases.Count > 0 ? activity.Phases[^1] : null;
        return (activity.Mode, activity.Category) switch
        {
            (ActivityMode.Watch, ActivityCategory.Music) =>
                phase is null ? $"You listened to music on {activity.Subject}." : $"You listened to music on {activity.Subject} ({phase}).",
            (ActivityMode.Watch, _) => $"You watched {activity.Subject} on {where}.",
            (ActivityMode.Play, _) => $"You played {activity.Subject}.",
            (ActivityMode.Make, _) when activity.Subject.Equals(activity.App, StringComparison.OrdinalIgnoreCase) =>
                $"You worked in {activity.App}.",
            (ActivityMode.Make, _) => $"You edited {activity.Subject} in {activity.App}.",
            (ActivityMode.Private, _) => $"You spent time on {activity.Subject} (private, so its content was not recorded).",
            _ => $"You were {DoingOf(activity.Mode, activity.Category)} in {where}: {activity.Label}."
        };
    }

    /// What the user was doing, in words a sentence can reuse as they are.
    private static string DoingOf(ActivityMode mode, ActivityCategory category) => (mode, category) switch
    {
        (ActivityMode.Watch, ActivityCategory.Music) => "listening to music",
        (ActivityMode.Watch, _) => "watching video",
        (ActivityMode.Play, _) => "playing a game",
        (ActivityMode.Make, ActivityCategory.Photo) => "editing photos",
        (ActivityMode.Make, _) => "editing or creating",
        (ActivityMode.Private, ActivityCategory.Finance) => "on a private finance site",
        (ActivityMode.Private, _) => "in a private app",
        (_, ActivityCategory.Email) => "doing email",
        (_, ActivityCategory.Chat) => "chatting",
        (_, ActivityCategory.Coding) => "coding",
        (_, ActivityCategory.Docs) => "working on documents",
        (_, ActivityCategory.Learning) => "studying",
        (_, ActivityCategory.Assistant) => "using an AI assistant",
        (_, ActivityCategory.Files) => "managing files",
        (_, ActivityCategory.Game) => "browsing games",
        (_, ActivityCategory.Browsing) => "browsing",
        _ => "using other apps"
    };

    /// Work, as people mean it when they ask about it.
    private static bool IsWork(ActivityRecord activity) =>
        activity.Mode is ActivityMode.Make
        || activity.Category is ActivityCategory.Coding or ActivityCategory.Docs or ActivityCategory.Email
            or ActivityCategory.Chat or ActivityCategory.Learning or ActivityCategory.Design or ActivityCategory.Photo
            or ActivityCategory.Assistant or ActivityCategory.Files;

    // -----------------------------------------------------------------------
    // Everything that is not about the user's activity
    // -----------------------------------------------------------------------

    internal static string BuildGeneralPrompt(string question, IReadOnlyList<AskTurn> history, AskKind kind)
    {
        var smallTalk = kind == AskKind.SmallTalk
            ? "Reply to exactly what they said, warmly and briefly, like a friend: a greeting with a greeting, thanks with \"you're welcome\", \"how are you\" with how you are. Only say how you are if they asked. If they ask what you can do: you remember what they did on this computer (what they watched, read, played, edited and worked on, and when) and can answer questions about it, and you can help with anything else too.\n"
            : string.Empty;
        return
            $"""
            You are Glint, a warm, thoughtful assistant running privately on the user's computer. Reply naturally, the way a capable, friendly person would: match their tone and the length their message calls for, answer directly, and use a list only when it really helps. If you are not sure of something, say so instead of guessing.
            {smallTalk}
            {Conversation(history)}User: {question}
            Glint:
            """;
    }

    private async Task<AskResponse> AnswerGeneralAsync(
        string question,
        IReadOnlyList<AskTurn> history,
        AskIntent intent,
        CancellationToken cancellationToken)
    {
        var kind = intent.Kind == AskKind.SmallTalk ? "smalltalk" : "general";
        if (_generator is null)
        {
            var reply = intent.Kind == AskKind.SmallTalk
                ? (ChitChat(question) is { } chat ? ChitChatReply(chat) + " " : string.Empty)
                  + "Ask me about anything you did on this computer: what you watched, read, played or worked on, and when."
                : "The local model isn't set up yet, so for now I can only answer questions about what you did on this computer.";
            return new AskResponse(reply, [], 0, kind, intent.Period.Label, "facts", 0);
        }

        var raw = (await _generator.GenerateAsync(
                new LiteRtGenerationRequest(BuildGeneralPrompt(question, history, intent.Kind), Temperature: 0.6, Seed: 1),
                TimeSpan.FromMinutes(5),
                cancellationToken)
            .ConfigureAwait(false)).Text;
        var answer = StripCitations(Clean(raw));
        return new AskResponse(
            answer.Length > 0 ? answer : "Sorry, I couldn't come up with an answer to that.",
            [],
            0,
            kind,
            intent.Period.Label,
            "model",
            0);
    }

    private static string Conversation(IReadOnlyList<AskTurn> history)
    {
        var turns = new List<string>();
        var used = 0;
        foreach (var turn in history.Reverse())
        {
            var speaker = turn.Role switch
            {
                "user" => "User",
                "agent" => "Glint",
                _ => null
            };
            if (speaker is null || string.IsNullOrWhiteSpace(turn.Text))
            {
                continue;
            }

            var text = $"{speaker}: {Clip(turn.Text.Trim(), 320)}";
            if (turns.Count > 0 && used + text.Length > HistoryBudget)
            {
                break;
            }

            turns.Add(text);
            used += text.Length;
        }

        if (turns.Count == 0)
        {
            return string.Empty;
        }

        turns.Reverse();
        return "Conversation so far:\n" + string.Join("\n", turns) + "\n\n";
    }

    // -----------------------------------------------------------------------
    // Small talk inside a question
    // -----------------------------------------------------------------------

    /// The friendly-chat part of a message, if any: "how are you", "thanks".
    /// Words that can sit next to small talk without asking for anything.
    private static readonly HashSet<string> FillerWords = new(StringComparer.Ordinal)
    {
        "today", "tonight", "now", "doing", "going", "there", "glint", "buddy", "friend",
        "man", "dude", "and", "so", "also", "then", "btw", "anyway", "lol", "haha", "u", "you", "is", "it"
    };

    internal static string? ChitChat(string question)
    {
        var match = ChitChatRegex().Match(question);
        return match.Success ? match.Value.Trim() : null;
    }

    /// <summary>
    /// Makes sure small talk in the message was answered. The model usually
    /// does; when it skipped it, or the check removed its reply, Glint's own
    /// short reply leads instead.
    /// </summary>
    private static string EnsureChitChat(string answer, string? chitChat)
    {
        if (chitChat is null)
        {
            return answer;
        }

        var opening = answer.Length <= 160 ? answer : answer[..160];
        return ChitChatAnsweredRegex().IsMatch(opening) ? answer : $"{ChitChatReply(chitChat)} {answer}".Trim();
    }

    /// <summary>
    /// What to do with the small talk in a message, matched to what was
    /// said: a greeting is not "how are you", and gets no "I'm doing great".
    /// </summary>
    private static string ChitChatInstruction(string chitChat)
    {
        var lower = chitChat.ToLowerInvariant();
        if (lower.Contains("how are", StringComparison.Ordinal) || lower.Contains("how's it", StringComparison.Ordinal)
            || lower.Contains("hows it", StringComparison.Ordinal) || lower.Contains("how you doing", StringComparison.Ordinal)
            || lower.Contains("how're", StringComparison.Ordinal) || lower.Contains("how r u", StringComparison.Ordinal))
        {
            return $"They also asked how you are (\"{chitChat}\"): begin by saying how you are, warmly, in a few words.";
        }

        if (lower.StartsWith("thank", StringComparison.Ordinal))
        {
            return "They also thanked you: begin with a brief \"you're welcome\".";
        }

        if (lower.Contains("what's up", StringComparison.Ordinal) || lower.Contains("whats up", StringComparison.Ordinal))
        {
            return "They also asked what's up: begin with a brief, friendly reply.";
        }

        return "They greeted you: begin by greeting them back in a word or two. Do not say how you are.";
    }

    /// Glint's own reply to small talk, used when the model is not.
    private static string ChitChatReply(string chitChat)
    {
        var lower = chitChat.ToLowerInvariant();
        if (lower.Contains("how are", StringComparison.Ordinal) || lower.Contains("how's it", StringComparison.Ordinal)
            || lower.Contains("how you doing", StringComparison.Ordinal) || lower.Contains("how're", StringComparison.Ordinal))
        {
            return "I'm doing well, thanks for asking!";
        }

        if (lower.StartsWith("thank", StringComparison.Ordinal) || lower is "thx" or "ty")
        {
            return "You're welcome!";
        }

        if (lower.Contains("what's up", StringComparison.Ordinal) || lower.Contains("whats up", StringComparison.Ordinal) || lower == "sup")
        {
            return "Not much, just keeping track of things for you!";
        }

        return "Hey!";
    }

    // -----------------------------------------------------------------------
    // Shared pieces
    // -----------------------------------------------------------------------

    /// With GLINT_ASK_DEBUG=1, the model's unchecked text goes to stderr.
    private static void Diagnose(string part, string raw)
    {
        if (Environment.GetEnvironmentVariable("GLINT_ASK_DEBUG") == "1")
        {
            Console.Error.WriteLine($"[ask:{part}] {raw.ReplaceLineEndings(" / ")}");
        }
    }

    private static AskResponse Reply(string answer, IReadOnlyList<AskCitation> citations, int scoped, AskIntent intent, string source, int removed) =>
        new(answer, citations, scoped, "activity", intent.Period.Label, source, removed);

    private string NothingRecorded(AskPeriod period)
    {
        var latest = _source.GetLatestActivity();
        return latest is null
            ? $"I don't have anything recorded {PeriodPhrase(period)}. Start scanning and I'll remember what you do."
            : $"I don't have anything recorded {PeriodPhrase(period)}. The most recent thing I have is {TitleOf(latest)}, on {Day(latest.StartedAtMilliseconds)} at {Clock(latest.StartedAtMilliseconds)}.";
    }

    /// The period as it reads inside a sentence: "today", "yesterday",
    /// "so far", "on Friday, September 18".
    internal static string PeriodPhrase(AskPeriod period)
    {
        var label = period.Label;
        var cut = label.IndexOf(" (", StringComparison.Ordinal);
        var head = cut > 0 ? label[..cut] : label;
        return head switch
        {
            "all recorded time" => "so far",
            "the last few minutes" => "just now",
            _ when char.IsUpper(head[0]) => "on " + head,
            _ => head
        };
    }

    private static string KindVerb(AskIntent intent)
    {
        var verbs = intent.Kinds.Select(kind => (kind.Mode, kind.Category) switch
        {
            (ActivityMode.Watch, ActivityCategory.Music) => "listened to",
            (ActivityMode.Watch, _) => "watched",
            (ActivityMode.Play, _) => "played",
            (ActivityMode.Make, _) => "worked on",
            (_, ActivityCategory.Email) => "did on email",
            (_, ActivityCategory.Chat) => "chatted about",
            (_, ActivityCategory.Coding) => "coded",
            (_, ActivityCategory.Docs) => "worked on",
            (_, ActivityCategory.Learning) => "studied",
            (_, ActivityCategory.Assistant) => "asked AI about",
            (ActivityMode.Read, _) => "read",
            _ => "did"
        }).Distinct().ToList();
        return string.Join(" and ", verbs);
    }

    internal static string KindPhrase(AskIntent intent)
    {
        var phrases = intent.Kinds.Select(kind => (kind.Mode, kind.Category) switch
        {
            (ActivityMode.Watch, ActivityCategory.Music) => "listening to music",
            (ActivityMode.Watch, _) => "watching anything",
            (ActivityMode.Play, _) => "playing games",
            (ActivityMode.Make, _) => "editing or creating anything",
            (_, ActivityCategory.Email) => "on email",
            (_, ActivityCategory.Chat) => "chatting",
            (_, ActivityCategory.Coding) => "coding",
            (_, ActivityCategory.Docs) => "working on documents",
            (_, ActivityCategory.Learning) => "studying",
            (_, ActivityCategory.Assistant) => "using an AI assistant",
            (ActivityMode.Read, _) => "reading or browsing",
            _ => "doing that"
        }).Distinct();
        return string.Join(" or ", phrases);
    }

    internal static string Clean(string raw)
    {
        var text = raw.Replace("**", string.Empty, StringComparison.Ordinal).Trim();
        foreach (var prefix in new[] { "Glint:", "Answer:", "Assistant:", "Opening:" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..].TrimStart();
            }
        }

        // A model that keeps going writes the user's next turn: stop there.
        var cut = text.IndexOf("\nUser:", StringComparison.Ordinal);
        return (cut >= 0 ? text[..cut] : text).Trim().Trim('"');
    }

    /// Answers carry no reference numbers: the evidence is listed under them.
    internal static string StripCitations(string text) =>
        Regex.Replace(CitationRegex().Replace(text, string.Empty), @"[ \t]+([.,;:!?])", "$1").Trim();

    /// <summary>
    /// The opening part of what the model wrote: its leading paragraphs, up
    /// to two, stopping at anything that looks like a list or a copied
    /// "Facts:" line. Glint shows the real list itself.
    /// </summary>
    private static string Opening(string text, string highlights, string facts)
    {
        // Pieces of the prompt the model may echo back: each highlight's
        // "(2 episodes, 25 min)" and the facts' opening clause.
        var echoes = HighlightTimeRegex().Matches(highlights).Select(match => match.Value)
            .Append(facts.Split('.')[0])
            .Where(echo => echo.Length > 6)
            .ToList();
        var kept = new List<string>();
        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var listLike = paragraph.Split('\n').Any(line =>
            {
                var trimmed = line.TrimStart();
                return trimmed.StartsWith('•') || trimmed.StartsWith('◦') || trimmed.StartsWith("- ", StringComparison.Ordinal)
                    || trimmed.StartsWith("* ", StringComparison.Ordinal) || trimmed.StartsWith("Facts:", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("Highlights:", StringComparison.OrdinalIgnoreCase);
            });
            if (listLike || kept.Count == 2)
            {
                break;
            }

            // Within a paragraph, keep the sentences before any echo.
            var sentences = new List<string>();
            foreach (var sentence in Regex.Split(paragraph.ReplaceLineEndings(" "), @"(?<=[.!?])\s+"))
            {
                if (echoes.Any(echo => sentence.Contains(echo, StringComparison.OrdinalIgnoreCase)))
                {
                    break;
                }

                sentences.Add(sentence);
            }

            if (sentences.Count > 0)
            {
                kept.Add(string.Join(" ", sentences));
            }

            if (sentences.Count < Regex.Split(paragraph, @"(?<=[.!?])\s+").Length)
            {
                break;
            }
        }

        return string.Join(" ", kept).Trim();
    }

    private static IReadOnlyList<AskCitation> ToCitations(IReadOnlyList<ActivityRecord> activities) =>
        activities.Take(MaxCitations).Select((activity, index) => new AskCitation(
            index + 1,
            activity.Id,
            activity.Label,
            activity.Mode,
            activity.Category,
            activity.StartedAtMilliseconds,
            activity.EndedAtMilliseconds,
            activity.ActiveMilliseconds,
            activity.App,
            activity.Site)).ToList();

    /// Whole minutes, as every duration is shown.
    private static int Minutes(long milliseconds) => (int)Math.Round(milliseconds / 60_000.0, MidpointRounding.AwayFromZero);

    /// The sum of the minutes each activity shows, so a total always equals
    /// what a reader adds up.
    private static int TotalMinutes(IEnumerable<ActivityRecord> activities, AskPeriod period) =>
        activities.Sum(activity => Minutes(Inside(activity, period)));

    internal static string MinutesText(int minutes) =>
        minutes < 1 ? "under a minute"
        : minutes < 60 ? $"{minutes} min"
        : minutes % 60 == 0 ? $"{minutes / 60} h"
        : $"{minutes / 60} h {minutes % 60} min";

    private static long Inside(ActivityRecord activity, AskPeriod period)
    {
        var span = Math.Max(1, activity.EndedAtMilliseconds - activity.StartedAtMilliseconds);
        var start = Math.Max(activity.StartedAtMilliseconds, period.FromMilliseconds);
        var end = Math.Min(activity.EndedAtMilliseconds, period.ToMilliseconds);
        if (end <= start)
        {
            return activity.EndedAtMilliseconds <= activity.StartedAtMilliseconds && Overlaps(activity, period)
                ? activity.ActiveMilliseconds
                : 0;
        }

        // Active time scaled to the part of the activity inside the period.
        return (long)(activity.ActiveMilliseconds * ((double)(end - start) / span));
    }

    private static string TaskWord(ActivityTaskStatus status) => status switch
    {
        ActivityTaskStatus.Open => "open",
        ActivityTaskStatus.LooksDone => "looks done",
        ActivityTaskStatus.Done => "done",
        _ => "none"
    };

    private static string Clock(long milliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture);

    private static string Day(long milliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToLocalTime().ToString("ddd MMM d", CultureInfo.InvariantCulture);

    private static string Span(long start, long end) =>
        end - start < 60_000 ? $"at {Clock(start)}" : $"{Clock(start)}–{Clock(end)}";

    private static string Clip(string text, int length) =>
        text.Length <= length ? text : text[..length].TrimEnd() + "…";

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’]*", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\s*\[\s*\d+(?:\s*[,\]]\s*\[?\s*\d+)*\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex CitationRegex();

    [GeneratedRegex(@"\b(longer|longest|shorter|shortest|most|least|more time|less time|biggest|compare|which (one|was|took))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComparisonRegex();

    [GeneratedRegex(@"\b(what|which)\b.*\b(did|have|do)\s+i\b|\b(list|show me|give me)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListQuestionRegex();

    [GeneratedRegex(@"^(?<series>.+?)\s*[-–:|]?\s*\b(?:episode|ep\.?)\s*(?<n>\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeRegex();

    [GeneratedRegex(@"\b(thanks|thank you|doing (well|great|good|fine)|i'?m (good|great|well|fine|doing)|not (much|bad)|you'?re welcome|glad|hey|hi|hello|good (morning|afternoon|evening))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChitChatAnsweredRegex();

    [GeneratedRegex(@"\([^()]*\d+ (?:min|h)[^()]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex HighlightTimeRegex();

    [GeneratedRegex(@"^\s*(?:hey|hi|hello|yo|hiya)\b[\s,!]*(?:glint\b)?[\s,!]*|\b(how (are|r) (you|u)( doing)?|how's it going|hows it going|how are things|how you doing|how're you|what'?s up|hope you'?re (well|good)|thank(s| you)( so much)?|good (morning|afternoon|evening|night)|hey there|hello|hi there)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChitChatRegex();
}
