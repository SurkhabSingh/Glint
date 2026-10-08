using System.Globalization;
using System.Text.RegularExpressions;

namespace Glint.Phase0.Core;

public enum AskKind
{
    /// "hi", "thanks", "what can you do": a short friendly reply.
    SmallTalk,

    /// Not about the user's own activity: answered like any assistant.
    General,

    /// About what the user did: answered from their activities only.
    Activity
}

/// A window of local time a question is about, half-open [From, To).
public sealed record AskPeriod(long FromMilliseconds, long ToMilliseconds, string Label);

/// One kind of activity a message asks about: a mode, a category, or both
/// ("listened" is Watch and Music, "emailed" is any Email).
public sealed record AskKindFilter(ActivityMode? Mode, ActivityCategory? Category);

public sealed record AskIntent(
    AskKind Kind,
    AskPeriod Period,
    bool PeriodFromQuestion,
    IReadOnlyList<AskKindFilter> Kinds,
    IReadOnlyList<string> Keywords,
    bool AboutWork,
    bool AboutTasks,
    // "Summarize my day", "what did I do": an overview rather than a
    // specific question. Written by Glint from the facts, exactly.
    bool WantsSummary = false);

/// <summary>
/// Reads what a message is asking: whether it is about the user's own
/// activity at all, which stretch of time, and which kind of activity.
/// Deterministic, so the same words always mean the same period.
/// </summary>
/// <remarks>
/// The old Ask took its period only from the scope picker and its evidence
/// from the newest captures. "Summarize my work on this day" with the picker
/// on All time therefore summarized a random slice of history. Words in the
/// message now win over the picker, because they are what the user meant.
/// </remarks>
public static partial class AskIntentParser
{
    private static readonly string[] SmallTalkOpeners =
    [
        "hi", "hello", "hey", "yo", "sup", "hiya", "thanks", "thank you", "thx", "ty",
        "good morning", "good afternoon", "good evening", "good night", "ok", "okay",
        "cool", "nice", "great", "awesome", "bye", "goodbye", "see you", "who are you",
        "what are you", "what can you do", "how are you", "help", "lol", "haha"
    ];

    /// Words that put a message about the user's own life.
    private static readonly HashSet<string> Personal = new(StringComparer.Ordinal)
    {
        "i", "my", "me", "mine", "myself", "i'm", "im", "i've", "ive", "i'd", "id", "we", "our", "us"
    };

    private static readonly HashSet<string> ActivityWords = new(StringComparer.Ordinal)
    {
        "did", "done", "doing", "do", "spent", "spend", "worked", "work", "working",
        "today", "yesterday", "tonight", "morning", "afternoon", "evening", "week",
        "recently", "earlier", "lately", "summarize", "summary", "recap", "activity",
        "activities", "timeline", "session", "sessions", "history", "day", "productive",
        "focus", "focused", "distracted", "time", "hours", "minutes", "when", "last",
        "remind", "todo", "tasks", "task", "unfinished", "open", "pending", "owe",
        "promised", "deadline", "deadlines"
    };

    private static readonly (string[] Words, ActivityMode? Mode, ActivityCategory? Category)[] KindWords =
    [
        (["watch", "watched", "watching", "video", "videos", "youtube", "anime", "episode", "episodes", "movie", "movies", "film", "films", "show", "shows", "series", "stream", "streams", "netflix", "twitch"], ActivityMode.Watch, ActivityCategory.Video),
        (["listen", "listened", "listening", "music", "song", "songs", "spotify", "playlist", "album", "podcast", "podcasts"], ActivityMode.Watch, ActivityCategory.Music),
        (["play", "played", "playing", "game", "games", "gaming", "gamed"], ActivityMode.Play, ActivityCategory.Game),
        (["edit", "edited", "editing", "design", "designed", "designing", "photoshop", "lightroom", "blender", "draw", "drew", "drawing", "illustrate", "render"], ActivityMode.Make, null),
        (["email", "emails", "mail", "inbox", "gmail", "outlook", "replied", "reply"], null, ActivityCategory.Email),
        (["chat", "chats", "chatted", "message", "messages", "messaged", "discord", "whatsapp", "slack", "telegram", "friend", "friends", "dm", "dms", "talked", "texted"], null, ActivityCategory.Chat),
        (["code", "coded", "coding", "program", "programming", "bug", "bugs", "debug", "debugging", "commit", "repo", "vscode", "terminal"], null, ActivityCategory.Coding),
        (["document", "documents", "doc", "docs", "wrote", "writing", "notes", "word", "excel", "spreadsheet", "pdf"], null, ActivityCategory.Docs),
        (["study", "studied", "studying", "learn", "learned", "learning", "course", "lecture", "homework"], null, ActivityCategory.Learning),
        (["claude", "chatgpt", "gemini", "assistant", "ai"], null, ActivityCategory.Assistant),
        (["read", "reading", "article", "articles", "browse", "browsed", "browsing", "website", "websites", "site", "page", "pages"], ActivityMode.Read, null)
    ];

