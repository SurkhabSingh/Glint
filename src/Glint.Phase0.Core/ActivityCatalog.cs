using Microsoft.Win32;

namespace Glint.Phase0.Core;

/// <summary>
/// Built-in knowledge of apps, sites and game folders. Only a starting point:
/// unknown apps are learned from how they behave, and the user's choice
/// overrides everything here.
/// </summary>
public static class ActivityCatalog
{
    private static readonly Dictionary<string, (ActivityMode Mode, ActivityCategory Category)> Processes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Browsers. Their mode comes from the site; this is the fallback.
            ["chrome"] = (ActivityMode.Read, ActivityCategory.Browsing),
            ["msedge"] = (ActivityMode.Read, ActivityCategory.Browsing),
            ["firefox"] = (ActivityMode.Read, ActivityCategory.Browsing),
            ["zen"] = (ActivityMode.Read, ActivityCategory.Browsing),
            ["brave"] = (ActivityMode.Read, ActivityCategory.Browsing),
            ["opera"] = (ActivityMode.Read, ActivityCategory.Browsing),
            ["opera_gx"] = (ActivityMode.Read, ActivityCategory.Browsing),
            ["vivaldi"] = (ActivityMode.Read, ActivityCategory.Browsing),
            ["arc"] = (ActivityMode.Read, ActivityCategory.Browsing),
            ["dia"] = (ActivityMode.Read, ActivityCategory.Browsing),
            ["comet"] = (ActivityMode.Read, ActivityCategory.Browsing),
            ["librewolf"] = (ActivityMode.Read, ActivityCategory.Browsing),
            ["waterfox"] = (ActivityMode.Read, ActivityCategory.Browsing),
            ["floorp"] = (ActivityMode.Read, ActivityCategory.Browsing),
            ["thorium"] = (ActivityMode.Read, ActivityCategory.Browsing),
            ["chromium"] = (ActivityMode.Read, ActivityCategory.Browsing),

            ["outlook"] = (ActivityMode.Read, ActivityCategory.Email),
            ["olk"] = (ActivityMode.Read, ActivityCategory.Email),
            ["thunderbird"] = (ActivityMode.Read, ActivityCategory.Email),
            ["mailspring"] = (ActivityMode.Read, ActivityCategory.Email),

            ["discord"] = (ActivityMode.Read, ActivityCategory.Chat),
            ["slack"] = (ActivityMode.Read, ActivityCategory.Chat),
            ["teams"] = (ActivityMode.Read, ActivityCategory.Chat),
            ["ms-teams"] = (ActivityMode.Read, ActivityCategory.Chat),
            ["whatsapp"] = (ActivityMode.Read, ActivityCategory.Chat),
            ["whatsapp.root"] = (ActivityMode.Read, ActivityCategory.Chat),
            ["telegram"] = (ActivityMode.Read, ActivityCategory.Chat),
            ["zoom"] = (ActivityMode.Read, ActivityCategory.Chat),

            ["code"] = (ActivityMode.Read, ActivityCategory.Coding),
            ["cursor"] = (ActivityMode.Read, ActivityCategory.Coding),
            ["windsurf"] = (ActivityMode.Read, ActivityCategory.Coding),
            ["zed"] = (ActivityMode.Read, ActivityCategory.Coding),
            ["devenv"] = (ActivityMode.Read, ActivityCategory.Coding),
            ["rider64"] = (ActivityMode.Read, ActivityCategory.Coding),
            ["idea64"] = (ActivityMode.Read, ActivityCategory.Coding),
            ["pycharm64"] = (ActivityMode.Read, ActivityCategory.Coding),
            ["webstorm64"] = (ActivityMode.Read, ActivityCategory.Coding),
            ["sublime_text"] = (ActivityMode.Read, ActivityCategory.Coding),
            ["notepad++"] = (ActivityMode.Read, ActivityCategory.Coding),
            ["windowsterminal"] = (ActivityMode.Read, ActivityCategory.Coding),
            ["opencode"] = (ActivityMode.Read, ActivityCategory.Coding),
            ["powershell"] = (ActivityMode.Read, ActivityCategory.Coding),
            ["pwsh"] = (ActivityMode.Read, ActivityCategory.Coding),
            ["cmd"] = (ActivityMode.Read, ActivityCategory.Coding),

