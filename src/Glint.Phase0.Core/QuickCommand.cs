namespace Glint.Phase0.Core;

public enum QuickCommandKind
{
    CaptureCurrentWindow,
    StartScanning,
    PauseScanning,
    SearchContext,
    OpenDashboard
}

public sealed record QuickCommand(QuickCommandKind Kind, string? Query = null);

public static class QuickCommandParser
{
    public static QuickCommand Parse(string input)
    {
        var command = input.Trim();
        var lower = command.ToLowerInvariant();
        if (lower.StartsWith("@glint", StringComparison.Ordinal))
        {
            command = command[6..].Trim();
            lower = command.ToLowerInvariant();
        }

        if (lower.StartsWith("search", StringComparison.Ordinal)
            || lower.StartsWith("find", StringComparison.Ordinal)
            || lower.StartsWith("look up", StringComparison.Ordinal))
        {
            var query = RemoveLeadingIntent(command, "search", "find", "look up");
            if (query.Equals("your context", StringComparison.OrdinalIgnoreCase)
                || query.Equals("my context", StringComparison.OrdinalIgnoreCase)
                || query.Equals("context", StringComparison.OrdinalIgnoreCase))
            {
                query = string.Empty;
            }

            return new(
                QuickCommandKind.SearchContext,
                string.IsNullOrWhiteSpace(query) ? null : query);
        }

        if (ContainsAny(lower, "pause", "stop scanning", "stop capture"))
        {
            return new(QuickCommandKind.PauseScanning);
        }

        if (ContainsAny(lower, "start scanning", "start capture", "resume scanning", "resume capture"))
        {
            return new(QuickCommandKind.StartScanning);
        }

        if (ContainsAny(lower, "capture", "scan this", "scan current", "grab this"))
        {
            return new(QuickCommandKind.CaptureCurrentWindow);
        }

        if (ContainsAny(lower, "open glint", "open dashboard", "show glint", "show dashboard"))
        {
            return new(QuickCommandKind.OpenDashboard);
        }

        return string.IsNullOrWhiteSpace(command)
            ? new(QuickCommandKind.OpenDashboard)
            : new(QuickCommandKind.SearchContext, command);
    }

    private static string RemoveLeadingIntent(string value, params string[] intents)
    {
        foreach (var intent in intents.OrderByDescending(intent => intent.Length))
        {
            if (value.StartsWith(intent, StringComparison.OrdinalIgnoreCase))
            {
                return value[intent.Length..]
                    .TrimStart(' ', ':', '-', '\t');
            }
        }

        return value;
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.Ordinal));
}
