namespace Glint.Phase0.Core;

/// <summary>
/// Decides when two sessions are carrying the same outstanding thing, so the
/// same commitment mentioned three times reads as one item rather than three.
/// </summary>
/// <remarks>
/// Matching is deliberately narrow, because the obvious approach does not
/// survive contact with real data. Measured across real reminders, three
/// mentions of one meeting scored 0.29–0.33 word overlap while a genuinely
/// different commitment scored 0.18 against them: too small a margin to
/// separate on, and what overlap there was came entirely from generic words
/// — "meeting", "pm", "tomorrow". The distinctive words (a person's name,
/// "logs") were never shared. A threshold tuned to that would merge
/// unrelated commitments, which is worse than leaving them apart: a missed
/// link shows one thing twice, a false link hides something real.
///
/// So precision comes from a conjunction rather than from a cleverer score:
///
///   - same app, moderate overlap, within a day — the ordinary case of
///     returning to the same conversation; or
///   - near-identical text within a fortnight — the same reminder captured
///     twice, which is what happens when a window stays open across
///     sessions, regardless of app.
///
/// Anything less certain is left unlinked and simply shows up twice.
/// </remarks>
public static class SessionThreads
{
    /// Overlap required when the app and a short window already agree.
    public const double SameAppSimilarity = 0.3;

    /// How long the same conversation may pause and still be the same thread.
    public const long SameAppWindowMilliseconds = 86_400_000;

    /// Overlap required to link across apps: effectively the same sentence.
    public const double NearIdenticalSimilarity = 0.8;

    /// How far back a near-identical reminder may be.
    public const long NearIdenticalWindowMilliseconds = 1_209_600_000;

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "at", "in", "on", "to", "of", "for", "with", "and",
        "or", "is", "was", "be", "been", "this", "that", "it", "its", "i",
        "me", "my", "we", "our", "you", "your", "they", "them", "there",
        "here", "now", "then", "so", "if", "but", "as", "by", "from", "up",
        "out", "over", "under", "again", "more", "most", "some", "such",
        "no", "nor", "not", "only", "own", "same", "than", "too", "very",
        "can", "will", "just", "should", "get", "got"
    };

    /// Content words of a reminder, lowercased, without punctuation.
    public static IReadOnlySet<string> Tokenize(string? text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        var current = new System.Text.StringBuilder();
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character) || character == '\'')
            {
                current.Append(char.ToLowerInvariant(character));
                continue;
            }

            Flush(current, tokens);
        }

        Flush(current, tokens);
        return tokens;

        static void Flush(System.Text.StringBuilder builder, HashSet<string> into)
        {
            if (builder.Length > 1)
            {
                var word = builder.ToString();
                if (!Stopwords.Contains(word))
                {
                    into.Add(word);
                }
            }

            builder.Clear();
        }
    }

    /// Jaccard overlap of two reminders' content words.
    public static double Similarity(string? left, string? right)
    {
        var a = Tokenize(left);
        var b = Tokenize(right);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var shared = a.Count(b.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/>, an earlier session, is carrying
    /// the same thing as <paramref name="session"/>.
    /// </summary>
    public static bool IsSameThread(ActivitySession session, ActivitySession candidate)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(candidate);

        var age = session.StartedAtMilliseconds - candidate.StartedAtMilliseconds;
        if (age < 0)
        {
            return false;
        }

        var similarity = Similarity(session.ReminderCandidate, candidate.ReminderCandidate);
        if (similarity >= NearIdenticalSimilarity && age <= NearIdenticalWindowMilliseconds)
        {
            return true;
        }

        return similarity >= SameAppSimilarity
            && age <= SameAppWindowMilliseconds
            && string.Equals(
                session.ProcessName,
                candidate.ProcessName,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The most recent earlier session carrying the same thing, if any.
    /// Candidates are expected newest first.
    /// </summary>
    public static ActivitySession? FindThread(
        ActivitySession session,
        IEnumerable<ActivitySession> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(candidate => candidate.Id != session.Id)
            .FirstOrDefault(candidate => IsSameThread(session, candidate));
    }
}
