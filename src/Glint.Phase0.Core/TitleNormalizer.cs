using System.Text.RegularExpressions;

namespace Glint.Phase0.Core;

/// <summary>
/// Turns a window title into the name of the thing being worked on, by
/// removing what keeps changing (counters, zoom, unsaved marks) and what
/// never does (the app's own name).
/// </summary>
/// <remarks>
/// Titles are the most widely available identity signal on Windows: every
/// app has one, including games and canvases with no readable text. They are
/// also noisy. "(3) Inbox - Gmail - Zen Browser", "poster.psd @ 66.7% (Layer
/// 2, RGB/8) *" and "● Program.cs - glint - Visual Studio Code" all carry one
/// stable name wrapped in decoration. Treating the raw title as identity split
/// one activity every time a counter ticked or the zoom changed.
/// </remarks>
public static partial class TitleNormalizer
{
    private static readonly string[] Separators = [" - ", " — ", " – ", " | ", " · "];

    /// App names that appear as a title segment. Anything else constant is
    /// learned per app from its recent titles.
    private static readonly HashSet<string> KnownAppSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "google chrome", "microsoft edge", "microsoft​ edge", "mozilla firefox",
        "firefox", "zen browser", "zen", "brave", "opera", "vivaldi", "arc",
        "librewolf", "waterfox", "floorp", "chromium", "visual studio code",
        "cursor", "windsurf", "discord", "slack", "microsoft teams", "notepad",
        "file explorer", "microsoft visual studio", "visual studio", "obsidian",
        "notion", "spotify", "vlc media player", "adobe acrobat", "word", "excel",
        "powerpoint", "outlook", "mail"
    };

    /// Site names as they appear in page titles, keyed by host.
    private static readonly Dictionary<string, string> SiteBrands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mail.google.com"] = "Gmail",
        ["outlook.live.com"] = "Outlook",
        ["outlook.office.com"] = "Outlook",
        ["outlook.office365.com"] = "Outlook",
        ["youtube.com"] = "YouTube",
        ["music.youtube.com"] = "YouTube Music",
        ["web.whatsapp.com"] = "WhatsApp",
        ["discord.com"] = "Discord",
        ["github.com"] = "GitHub",
        ["docs.google.com"] = "Google Docs",
        ["open.spotify.com"] = "Spotify",
        ["chatgpt.com"] = "ChatGPT",
        ["claude.ai"] = "Claude",
        ["x.com"] = "X",
        ["reddit.com"] = "Reddit",
        ["netflix.com"] = "Netflix",
        ["twitch.tv"] = "Twitch",
        ["stackoverflow.com"] = "Stack Overflow",
        ["web.telegram.org"] = "Telegram",
        ["messenger.com"] = "Messenger"
    };

    public static bool IsUnsaved(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var trimmed = title.Trim();
        return trimmed.StartsWith('*')
            || trimmed.StartsWith('●')
            || trimmed.StartsWith('•')
            || trimmed.EndsWith(" *", StringComparison.Ordinal)
            || UnsavedAfterFileRegex().IsMatch(trimmed);
    }

    /// The site's name as people say it: "Gmail", "YouTube", or the bare host.
    public static string SiteBrand(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        foreach (var (brandHost, brand) in SiteBrands)
        {
            if (host.Equals(brandHost, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + brandHost, StringComparison.OrdinalIgnoreCase))
            {
                return brand;
            }
        }

        return host;
    }

    /// <summary>
    /// Title segments that stay the same across most of an app's titles: its
    /// own name and fixed decoration. Needs a few distinct titles to say
    /// anything, so a single long-lived window is never stripped bare.
    /// </summary>
    public static IReadOnlySet<string> LearnConstantSegments(IReadOnlyList<string> recentTitles)
    {
        ArgumentNullException.ThrowIfNull(recentTitles);
        var learned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinct = recentTitles
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(StripDecoration)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count < 3)
        {
            return learned;
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var title in distinct)
        {
            foreach (var segment in Split(title).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                counts[segment] = counts.GetValueOrDefault(segment) + 1;
            }
        }

        foreach (var (segment, count) in counts)
        {
            if (count * 10 >= distinct.Count * 6)
            {
                learned.Add(segment);
            }
        }

        return learned;
    }

    /// <summary>
    /// The title with the app's name, the site's name, counters, zoom and
    /// unsaved marks removed. Falls back to the decorated title rather than
    /// returning nothing.
    /// </summary>
    public static string Clean(string? title, IReadOnlySet<string>? constantSegments = null, string? site = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var stripped = StripDecoration(title);
        var brand = site is null ? null : SiteBrand(site);
        // "animex" for animex.one, "example" for shop.example.co: how a site
        // names itself at the end of its titles.
        var siteName = site is null ? null : SiteName(site);
        var kept = Split(stripped)
            .Where(segment =>
                !KnownAppSegments.Contains(segment)
                && !(constantSegments?.Contains(segment) ?? false)
                && !(brand is not null && segment.Equals(brand, StringComparison.OrdinalIgnoreCase))
                // Captures stored before sites were recorded still name them.
                && !SiteBrands.ContainsValue(segment)
                && !(site is not null && segment.Equals(site, StringComparison.OrdinalIgnoreCase))
                && !(siteName is not null && segment.Equals(siteName, StringComparison.OrdinalIgnoreCase))
                && !OnlyNumbersRegex().IsMatch(segment))
            .ToList();
        var cleaned = string.Join(" - ", kept);
        return cleaned.Length > 0 ? cleaned : stripped;
    }

    /// The main label of a host: the part before its last one or two labels.
    internal static string? SiteName(string host)
    {
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2)
        {
            return null;
        }

        // Two-letter country suffixes after a short second level: example.co.uk.
        var index = labels.Length >= 3 && labels[^1].Length == 2 && labels[^2].Length <= 3
            ? labels.Length - 3
            : labels.Length - 2;
        return labels[index];
    }

    /// File-like tokens in a title: "poster.psd", "Program.cs".
    public static IEnumerable<string> FileTokens(string title)
    {
        foreach (Match match in FileTokenRegex().Matches(title))
        {
            yield return match.Value;
        }
    }

    /// Whitespace-collapsed, lowercased form used inside activity keys.
    public static string KeyOf(string value) =>
        string.Join(' ', value.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Removes what changes without the subject changing: unsaved marks,
    /// unread counters, Photoshop-style zoom and mode, progress percentages.
    /// </summary>
    internal static string StripDecoration(string title)
    {
        var text = title.Trim();
        text = LeadingMarkRegex().Replace(text, string.Empty);
        text = LeadingCounterRegex().Replace(text, string.Empty);
        text = ZoomRegex().Replace(text, string.Empty);
        text = TrailingMarkRegex().Replace(text, string.Empty);
        text = ProgressRegex().Replace(text, string.Empty);
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static IEnumerable<string> Split(string title) =>
        title.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(@"^[\*●•]+\s*", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingMarkRegex();

    [GeneratedRegex(@"^(\(\d+\+?\)|\[\d+\+?\])\s*", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingCounterRegex();

    [GeneratedRegex(@"\s*@\s*\d+(\.\d+)?%.*$", RegexOptions.CultureInvariant)]
    private static partial Regex ZoomRegex();

    [GeneratedRegex(@"\s*[\*●•]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingMarkRegex();

    [GeneratedRegex(@"\s*\(?\d{1,3}%\)?", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(@"^[\d\s.,:%()/+-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex OnlyNumbersRegex();

    [GeneratedRegex(@"\.[A-Za-z0-9]{1,8}\*", RegexOptions.CultureInvariant)]
    private static partial Regex UnsavedAfterFileRegex();

    [GeneratedRegex(@"[^\s\\/:*?""<>|()\[\]]+\.[A-Za-z][A-Za-z0-9]{0,7}\b", RegexOptions.CultureInvariant)]
    private static partial Regex FileTokenRegex();
}
