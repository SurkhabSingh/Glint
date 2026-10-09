using System.Text;

namespace Glint.Phase0.Core;

/// <summary>
/// Decides what a look added, by comparing its lines with the lines the same
/// page already holds. A page's lines are stored once each, so there is no
/// full copy to refresh and no list of differences to put back together.
/// </summary>
/// <remarks>
/// Lines are compared after tidying: numbers become "#", common OCR
/// confusions inside numbers are folded, case and spacing are evened out.
/// "AAPL 187.42 ▲0.3%" and "AAPL 187.51 ▲0.4%" are then the same line, so a
/// ticking value is not a change, while a genuinely new line ("Order filled",
/// a new message, the reply being typed) is. The tidied form is only the key
/// lines are compared by; the line itself is stored as it was read.
/// </remarks>
public static class ChangeMeter
{
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

    /// <param name="lines">This look's lines (<see cref="PageLines.Of"/> of its redacted text).</param>
    /// <param name="known">Keys of those lines the page already holds.</param>
    public static ChangeVerdict Compare(
        IReadOnlyList<PageLine> lines,
        IReadOnlySet<string> known)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(known);
        if (lines.Count == 0 || Letters(lines.Select(line => line.Text)) < MinimumNewLetters)
        {
            return new(ChangeVerdictKind.Same, 1, []);
        }

        var fresh = lines.Where(line => !known.Contains(line.Key)).ToList();
        var similarity = (double)(lines.Count - fresh.Count) / lines.Count;
        var typed = fresh.Any(line => line.Typed);
        if (fresh.Count == 0 || (!typed && Letters(fresh.Select(line => line.Text)) < MinimumNewLetters))
        {
            return new(ChangeVerdictKind.Same, similarity, []);
        }

        return new(ChangeVerdictKind.New, similarity, fresh);
    }
}

/// The lines of a text, as a page stores them.
public static class PageLines
{
    public static IReadOnlyList<PageLine> Of(string text) =>
        ChangeMeter.Lines(text)
            .Select(line => new PageLine(
                line.Key,
                line.Raw,
                line.Raw.StartsWith(ChangeMeter.InputPrefix, StringComparison.Ordinal)))
            .ToList();
}

public enum ChangeVerdictKind
{
    /// Nothing worth keeping that the page does not already hold.
    Same,

    /// Lines the page did not hold yet.
    New
}

/// <param name="Lines">The lines the page did not hold yet; empty when nothing changed.</param>
public sealed record ChangeVerdict(
    ChangeVerdictKind Kind,
    double Similarity,
    IReadOnlyList<PageLine> Lines);