    private static readonly HashSet<string> WorkWords = new(StringComparer.Ordinal) { "work", "worked", "working", "productive", "job", "office" };

    private static readonly HashSet<string> TaskWords = new(StringComparer.Ordinal)
    {
        "todo", "to-do", "task", "tasks", "remind", "reminder", "reminders", "unfinished",
        "pending", "owe", "promised", "deadline", "deadlines", "follow", "need"
    };

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "but", "of", "to", "in", "on", "at", "for", "with",
        "about", "from", "by", "as", "is", "was", "were", "are", "be", "been", "it", "this",
        "that", "these", "those", "what", "which", "who", "whom", "when", "where", "why",
        "how", "much", "many", "long", "did", "do", "does", "doing", "done", "can", "could",
        "would", "should", "will", "shall", "have", "has", "had", "any", "some", "all",
        "there", "here", "then", "than", "so", "if", "not", "no", "yes", "please", "tell",
        "show", "give", "list", "summarize", "summary", "recap", "again", "more", "most",
        "just", "also", "very", "really", "get", "got", "make", "made", "spent", "spend",
        "time", "thing", "things", "stuff", "anything", "something", "everything", "kind",
        "today", "yesterday", "tonight", "morning", "afternoon", "evening", "night", "week",
        "month", "day", "days", "hour", "hours", "minute", "minutes", "now", "currently",
        "right", "recently", "earlier", "lately", "last", "past", "previous", "ago", "around",
        "between", "before", "after", "during", "since", "until", "into", "onto", "over",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
        "activity", "activities", "work", "worked", "working", "whole", "entire", "overall",
        "glint", "hey", "hi", "hello", "yo", "please", "pls", "thanks", "thank", "you", "been"
    };

    private static readonly string[] Weekdays = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    private static readonly string[] Months =
        ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];

    public static AskIntent Parse(string question, string? scope, string? day, DateTimeOffset now, bool hasHistory)
    {
        ArgumentNullException.ThrowIfNull(question);
        var text = question.Trim().ToLowerInvariant();

        // "This day", "my day", "the day": the day picked in the scope
        // control when one is, otherwise today.
        if (ThisDayRegex().IsMatch(text))
        {
            var picked = PeriodFromScope("day", scope == "day" ? day : null, now);
            return Parse(ThisDayRegex().Replace(text, " "), scope, day, now, hasHistory) with
            {
                Kind = AskKind.Activity,
                Period = picked,
                PeriodFromQuestion = true
            };
        }

        var words = WordRegex().Matches(text).Select(match => match.Value).ToList();

        var kinds = KindWords
            .Where(entry => words.Any(word => entry.Words.Contains(word)))
            .Select(entry => new AskKindFilter(entry.Mode, entry.Category))
            .Distinct()
            .ToList();

        var period = PeriodFromQuestion(text, now);
        var personal = words.Any(Personal.Contains);
        var activityWords = words.Any(ActivityWords.Contains);
        var kindWords = kinds.Count > 0;

        AskKind kind;
        if (IsSmallTalk(text, words) && !kindWords && period is null)
        {
            kind = AskKind.SmallTalk;
        }
        else if (personal || activityWords || period is not null || (kindWords && words.Count <= 8) || (hasHistory && words.Count <= 6))
        {
            kind = AskKind.Activity;
        }
        else
        {
            kind = AskKind.General;
        }

        var keywords = words
            .Where(word => word.Length >= 3 && !Stopwords.Contains(word) && !Personal.Contains(word)
                && !KindWords.Any(entry => entry.Words.Contains(word)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new AskIntent(
            kind,
            period ?? PeriodFromScope(scope, day, now),
            period is not null,
            kinds,
            keywords,
            words.Any(WorkWords.Contains),
            words.Any(TaskWords.Contains),
            SummaryRegex().IsMatch(text));
    }

    private static bool IsSmallTalk(string text, IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            return true;
        }

        if (words.Count > 7)
        {
            return false;
        }

        var trimmed = text.TrimEnd('!', '.', '?', ' ');
        return SmallTalkOpeners.Any(opener =>
            trimmed == opener
            || trimmed.StartsWith(opener + " ", StringComparison.Ordinal)
            || trimmed.StartsWith(opener + ",", StringComparison.Ordinal));
    }

    public static AskPeriod PeriodFromScope(string? scope, string? day, DateTimeOffset now)
    {
        var today = StartOfDay(now);
        switch (scope)
        {
            case "week":
                return Window(today.AddDays(-6), now.AddMinutes(1), $"the past week ({Date(today.AddDays(-6))} – {Date(today)})");
            case "day" when DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var picked):
                var start = new DateTimeOffset(picked.ToDateTime(TimeOnly.MinValue), now.Offset);
                return Window(start, start.AddDays(1), DayLabel(start, today));
            case "day":
                return Window(today, today.AddDays(1), DayLabel(today, today));
            default:
                return new AskPeriod(0, now.AddMinutes(1).ToUnixTimeMilliseconds(), "all recorded time");
        }
    }

    /// <summary>The period a message names, or null when it names none.</summary>
    public static AskPeriod? PeriodFromQuestion(string text, DateTimeOffset now)
    {
        var today = StartOfDay(now);
        var yesterday = today.AddDays(-1);

        if (Regex.IsMatch(text, @"\b(right now|currently|at the moment|just now)\b") || Regex.IsMatch(text, @"\bam i (doing|watching|reading|playing|on)\b"))
        {
            return Window(now.AddMinutes(-15), now.AddMinutes(1), "the last few minutes");
        }

        var lastSpan = LastSpanRegex().Match(text);
        if (lastSpan.Success)
        {
            var amount = lastSpan.Groups["n"].Success && int.TryParse(lastSpan.Groups["n"].Value, out var n) ? n : 1;
            var unit = lastSpan.Groups["unit"].Value;
            var span = unit.StartsWith("min", StringComparison.Ordinal)
                ? TimeSpan.FromMinutes(amount)
                : unit.StartsWith("hour", StringComparison.Ordinal) ? TimeSpan.FromHours(amount) : TimeSpan.FromDays(amount);
            var label = $"the last {(amount == 1 ? string.Empty : amount + " ")}{unit.TrimEnd('s')}{(amount == 1 ? string.Empty : "s")}";
            return Window(now - span, now.AddMinutes(1), label);
        }

        if (Regex.IsMatch(text, @"\blast night\b"))
        {
            return Window(yesterday.AddHours(18), today.AddHours(4), $"last night ({Date(yesterday)})");
        }

        // A day, possibly narrowed to part of it.
        DateTimeOffset? dayStart = null;
        string? dayName = null;
        if (Regex.IsMatch(text, @"\b(today|today's|tonight|this morning|this afternoon|this evening)\b"))
        {
            dayStart = today;
            dayName = "today";
        }
        else if (Regex.IsMatch(text, @"\bday before yesterday\b"))
        {
            dayStart = today.AddDays(-2);
        }
        else if (Regex.IsMatch(text, @"\byesterday\b"))
        {
            dayStart = yesterday;
            dayName = "yesterday";
        }
        else if (WeekdayRegex().Match(text) is { Success: true } weekday)
        {
            var target = Array.IndexOf(Weekdays, weekday.Groups["d"].Value);
            var back = ((int)now.DayOfWeek - target + 7) % 7;
            if (weekday.Groups["last"].Success && back == 0)
            {
                back = 7;
            }

            dayStart = today.AddDays(-back);
        }
        else if (DateRegex().Match(text) is { Success: true } date && ParseDate(date, now) is { } parsed)
        {
            dayStart = parsed;
        }
        else if (IsoDateRegex().Match(text) is { Success: true } iso
                 && DateOnly.TryParseExact(iso.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var isoDate))
        {
            dayStart = new DateTimeOffset(isoDate.ToDateTime(TimeOnly.MinValue), now.Offset);
        }

        if (Regex.IsMatch(text, @"\bthis week\b"))
        {
            var monday = today.AddDays(-(((int)now.DayOfWeek + 6) % 7));
            return Window(monday, now.AddMinutes(1), $"this week ({Date(monday)} – {Date(today)})");
        }

        if (Regex.IsMatch(text, @"\blast week\b"))
        {
            var monday = today.AddDays(-(((int)now.DayOfWeek + 6) % 7));
            return Window(monday.AddDays(-7), monday, $"last week ({Date(monday.AddDays(-7))} – {Date(monday.AddDays(-1))})");
        }

        if (Regex.IsMatch(text, @"\b(this month)\b"))
        {
            var first = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
            return Window(first, now.AddMinutes(1), $"this month ({first:MMMM})");
        }

        // A clock time: "at 5 pm", "around 17:30".
        var clock = ClockRegex().Match(text);
        if (clock.Success && ToHour(clock) is { } hour)
        {
            var baseDay = dayStart ?? today;
            var at = baseDay.AddHours(hour.Hour).AddMinutes(hour.Minute);
            return Window(at.AddMinutes(-30), at.AddMinutes(60), $"around {at:h:mm tt} {DayLabel(baseDay, today)}");
        }

        if (dayStart is { } chosen)
        {
            var part = Regex.Match(text, @"\b(morning|afternoon|evening|tonight|night)\b");
            var label = dayName ?? DayLabel(chosen, today);
            if (part.Success)
            {
                return part.Value switch
                {
                    "morning" => Window(chosen.AddHours(5), chosen.AddHours(12), $"{label} morning"),
                    "afternoon" => Window(chosen.AddHours(12), chosen.AddHours(17), $"{label} afternoon"),
                    _ => Window(chosen.AddHours(17), chosen.AddDays(1).AddHours(4), $"{label} evening"),
                };
            }

            return Window(chosen, chosen.AddDays(1), dayName is null ? label : $"{label} ({Date(chosen)})");
        }

        // A part of a day with no day named means today.
        var bare = Regex.Match(text, @"\bthis (morning|afternoon|evening)\b");
        if (bare.Success)
        {
            return PeriodFromQuestion($"today {bare.Groups[1].Value}", now);
        }

        return null;
    }

    private static DateTimeOffset? ParseDate(Match match, DateTimeOffset now)
    {
        var monthText = match.Groups["m1"].Success ? match.Groups["m1"].Value : match.Groups["m2"].Value;
        var dayText = match.Groups["d1"].Success ? match.Groups["d1"].Value : match.Groups["d2"].Value;
        var month = Array.FindIndex(Months, name => name.StartsWith(monthText, StringComparison.Ordinal)) + 1;
        if (month == 0 || !int.TryParse(dayText, out var dayOfMonth) || dayOfMonth is < 1 or > 31)
        {
            return null;
        }

        var year = now.Year;
        if (dayOfMonth > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        var candidate = new DateTimeOffset(year, month, dayOfMonth, 0, 0, 0, now.Offset);
        // "Dec 30" asked in January means last December.
        return candidate > now ? candidate.AddYears(-1) : candidate;
    }

    private static (int Hour, int Minute)? ToHour(Match clock)
    {
        if (!int.TryParse(clock.Groups["h"].Value, out var hour))
        {
            return null;
        }

        var minute = clock.Groups["min"].Success && int.TryParse(clock.Groups["min"].Value, out var parsedMinute) ? parsedMinute : 0;
        var meridiem = clock.Groups["ampm"].Value;
        if (meridiem.StartsWith('p') && hour < 12)
        {
            hour += 12;
        }
        else if (meridiem.StartsWith('a') && hour == 12)
        {
            hour = 0;
        }
        else if (meridiem.Length == 0 && !clock.Groups["min"].Success)
        {
            return null;
        }

        return hour is >= 0 and < 24 && minute is >= 0 and < 60 ? (hour, minute) : null;
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset now) => new(now.Date, now.Offset);

    private static AskPeriod Window(DateTimeOffset from, DateTimeOffset to, string label) =>
        new(from.ToUnixTimeMilliseconds(), to.ToUnixTimeMilliseconds(), label);

    private static string Date(DateTimeOffset day) => day.ToString("ddd, MMM d", CultureInfo.InvariantCulture);

    private static string DayLabel(DateTimeOffset day, DateTimeOffset today) =>
        day == today ? $"today ({Date(day)})"
        : day == today.AddDays(-1) ? $"yesterday ({Date(day)})"
        : day.ToString("dddd, MMMM d", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"[a-z0-9][a-z0-9'\-]*", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\b(?:last|past|previous)\s+(?:(?<n>\d{1,3})\s+)?(?<unit>minutes?|mins?|hours?|days?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex LastSpanRegex();

    [GeneratedRegex(@"\b(?<last>last\s+)?(?:on\s+)?(?<d>sunday|monday|tuesday|wednesday|thursday|friday|saturday)\b", RegexOptions.CultureInvariant)]
    private static partial Regex WeekdayRegex();

    [GeneratedRegex(@"\b(?:(?<m1>jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\.?\s+(?<d1>\d{1,2})(?:st|nd|rd|th)?|(?<d2>\d{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?(?<m2>jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"\b(?:on\s+)?(?:this|that|my|the)\s+day\b(?!\s+(?:before|after))", RegexOptions.CultureInvariant)]
    private static partial Regex ThisDayRegex();

    [GeneratedRegex(@"\b(summari[sz]e|summary|recap|overview|rundown|what did i (do|get up to|work on)|what have i (done|been doing|been up to)|how was my|how did i spend|what i did)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SummaryRegex();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDateRegex();

    [GeneratedRegex(@"\b(?:at|around|about|by|near)\s+(?<h>\d{1,2})(?::(?<min>\d{2}))?\s*(?<ampm>am|pm|a\.m\.|p\.m\.)?", RegexOptions.CultureInvariant)]
    private static partial Regex ClockRegex();
}
