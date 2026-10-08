using System.Text;

namespace Glint.Phase0.Core;

/// <summary>
/// Decides what a look added, by comparing its lines with everything already
/// stored for the same page since its last full copy.
/// </summary>
/// <remarks>
/// This replaces an exact hash of the whole text compared with the single
/// previous capture of any window. That treated a price tick, a clock, or one
/// OCR misread as a brand new screen, so a live page was saved in full on
/// every tick; and it compared a page with whichever window came before, so
/// alt-tabbing back and forth stored the same page again and again.
///
/// Lines are compared after tidying: numbers become "#", common OCR
/// confusions inside numbers are folded, case and spacing are evened out.
/// "AAPL 187.42 ▲0.3%" and "AAPL 187.51 ▲0.4%" are then the same line, so a
/// ticking value is not a change, while a genuinely new line ("Order filled",
/// a new message, the reply being typed) is.
/// </remarks>
public static class ChangeMeter
{
    /// Below this share of lines already known, the screen counts as a
    /// different one and gets a full copy.
    public const double NewScreenBelow = 0.6;

    /// A full copy at least this often while a page keeps changing, so the
    /// stored picture of it never drifts far from what was on screen.
    public const long KeyframeEveryMilliseconds = 300_000;

    /// Lines shorter than this after tidying are layout debris ("|", "x", "#").
    private const int MinimumLineLength = 3;

    /// <summary>
    /// Fewer letters than this across everything new is debris, not a
    /// change: OCR of a video frame ("、 ミ 、"), a flickering time code.
    /// </summary>
    public const int MinimumNewLetters = 12;

    /// Characters that stand in for things that are not text: embedded
    /// objects, unreadable glyphs, invisible joiners.
    private static readonly char[] NotText = ['\uFFFC', '\uFFFD', '\u200B', '\u200C', '\u200D', '\uFEFF'];

    /// Text without placeholder characters, so a page's embedded players and
    /// icons cannot make a line look new.
    public static string CleanText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.IndexOfAny(NotText) < 0
            ? text
            : string.Concat(text.Where(character => Array.IndexOf(NotText, character) < 0));
    }

    /// Marks the value of the control the user is typing in. Its numbers are
    /// kept, because a quantity the user entered is exactly what matters.
    public const string InputPrefix = "[input] ";

    public static string NormalizeLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var keepDigits = line.StartsWith(InputPrefix, StringComparison.Ordinal);
        var builder = new StringBuilder(line.Length);
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(keepDigits ? token.ToLowerInvariant() : NormalizeToken(token));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Lowercases a token and collapses every run of digits (with the
    /// separators and OCR look-alikes inside it) to one "#".
    /// </summary>
    private static string NormalizeToken(string token)
    {
        var digits = 0;
        foreach (var character in token)
        {
            if (char.IsDigit(character))
            {
                digits++;
            }
        }

        var numeric = digits > 0 && digits * 2 >= token.Count(char.IsLetterOrDigit);
        var builder = new StringBuilder(token.Length);
        var inNumber = false;
        foreach (var raw in token)
        {
            var character = char.ToLowerInvariant(raw);
            // Inside a mostly-numeric token, letters OCR confuses with digits
            // are digits: "1O5" is 105, "l0" is 10.
            var isDigit = char.IsDigit(character)
                || (numeric && character is 'o' or 'l' or 'i' or 's' or 'b');
            var isSeparator = inNumber && character is '.' or ',' or ':' or '\'';
            if (isDigit || isSeparator)
            {
                if (!inNumber)
                {
                    builder.Append('#');
                    inNumber = true;
                }

                continue;
            }

            inNumber = false;
            builder.Append(character);
        }

        return builder.ToString();
    }

    /// Distinct tidied lines of a text, in order.
    public static IReadOnlyList<(string Raw, string Key)> Lines(string text)
    {
        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in CleanText(text).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var raw = line.Trim();
            if (raw.Length == 0)
            {
                continue;
            }

            var key = NormalizeLine(raw);
            if (key.Replace("#", string.Empty, StringComparison.Ordinal).Length < MinimumLineLength
                && !raw.StartsWith(InputPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (seen.Add(key))
            {
                result.Add((raw, key));
            }
        }

        return result;
    }

    private static int Letters(IEnumerable<string> lines) =>
        lines.Sum(line => line.Count(char.IsLetter));

    public static HashSet<string> Basis(IEnumerable<string> storedTexts)
    {
        var basis = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in storedTexts)
        {
            foreach (var (_, key) in Lines(text))
            {
                basis.Add(key);
            }
        }

        return basis;
    }

    /// <param name="text">This look's redacted text.</param>
    /// <param name="basis">Tidied lines already stored for this page since its last full copy; empty if none.</param>
    /// <param name="sinceKeyframeMilliseconds">Time since the page's last full copy, or null if it has none.</param>
    public static ChangeVerdict Compare(
        string text,
        IReadOnlySet<string> basis,
        long? sinceKeyframeMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(basis);
        var lines = Lines(text);
        if (lines.Count == 0 || Letters(lines.Select(line => line.Raw)) < MinimumNewLetters)
        {
            return new(ChangeVerdictKind.Same, 1, []);
        }

        if (basis.Count == 0 || sinceKeyframeMilliseconds is null)
        {
            return new(ChangeVerdictKind.Keyframe, 0, lines.Select(line => line.Raw).ToList());
        }

        var known = 0;
        var fresh = new List<string>();
        foreach (var (raw, key) in lines)
        {
            if (basis.Contains(key))
            {
                known++;
            }
            else
            {
                fresh.Add(raw);
            }
        }

        var similarity = (double)known / lines.Count;
        var typed = fresh.Any(line => line.StartsWith(InputPrefix, StringComparison.Ordinal));
        if (fresh.Count == 0 || (!typed && Letters(fresh) < MinimumNewLetters))
        {
            return new(ChangeVerdictKind.Same, similarity, []);
        }

        if (similarity < NewScreenBelow || sinceKeyframeMilliseconds >= KeyframeEveryMilliseconds)
        {
            return new(ChangeVerdictKind.Keyframe, similarity, lines.Select(line => line.Raw).ToList());
        }

        return new(ChangeVerdictKind.Delta, similarity, fresh);
    }
}

public enum ChangeVerdictKind
{
    Same,
    Delta,
    Keyframe
}

/// <param name="Lines">For a keyframe, every line; for a delta, only the new ones.</param>
public sealed record ChangeVerdict(
    ChangeVerdictKind Kind,
    double Similarity,
    IReadOnlyList<string> Lines);
