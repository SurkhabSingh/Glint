namespace Glint.Phase0.Core;

/// <summary>
/// Reads the website out of a browser's address bar. Only the host is kept:
/// paths and query strings can carry tokens, names and search terms, and the
/// host alone is what tells activities apart.
/// </summary>
public static class SiteParser
{
    private static readonly string[] InternalSchemes =
        ["about:", "chrome:", "edge:", "brave:", "opera:", "vivaldi:", "file:", "moz-extension:", "chrome-extension:", "view-source:"];

    /// The host, without "www." or a port, or null if the value is not a
    /// web address (a search being typed, an internal page, empty).
    public static string? TryParseHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.Any(char.IsWhiteSpace))
        {
            return null;
        }

        if (InternalSchemes.Any(scheme => text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var scheme = text[..schemeEnd];
            if (!scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                && !scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            text = text[(schemeEnd + 3)..];
        }

        var end = text.IndexOfAny(['/', '?', '#']);
        var host = (end >= 0 ? text[..end] : text).ToLowerInvariant();
        var at = host.LastIndexOf('@');
        if (at >= 0)
        {
            host = host[(at + 1)..];
        }

        var colon = host.IndexOf(':');
        if (colon >= 0)
        {
            host = host[..colon];
        }

        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        if (host.Length < 3
            || !host.Contains('.')
            || host.StartsWith('.')
            || host.EndsWith('.')
            || !host.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-'))
        {
            return null;
        }

        var lastLabel = host[(host.LastIndexOf('.') + 1)..];
        return lastLabel.Length >= 2 && !lastLabel.All(char.IsAsciiDigit) ? host : null;
    }
}