            ["winword"] = (ActivityMode.Read, ActivityCategory.Docs),
            ["excel"] = (ActivityMode.Read, ActivityCategory.Docs),
            ["powerpnt"] = (ActivityMode.Read, ActivityCategory.Docs),
            ["onenote"] = (ActivityMode.Read, ActivityCategory.Docs),
            ["acrobat"] = (ActivityMode.Read, ActivityCategory.Docs),
            ["acrord32"] = (ActivityMode.Read, ActivityCategory.Docs),
            ["sumatrapdf"] = (ActivityMode.Read, ActivityCategory.Docs),
            ["notepad"] = (ActivityMode.Read, ActivityCategory.Docs),
            ["obsidian"] = (ActivityMode.Read, ActivityCategory.Docs),
            ["notion"] = (ActivityMode.Read, ActivityCategory.Docs),

            ["claude"] = (ActivityMode.Read, ActivityCategory.Assistant),
            ["chatgpt"] = (ActivityMode.Read, ActivityCategory.Assistant),
            ["copilot"] = (ActivityMode.Read, ActivityCategory.Assistant),

            ["explorer"] = (ActivityMode.Read, ActivityCategory.Files),
            ["treesizefree"] = (ActivityMode.Read, ActivityCategory.Files),

            ["photoshop"] = (ActivityMode.Make, ActivityCategory.Design),
            ["illustrator"] = (ActivityMode.Make, ActivityCategory.Design),
            ["indesign"] = (ActivityMode.Make, ActivityCategory.Design),
            ["afterfx"] = (ActivityMode.Make, ActivityCategory.Design),
            ["adobe premiere pro"] = (ActivityMode.Make, ActivityCategory.Design),
            ["lightroom"] = (ActivityMode.Make, ActivityCategory.Photo),
            ["lightroomcc"] = (ActivityMode.Make, ActivityCategory.Photo),
            ["blender"] = (ActivityMode.Make, ActivityCategory.Design),
            ["figma"] = (ActivityMode.Make, ActivityCategory.Design),
            ["krita"] = (ActivityMode.Make, ActivityCategory.Design),
            ["inkscape"] = (ActivityMode.Make, ActivityCategory.Design),
            ["resolve"] = (ActivityMode.Make, ActivityCategory.Design),
            ["clipstudiopaint"] = (ActivityMode.Make, ActivityCategory.Design),
            ["aseprite"] = (ActivityMode.Make, ActivityCategory.Design),
            ["fl64"] = (ActivityMode.Make, ActivityCategory.Design),
            ["reaper"] = (ActivityMode.Make, ActivityCategory.Design),
            ["audacity"] = (ActivityMode.Make, ActivityCategory.Design),

            ["vlc"] = (ActivityMode.Watch, ActivityCategory.Video),
            ["mpc-hc64"] = (ActivityMode.Watch, ActivityCategory.Video),
            ["mpc-be64"] = (ActivityMode.Watch, ActivityCategory.Video),
            ["potplayermini64"] = (ActivityMode.Watch, ActivityCategory.Video),
            ["mpv"] = (ActivityMode.Watch, ActivityCategory.Video),
            ["spotify"] = (ActivityMode.Watch, ActivityCategory.Music),
            ["applemusic"] = (ActivityMode.Watch, ActivityCategory.Music),

            ["hd-player"] = (ActivityMode.Play, ActivityCategory.Game),
            ["osu!"] = (ActivityMode.Play, ActivityCategory.Game),
            ["robloxplayerbeta"] = (ActivityMode.Play, ActivityCategory.Game),
            ["minecraft.windows"] = (ActivityMode.Play, ActivityCategory.Game),

