using System.Text.RegularExpressions;

namespace Glint.Phase0.Core;

public static partial class SecretSniffer
{
    private static readonly string[] PrivateKeyMarkers =
    [
        "-----BEGIN OPENSSH PRIVATE KEY-----",
        "-----BEGIN PRIVATE KEY-----",
        "-----BEGIN RSA PRIVATE KEY-----",
        "-----BEGIN EC PRIVATE KEY-----"
    ];

    public static string? ShouldDrop(string text)
    {
        if (PrivateKeyMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal)))
        {
            return "private-key-material";
        }

        var environmentAssignments = EnvironmentAssignmentRegex().Matches(text);
        if (environmentAssignments.Count >= 3)
        {
            return "environment-secret-dump";
        }

        if (SeedPhraseLabelRegex().IsMatch(text) && WordSequenceRegex().IsMatch(text))
        {
            return "seed-or-recovery-phrase";
        }

        return null;
    }

    [GeneratedRegex(@"(?m)^\s*[A-Z][A-Z0-9_]{2,}\s*=\s*\S+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentAssignmentRegex();

    [GeneratedRegex(@"\b(seed|recovery|mnemonic)\s+(phrase|words?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeedPhraseLabelRegex();

    [GeneratedRegex(@"\b(?:[a-z]{3,12}\s+){11,23}[a-z]{3,12}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WordSequenceRegex();
}
