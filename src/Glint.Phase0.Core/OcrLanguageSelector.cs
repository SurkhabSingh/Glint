using System.Globalization;

namespace Glint.Phase0.Core;

internal static class OcrLanguageSelector
{
    public const int MaxLanguagesPerFrame = 8;

    private static readonly string[] HighValueScriptPrefixes =
    [
        "ja",
        "zh",
        "ko",
        "ar",
        "he",
        "hi",
        "th",
        "ru"
    ];

    public static IReadOnlyList<string> CandidateTags(
        IEnumerable<string> availableTags,
        IEnumerable<string> userLanguageTags,
        int maxLanguages = MaxLanguagesPerFrame)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLanguages);

        var available = availableTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selected = new List<string>(capacity: Math.Min(maxLanguages, available.Length));

        void AddMatching(IEnumerable<string> tags)
        {
            foreach (var tag in tags)
            {
                var match = available.FirstOrDefault(candidate =>
                    string.Equals(candidate, tag, StringComparison.OrdinalIgnoreCase)
                    || candidate.StartsWith(tag + "-", StringComparison.OrdinalIgnoreCase));
                if (match is not null
                    && !selected.Contains(match, StringComparer.OrdinalIgnoreCase)
                    && selected.Count < maxLanguages)
                {
                    selected.Add(match);
                }
            }
        }

        AddMatching(userLanguageTags);
        AddMatching(HighValueScriptPrefixes);

        foreach (var tag in available.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase))
        {
            if (selected.Count >= maxLanguages)
            {
                break;
            }

            if (!selected.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                selected.Add(tag);
            }
        }

        return selected;
    }

    public static int ScoreRecognizedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var letters = 0;
        var digits = 0;
        var cjkOrKana = 0;
        var hangul = 0;
        var words = 0;
        var symbols = 0;
        var replacement = 0;
        var previousWasToken = false;

        foreach (var character in text)
        {
            if (char.IsLetter(character))
            {
                letters++;
                if (IsCjkOrKana(character))
                {
                    cjkOrKana++;
                }
                else if (IsHangul(character))
                {
                    hangul++;
                }

                if (!previousWasToken)
                {
                    words++;
                }

                previousWasToken = true;
            }
            else if (char.IsDigit(character))
            {
                digits++;
                if (!previousWasToken)
                {
                    words++;
                }

                previousWasToken = true;
            }
            else
            {
                previousWasToken = false;
                var category = CharUnicodeInfo.GetUnicodeCategory(character);
                if (category is UnicodeCategory.OtherSymbol
                    or UnicodeCategory.MathSymbol
                    or UnicodeCategory.CurrencySymbol
                    or UnicodeCategory.ModifierSymbol)
                {
                    symbols++;
                }

                if (character == '\uFFFD')
                {
                    replacement++;
                }
            }
        }

        return (letters * 4)
            + (digits * 2)
            + (words * 3)
            + (cjkOrKana * 8)
            + (hangul * 6)
            - (symbols * 2)
            - (replacement * 20);
    }

    private static bool IsCjkOrKana(char character) =>
        character is >= '\u3040' and <= '\u30FF'
            or >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF';

    private static bool IsHangul(char character) =>
        character is >= '\uAC00' and <= '\uD7AF';
}
