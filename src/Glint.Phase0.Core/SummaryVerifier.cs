using System.Text.RegularExpressions;

namespace Glint.Phase0.Core;

public sealed record VerifiedNarration(
    string? Label,
    string? Summary,
    string? Task,
    int Kept,
    int Dropped,
    SummaryCheck Check);

/// <summary>
/// Removes anything from a model's description that was not on screen. Every
/// name, number, date and quoted phrase in a sentence must appear in the
/// activity's own text (or its app, site and title); a sentence that fails
/// is dropped before anyone sees it.
/// </summary>
/// <remarks>
/// The check is deliberately about specifics. A small local model rarely
/// invents structure ("the user replied to an email") but often invents or
/// borrows particulars: a name from another tab, a date from the example in
/// its instructions, a number from nowhere. Those are exactly the things a
/// person acts on, so they are the things that must be traceable.
///
/// Matching tolerates one wrong character in words of five letters or more,
/// because OCR misreads ("Pnya" for "Priya") are in the source, not in the
/// model's output.
/// </remarks>
public static partial class SummaryVerifier
{
    /// Capitalized words that are ordinary sentence furniture, not facts.
    private static readonly HashSet<string> Ordinary = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "this", "that", "these", "those", "they", "he", "she",
        "it", "i", "we", "you", "user", "users", "their", "his", "her", "its",
        "there", "here", "in", "on", "at", "after", "before", "then", "also",
        "no", "and", "but", "or", "while", "when", "during", "both", "several",
        "some", "one", "two", "three", "most", "many", "each", "every", "another",
        "other", "later", "earlier", "today", "tomorrow", "yesterday", "tonight",
        "none", "nothing", "glint", "screen", "page", "app", "window", "email",
        "emails", "chat", "message", "messages", "video", "videos", "game",
        "document", "file", "files", "task", "summary", "label", "re", "fwd",
        "ok", "okay", "yes", "not", "only", "about", "with", "from", "for", "to",
        "by", "as", "of", "if", "so", "all", "any", "more", "new", "recent"
    };

    public static VerifiedNarration Verify(
        Narration narration,
        string source,
        IEnumerable<string> metadata,
        IEnumerable<string> otherActivities)
    {
        ArgumentNullException.ThrowIfNull(narration);
        ArgumentNullException.ThrowIfNull(source);
        var evidence = new Evidence(source + "\n" + string.Join("\n", metadata ?? []));
        var foreign = ForeignTokens(otherActivities ?? [], evidence);

        var kept = new List<string>();
        var dropped = 0;
        foreach (var sentence in Sentences(narration.Summary))
        {
            if (Supported(sentence, evidence, foreign))
            {
                kept.Add(sentence);
            }
            else
            {
                dropped++;
            }
        }

        var label = Supported(narration.Label, evidence, foreign) ? narration.Label : null;
        var task = narration.Task is not null && Supported(narration.Task, evidence, foreign)
            ? narration.Task
            : null;
        var check = kept.Count == 0
            ? SummaryCheck.Fallback
            : dropped == 0 ? SummaryCheck.Verified : SummaryCheck.Partial;
        return new VerifiedNarration(
            label,
            kept.Count == 0 ? null : string.Join(" ", kept),
            task,
            kept.Count,
            dropped,
            check);
    }

    /// <summary>
    /// Words that begin sentences in ordinary answers ("Here's", "Sure",
    /// "Overall"). A sentence-initial word is checked as a fact only when it
    /// is not one of these, so a name opening a sentence is still checked.
    /// </summary>
    private static readonly HashSet<string> Starters = new(StringComparer.OrdinalIgnoreCase)
    {
        "here's", "heres", "sure", "yes", "yeah", "no", "nope", "sorry", "unfortunately",
        "looks", "seems", "overall", "first", "firstly", "then", "next", "finally", "lastly",
        "later", "also", "besides", "mostly", "mainly", "earlier", "it's", "that's", "there's",
        "you've", "you're", "you'd", "i'm", "i've", "i'd", "nothing", "based", "according",
        "between", "around", "all", "total", "totals", "altogether", "apart", "however",
        "although", "though", "meanwhile", "afterwards", "afterward", "plus", "beyond",
        "great", "good", "okay", "ok", "hi", "hello", "hey", "thanks", "glad", "happy",
        "today", "yesterday", "tonight", "this", "that", "your", "you", "we", "they",
        "most", "much", "many", "several", "some", "one", "two", "three", "four", "five",
        "a", "an", "the", "in", "on", "at", "by", "for", "from", "with", "without", "during",
        "after", "before", "since", "until", "while", "when", "where", "what", "which",
        "who", "why", "how", "if", "so", "and", "but", "or", "not", "just", "only", "still",
        "both", "each", "every", "other", "another", "there", "here", "these", "those",
        "spent", "watched", "played", "read", "edited", "listened", "worked", "chatted",
        "replied", "wrote", "browsed", "studied", "used", "opened", "started", "finished",
        "continued", "returned", "switched", "checked", "looked", "viewed", "visited",
        "time", "nope", "none", "let", "feel", "want", "would", "could", "can", "will",
        "did", "does", "do", "was", "were", "is", "are", "had", "has", "have", "my", "our"
    };

    /// <summary>
    /// The parts of a chat answer the evidence supports. Line structure is
    /// kept (bullets stay bullets); within each line every sentence whose
    /// names, numbers, times or quotes are not in the evidence is removed.
    /// Citation brackets are ignored while checking.
    /// </summary>
    public static (string Text, int Removed) FilterAnswer(string answer, string evidence)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(evidence);
        var support = new Evidence(evidence);
        var noForeign = new HashSet<string>(StringComparer.Ordinal);
        var removed = 0;
        var lines = new List<string>();
        foreach (var rawLine in answer.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Trim().Length == 0)
            {
                if (lines.Count > 0 && lines[^1].Length > 0)
                {
                    lines.Add(string.Empty);
                }

                continue;
            }

            var bullet = BulletRegex().Match(line);
            var prefix = bullet.Success ? bullet.Value : string.Empty;
            var body = line[prefix.Length..];
            var kept = new List<string>();
            foreach (var sentence in Sentences(body))
            {
                // Spelled-out numbers are numbers: "twenty-six minutes" is
                // checked exactly like "26 minutes".
                var checkable = NumberWordsToDigits(CitationRegex().Replace(sentence, " "));
                if (SupportedAnswer(checkable, support, noForeign))
                {
                    kept.Add(sentence);
                }
                else
                {
                    removed++;
                }
            }

            if (kept.Count > 0)
            {
                lines.Add(prefix + string.Join(" ", kept));
            }
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        // A lead-in that announces a list ("Here's what you did:") is
        // meaningless once every item under it was removed.
        if (lines.Count == 1 && lines[0].TrimEnd().EndsWith(':'))
        {
            removed++;
            lines.Clear();
        }

        return (string.Join("\n", lines).Trim(), removed);
    }

    private static readonly string[] Units =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"
    ];

    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    /// "twenty-six" becomes "26", "fourteen" becomes "14", "a hundred" stays words.
    internal static string NumberWordsToDigits(string text) =>
        NumberWordRegex().Replace(text, match =>
        {
            var parts = match.Value.ToLowerInvariant().Split(['-', ' '], StringSplitOptions.RemoveEmptyEntries);
            var value = 0;
            foreach (var part in parts)
            {
                var unit = Array.IndexOf(Units, part);
                var ten = Array.IndexOf(Tens, part);
                value += unit >= 0 ? unit : ten >= 2 ? ten * 10 : 0;
            }

            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        });

    private static bool SupportedAnswer(string sentence, Evidence evidence, IReadOnlySet<string> foreign)
    {
        var words = WordRegex().Matches(sentence);
        if (words.Count > 0 && Starters.Contains(words[0].Value.Trim('\'', '’')))
        {
            // The opening word is grammar; check the rest as usual.
            var rest = sentence[(words[0].Index + words[0].Length)..];
            return Supported("and " + rest, evidence, foreign);
        }

        return Supported(sentence, evidence, foreign);
    }

    /// The checkable specifics of a text: numbers, names, quotes, addresses.
    internal static IReadOnlyList<string> Specifics(string text)
    {
        var specifics = new List<string>();
        foreach (Match match in QuoteRegex().Matches(text))
        {
            specifics.Add(match.Groups[1].Value);
        }

        foreach (Match match in DigitRunRegex().Matches(text))
        {
            specifics.Add(match.Value);
        }

        var words = WordRegex().Matches(text);
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index].Value.Trim('\'', '’');
            if (word.Length < 2 || !char.IsUpper(word[0]) || Ordinary.Contains(word))
            {
                continue;
            }

            // A sentence's first word is capitalized by grammar, not because
            // it is a name; verbs like "Replied" or "Watched" are not facts.
            var sentenceStart = index == 0 || EndsSentence(text, words[index - 1], words[index]);
            if (sentenceStart && (word.EndsWith("ed", StringComparison.Ordinal) || word.EndsWith("ing", StringComparison.Ordinal)))
            {
                continue;
            }

            specifics.Add(word);
        }

        return specifics;
    }

    private static bool Supported(string text, Evidence evidence, IReadOnlySet<string> foreign)
    {
        foreach (var specific in Specifics(text))
        {
            if (!evidence.Contains(specific))
            {
                return false;
            }
        }

        foreach (Match match in WordRegex().Matches(text))
        {
            if (foreign.Contains(match.Value.ToLowerInvariant()))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Distinctive words from the other activities in the session that this
    /// activity's own text never mentions. Seeing one in this summary means
    /// the model borrowed from the wrong activity.
    /// </summary>
    private static IReadOnlySet<string> ForeignTokens(IEnumerable<string> others, Evidence evidence)
    {
        var foreign = new HashSet<string>(StringComparer.Ordinal);
        foreach (var other in others)
        {
            foreach (Match match in WordRegex().Matches(other))
            {
                var word = match.Value;
                if (word.Length >= 4 && char.IsUpper(word[0]) && !Ordinary.Contains(word) && !evidence.Contains(word))
                {
                    foreign.Add(word.ToLowerInvariant());
                }
            }
        }

        return foreign;
    }

    private static bool EndsSentence(string text, Match previous, Match current)
    {
        var start = previous.Index + previous.Length;
        var between = text.AsSpan(start, Math.Max(0, current.Index - start));
        return between.IndexOfAny(".!?:") >= 0;
    }

    private static IEnumerable<string> Sentences(string text) =>
        SentenceRegex().Split(text.Trim())
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 0);

    private sealed class Evidence
    {
        private readonly string _lower;
        private readonly HashSet<string> _words;
        private readonly ILookup<int, string> _wordsByLength;

        public Evidence(string text)
        {
            _lower = string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            _words = new HashSet<string>(
                WordRegex().Matches(_lower).Select(match => match.Value),
                StringComparer.Ordinal);
            _wordsByLength = _words.ToLookup(word => word.Length);
        }

        public bool Contains(string specific)
        {
            var needle = string.Join(' ', specific.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (needle.Length == 0 || _lower.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }

            if (needle.Contains(' ') || needle.Length < 5 || needle.Any(char.IsDigit))
            {
                return false;
            }

            // One misread character, as OCR produces.
            for (var length = needle.Length - 1; length <= needle.Length + 1; length++)
            {
                foreach (var candidate in _wordsByLength[length])
                {
                    if (WithinOneEdit(needle, candidate))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool WithinOneEdit(string a, string b)
        {
            if (Math.Abs(a.Length - b.Length) > 1)
            {
                return false;
            }

            var i = 0;
            var j = 0;
            var edits = 0;
            while (i < a.Length && j < b.Length)
            {
                if (a[i] == b[j])
                {
                    i++;
                    j++;
                    continue;
                }

                if (++edits > 1)
                {
                    return false;
                }

                if (a.Length > b.Length)
                {
                    i++;
                }
                else if (b.Length > a.Length)
                {
                    j++;
                }
                else
                {
                    i++;
                    j++;
                }
            }

            return edits + (a.Length - i) + (b.Length - j) <= 1;
        }
    }

    [GeneratedRegex(@"[""“”]([^""“”]{2,80})[""“”]", RegexOptions.CultureInvariant)]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex DigitRunRegex();

    [GeneratedRegex(@"[\p{L}][\p{L}\p{N}'’]*", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"(?<=[.!?])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceRegex();

    [GeneratedRegex(@"^\s*(?:[-*•]|\d+[.)])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex BulletRegex();

    // "one" is left alone: it is a pronoun far more often than a count.
    [GeneratedRegex(@"\b(?:(?:twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)(?:[- ](?:one|two|three|four|five|six|seven|eight|nine))?|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberWordRegex();

    [GeneratedRegex(@"\[\s*\d+(?:\s*,\s*\d+)*\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex CitationRegex();
}