            // Game launchers are storefronts and libraries: text, not play.
            ["steamwebhelper"] = (ActivityMode.Read, ActivityCategory.Game),
            ["steam"] = (ActivityMode.Read, ActivityCategory.Game),
            ["epicgameslauncher"] = (ActivityMode.Read, ActivityCategory.Game),
            ["riotclientux"] = (ActivityMode.Read, ActivityCategory.Game)
        };

    /// Process names that are web browsers. Shape detection (a document plus
    /// an address bar) covers browsers missing from this list.
    public static readonly IReadOnlySet<string> BrowserProcesses = new HashSet<string>(
        Processes.Where(entry => entry.Value.Category == ActivityCategory.Browsing).Select(entry => entry.Key),
        StringComparer.OrdinalIgnoreCase);

    private static readonly (string Host, ActivityMode Mode, ActivityCategory Category)[] Sites =
    [
        ("mail.google.com", ActivityMode.Read, ActivityCategory.Email),
        ("outlook.live.com", ActivityMode.Read, ActivityCategory.Email),
        ("outlook.office.com", ActivityMode.Read, ActivityCategory.Email),
        ("outlook.office365.com", ActivityMode.Read, ActivityCategory.Email),
        ("mail.yahoo.com", ActivityMode.Read, ActivityCategory.Email),
        ("mail.proton.me", ActivityMode.Private, ActivityCategory.Email),
        ("discord.com", ActivityMode.Read, ActivityCategory.Chat),
        ("web.whatsapp.com", ActivityMode.Read, ActivityCategory.Chat),
        ("web.telegram.org", ActivityMode.Read, ActivityCategory.Chat),
        ("messenger.com", ActivityMode.Read, ActivityCategory.Chat),
        ("app.slack.com", ActivityMode.Read, ActivityCategory.Chat),
        ("teams.microsoft.com", ActivityMode.Read, ActivityCategory.Chat),
        ("instagram.com", ActivityMode.Read, ActivityCategory.Chat),
        ("x.com", ActivityMode.Read, ActivityCategory.Browsing),
        ("reddit.com", ActivityMode.Read, ActivityCategory.Browsing),
        ("music.youtube.com", ActivityMode.Watch, ActivityCategory.Music),
        ("open.spotify.com", ActivityMode.Watch, ActivityCategory.Music),
        ("soundcloud.com", ActivityMode.Watch, ActivityCategory.Music),
        ("youtube.com", ActivityMode.Watch, ActivityCategory.Video),
        ("netflix.com", ActivityMode.Watch, ActivityCategory.Video),
        ("primevideo.com", ActivityMode.Watch, ActivityCategory.Video),
        ("hotstar.com", ActivityMode.Watch, ActivityCategory.Video),
        ("twitch.tv", ActivityMode.Watch, ActivityCategory.Video),
        ("crunchyroll.com", ActivityMode.Watch, ActivityCategory.Video),
        ("coursera.org", ActivityMode.Read, ActivityCategory.Learning),
        ("udemy.com", ActivityMode.Read, ActivityCategory.Learning),
        ("khanacademy.org", ActivityMode.Read, ActivityCategory.Learning),
        ("leetcode.com", ActivityMode.Read, ActivityCategory.Learning),
        ("wikipedia.org", ActivityMode.Read, ActivityCategory.Learning),
        ("chatgpt.com", ActivityMode.Read, ActivityCategory.Assistant),
        ("claude.ai", ActivityMode.Read, ActivityCategory.Assistant),
        ("gemini.google.com", ActivityMode.Read, ActivityCategory.Assistant),
        ("perplexity.ai", ActivityMode.Read, ActivityCategory.Assistant),
        ("copilot.microsoft.com", ActivityMode.Read, ActivityCategory.Assistant),
        ("github.com", ActivityMode.Read, ActivityCategory.Coding),
        ("stackoverflow.com", ActivityMode.Read, ActivityCategory.Coding),
        ("docs.google.com", ActivityMode.Read, ActivityCategory.Docs),
        ("notion.so", ActivityMode.Read, ActivityCategory.Docs),
        ("figma.com", ActivityMode.Make, ActivityCategory.Design),
        ("canva.com", ActivityMode.Make, ActivityCategory.Design),
        ("photopea.com", ActivityMode.Make, ActivityCategory.Design),
        ("krunker.io", ActivityMode.Play, ActivityCategory.Game),
        ("poki.com", ActivityMode.Play, ActivityCategory.Game),
        ("crazygames.com", ActivityMode.Play, ActivityCategory.Game),
        ("now.gg", ActivityMode.Play, ActivityCategory.Game),
        ("play.geforcenow.com", ActivityMode.Play, ActivityCategory.Game),
        ("chess.com", ActivityMode.Play, ActivityCategory.Game),
        ("lichess.org", ActivityMode.Play, ActivityCategory.Game),
        ("paypal.com", ActivityMode.Private, ActivityCategory.Finance),
        ("robinhood.com", ActivityMode.Private, ActivityCategory.Finance),
        ("zerodha.com", ActivityMode.Private, ActivityCategory.Finance),
        ("groww.in", ActivityMode.Private, ActivityCategory.Finance),
        ("coinbase.com", ActivityMode.Private, ActivityCategory.Finance),
        ("binance.com", ActivityMode.Private, ActivityCategory.Finance),
        ("tradingview.com", ActivityMode.Private, ActivityCategory.Finance)
    ];

    /// Words in a host name that mean the site is for watching. Unknown
    /// video sites are also learned from what plays on them.
    private static readonly string[] WatchHostWords =
    [
        "anime", "movie", "flix", "cinema", "kdrama", "dramacool"
    ];

    /// Words in a host name that mean money moves there.
    private static readonly string[] FinanceHostWords =
    [
        "bank", "billpay", "billing", "invoice", "broker", "trading", "wallet",
        "payments", "pay.", ".pay", "creditcard", "netbanking"
    ];

    /// Folders that only hold installed games.
    private static readonly string[] GameFolders =
    [
        @"\steamapps\common\",
        @"\epic games\",
        @"\xboxgames\",
        @"\gog galaxy\games\",
        @"\riot games\",
        @"\ubisoft game launcher\games\",
        @"\ea games\",
        @"\battle.net\",
        @"\bluestacks"
    ];

    /// Files an app is "making": creative work tracked by file, not text.
    private static readonly string[] MakeExtensions =
    [
        ".psd", ".psb", ".ai", ".indd", ".afphoto", ".afdesign", ".kra", ".xcf",
        ".clip", ".aseprite", ".blend", ".prproj", ".aep", ".drp", ".flp", ".als",
        ".rpp", ".aup3", ".fig", ".sketch", ".lrcat", ".cr2", ".cr3", ".nef",
        ".arw", ".dng", ".raf", ".tif", ".tiff"
    ];

    public static (ActivityMode Mode, ActivityCategory Category)? ForProcess(string processName) =>
        Processes.TryGetValue(processName, out var entry) ? entry : null;

    public static (ActivityMode Mode, ActivityCategory Category)? ForSite(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var normalized = host.ToLowerInvariant();
        foreach (var (siteHost, mode, category) in Sites)
        {
            if (normalized == siteHost || normalized.EndsWith("." + siteHost, StringComparison.Ordinal))
            {
                return (mode, category);
            }
        }

        if (FinanceHostWords.Any(word => normalized.Contains(word, StringComparison.Ordinal)))
        {
            return (ActivityMode.Private, ActivityCategory.Finance);
        }

        if (WatchHostWords.Any(word => normalized.Contains(word, StringComparison.Ordinal)))
        {
            return (ActivityMode.Watch, ActivityCategory.Video);
        }

        return null;
    }

    public static bool IsBrowser(string processName) => BrowserProcesses.Contains(processName);

    public static bool IsInGameFolder(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var lower = executablePath.Replace('/', '\\').ToLowerInvariant();
        return GameFolders.Any(folder => lower.Contains(folder, StringComparison.Ordinal));
    }

    /// The file being worked on, if the title names one with a creative extension.
    public static string? MakeFileIn(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        foreach (var token in TitleNormalizer.FileTokens(title))
        {
            var extension = Path.GetExtension(token);
            if (MakeExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return token;
            }
        }

        return null;
    }

    private static readonly Lazy<IReadOnlySet<string>> GameBarList = new(ReadGameBarList);

    /// <summary>
    /// Executables Windows' own Game Bar has recognized as games on this
    /// machine. Read once per process; an unreadable registry yields nothing.
    /// </summary>
    public static bool IsKnownToGameBar(string? executablePath) =>
        !string.IsNullOrWhiteSpace(executablePath)
        && GameBarList.Value.Contains(Path.GetFullPath(executablePath));

    private static IReadOnlySet<string> ReadGameBarList()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            if (children is null)
            {
                return paths;
            }

            foreach (var name in children.GetSubKeyNames())
            {
                using var child = children.OpenSubKey(name);
                if (child?.GetValue("MatchedExeFullPath") is string path && !string.IsNullOrWhiteSpace(path))
                {
                    try
                    {
                        paths.Add(Path.GetFullPath(path));
                    }
                    catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        // A malformed entry says nothing about other games.
                    }
                }
            }
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // No access means no extra signal, never a failed capture.
        }

        return paths;
    }
}
