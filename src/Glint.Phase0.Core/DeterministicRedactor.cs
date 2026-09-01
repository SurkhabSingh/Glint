using System.Text.RegularExpressions;

namespace Glint.Phase0.Core;

public sealed partial class DeterministicRedactor
{
    private sealed record Rule(string Name, string Placeholder, Regex Pattern);

    private static readonly Rule[] Rules =
    [
        new("jwt", "[REDACTED:jwt]", JwtRegex()),
        new("aws-access-key", "[REDACTED:aws-key]", AwsAccessKeyRegex()),
        new("github-token", "[REDACTED:github-token]", GitHubTokenRegex()),
        new("bearer-token", "[REDACTED:bearer-token]", BearerTokenRegex()),
        new("email", "[REDACTED:email]", EmailRegex()),
        new("credit-card", "[REDACTED:credit-card]", CreditCardRegex())
    ];

    public RedactionResult Redact(string input)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var text = FieldContextRegex().Replace(input, match =>
        {
            Increment(counts, "field-context");
            return $"{match.Groups["label"].Value}=[REDACTED:field]";
        });

        foreach (var rule in Rules)
        {
            text = rule.Pattern.Replace(text, match =>
            {
                if (rule.Name == "credit-card" && !PassesLuhn(match.Value))
                {
                    return match.Value;
                }

                Increment(counts, rule.Name);
                return rule.Placeholder;
            });
        }

        text = OpaqueTokenRegex().Replace(text, match =>
        {
            var candidate = match.Value;
            if (!LooksHighEntropy(candidate))
            {
                return candidate;
            }

            Increment(counts, "high-entropy");
            return "[REDACTED:high-entropy]";
        });

        return new(text, counts);
    }

    private static void Increment(IDictionary<string, int> counts, string key) =>
        counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;

    private static bool LooksHighEntropy(string value)
    {
        if (value.Length < 24 || value.All(char.IsLetter) || value.All(char.IsDigit))
        {
            return false;
        }

        var frequencies = value.GroupBy(character => character).Select(group => group.Count());
        var entropy = frequencies.Sum(count =>
        {
            var probability = (double)count / value.Length;
            return -probability * Math.Log2(probability);
        });
        var classes =
            (value.Any(char.IsLower) ? 1 : 0)
            + (value.Any(char.IsUpper) ? 1 : 0)
            + (value.Any(char.IsDigit) ? 1 : 0)
            + (value.Any(character => !char.IsLetterOrDigit(character)) ? 1 : 0);
        return entropy >= 4.0 && classes >= 3;
    }

    private static bool PassesLuhn(string candidate)
    {
        var digits = candidate.Where(char.IsDigit).Select(character => character - '0').ToArray();
        if (digits.Length is < 13 or > 19)
        {
            return false;
        }

        var sum = 0;
        var doubleDigit = false;
        for (var index = digits.Length - 1; index >= 0; index--)
        {
            var value = digits[index];
            if (doubleDigit)
            {
                value *= 2;
                if (value > 9)
                {
                    value -= 9;
                }
            }

            sum += value;
            doubleDigit = !doubleDigit;
        }

        return sum % 10 == 0;
    }

    [GeneratedRegex(
        @"(?im)\b(?<label>password|passwd|secret|api[_ -]?key|access[_ -]?token|refresh[_ -]?token|authorization)\b\s*[:=]\s*(?!\[REDACTED:)[^\s,;]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex FieldContextRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b", RegexOptions.CultureInvariant)]
    private static partial Regex AwsAccessKeyRegex();

    [GeneratedRegex(@"\b(?:ghp|gho|ghu|ghs|github_pat)_[A-Za-z0-9_]{20,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]{16,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b(?:\d[ -]*?){13,19}\b", RegexOptions.CultureInvariant)]
    private static partial Regex CreditCardRegex();

    [GeneratedRegex(@"\b[A-Za-z0-9+/=_\-]{24,160}\b", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueTokenRegex();
}
