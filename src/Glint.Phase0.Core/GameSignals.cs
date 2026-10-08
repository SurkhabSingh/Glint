using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Glint.Phase0.Core;

/// <summary>
/// Whether a process was started by a game launcher: Steam, Epic, GOG,
/// Battle.net, EA, Ubisoft, Riot, the Xbox app and the like. A game started
/// from a launcher is a game wherever it is installed, so this catches games
/// on custom drives and folders the folder list does not know.
/// </summary>
/// <remarks>
/// Reads the process list Windows keeps for everyone (names and parents
/// only), so it needs no access to the game itself and works for games
/// running as administrator. Launcher components (web helpers, crash
/// reporters, updaters) are excluded so the store window is not a game.
/// </remarks>
public static class GameSignals
{
    /// Launcher processes, lowercase, without ".exe".
    private static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam",
        "epicgameslauncher",
        "galaxyclient",
        "battle.net",
        "eadesktop",
        "origin",
        "upc",
        "ubisoftconnect",
        "riotclientservices",
        "amazongames",
        "itch",
        "gamelaunchhelper",
        "playnite.desktopapp",
        "playnite.fullscreenapp",
        "heroic",
        "hoyoplay",
        "launcher_main"
    };

    /// Parts of process names that belong to launchers and tools, not games.
    private static readonly string[] HelperFragments =
    [
        "webhelper", "crash", "reporter", "updater", "update", "installer", "setup",
        "redist", "dxsetup", "overlay", "cefprocess", "service", "helper", "bootstrap"
    ];

    /// <summary>
    /// Files that ship with games and almost nothing else: engine runtimes
    /// (Unity, Unreal, GameMaker, Godot), game middleware (Bink video, FMOD
    /// and Wwise audio, PhysX, CRI archives), store SDKs (Steam, Epic, GOG)
    /// and the stand-ins cracked copies put next to the game.
    /// </summary>
    private static readonly HashSet<string> GameFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "unityplayer.dll", "gameassembly.dll", "unitycrashhandler64.exe", "unitycrashhandler32.exe",
        "steam_api.dll", "steam_api64.dll", "steam_appid.txt",
        "eossdk-win64-shipping.dll", "eossdk-win32-shipping.dll",
        "galaxy.dll", "galaxy64.dll", "discord_game_sdk.dll",
        "bink2w64.dll", "bink2w32.dll", "binkw32.dll", "binkw64.dll",
        "fmod.dll", "fmod64.dll", "fmodex.dll", "fmodex64.dll", "fmodstudio.dll", "fmodstudio64.dll",
        "physx3_x64.dll", "physx3_x86.dll", "physx_64.dll", "physxloader.dll",
        "data.win",
        // Left by cracks and emulated Steam/Epic layers.
        "steam_emu.ini", "steam_api.ini", "cream_api.ini", "codex.ini", "cpy.ini", "onlinefix.ini",
        "onlinefix64.dll", "smartsteamemu.ini", "coldclientloader.ini", "3dmgame.ini", "ali213.ini",
        "skidrow.ini", "empress.ini", "flt.ini", "rld.ini", "eos_emu.ini"
    };

    /// File types only games carry: CRI archives, Ren'Py, RPG Maker, Godot,
    /// Wwise sound banks.
    private static readonly HashSet<string> GameExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cpk", ".rpa", ".rgss3a", ".rgss2a", ".rgssad", ".pck", ".bnk", ".wem", ".forge", ".bdt", ".ba2", ".bsa"
    };

    /// Folders next to the game: Goldberg's Steam emulator settings, a Unity
    /// data folder ("<Game>_Data"), Unreal content.
    private static readonly string[] GameFolders = ["steam_settings", "engine"];

    private static readonly ConcurrentDictionary<string, bool> InstallCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether an executable sits in a game's install folder, judged from the
    /// files around it. Works wherever the game is and however it was
    /// started (a pirated copy, an admin prompt, a desktop shortcut), and
    /// needs no access to the running game: only the folder is listed,
    /// nothing is opened.
    /// </summary>
    public static bool LooksLikeGameInstall(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(executablePath);
        // Unreal names every packaged game "<Game>-Win64-Shipping.exe".
        if (name.EndsWith("-Shipping", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrEmpty(directory) || IsSystemFolder(directory))
        {
            return false;
        }

        return InstallCache.GetOrAdd(directory, _ => InspectFolder(directory, name));
    }

    internal static bool InspectFolder(string directory, string executableName)
    {
        try
        {
            var looked = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                // A game folder shows itself quickly; a huge folder is not one.
                if (++looked > 600)
                {
                    break;
                }

                var entryName = Path.GetFileName(entry);
                if (GameFiles.Contains(entryName)
                    || GameExtensions.Contains(Path.GetExtension(entryName))
                    || string.Equals(entryName, executableName + "_Data", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (GameFolders.Contains(entryName, StringComparer.OrdinalIgnoreCase)
                    && Directory.Exists(entry)
                    && (!string.Equals(entryName, "engine", StringComparison.OrdinalIgnoreCase)
                        || Directory.Exists(Path.Combine(entry, "Binaries"))))
                {
                    return true;
                }
            }

            // Unreal: the real exe lives in <Game>\Binaries\Win64, the content
            // two folders up in <Game>\Content\Paks.
            var parent = Directory.GetParent(directory)?.Parent;
            return parent is not null
                && string.Equals(Path.GetFileName(directory), "Win64", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(Path.Combine(parent.FullName, "Content", "Paks"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return false;
        }
    }

    /// Windows and Program Files roots hold thousands of unrelated files; a
    /// game is always in a folder of its own.
    private static bool IsSystemFolder(string directory)
    {
        var full = Path.TrimEndingDirectorySeparator(directory);
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        ];
        return roots.Any(root => !string.IsNullOrEmpty(root)
            && (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(Path.GetFileName(root), "Windows", StringComparison.OrdinalIgnoreCase)
                    && full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))));
    }

    /// How far up the parent chain to look: launcher → bootstrapper → game.
    private const int MaxDepth = 3;

    private static readonly ConcurrentDictionary<int, (string Name, bool Result)> Cache = new();

    /// Whether the process with this id (named `processName`) was launched
    /// by a game launcher, directly or through a bootstrapper.
    public static bool LaunchedByGameLauncher(int processId, string processName)
    {
        if (processId <= 0
            || string.IsNullOrWhiteSpace(processName)
            || Launchers.Contains(processName)
            || ActivityCatalog.IsBrowser(processName)
            || HelperFragments.Any(fragment => processName.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Process ids are reused: the cache entry only counts for the same name.
        if (Cache.TryGetValue(processId, out var cached)
            && string.Equals(cached.Name, processName, StringComparison.OrdinalIgnoreCase))
        {
            return cached.Result;
        }

        var result = Decide(processId, ReadProcessTable());
        if (Cache.Count > 512)
        {
            Cache.Clear();
        }

        Cache[processId] = (processName, result);
        return result;
    }

    /// The decision over a process table (id → parent id and name); separate
    /// so it can be tested without a real process list.
    internal static bool Decide(int processId, IReadOnlyDictionary<int, (int ParentId, string Name)> table)
    {
        var current = processId;
        var seen = new HashSet<int> { current };
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            if (!table.TryGetValue(current, out var entry) || entry.ParentId <= 0 || !seen.Add(entry.ParentId))
            {
                return false;
            }

            if (!table.TryGetValue(entry.ParentId, out var parent))
            {
                return false;
            }

            if (Launchers.Contains(StripExe(parent.Name)))
            {
                return true;
            }

            current = entry.ParentId;
        }

        return false;
    }

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static Dictionary<int, (int ParentId, string Name)> ReadProcessTable()
    {
        var table = new Dictionary<int, (int, string)>();
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == 0 || snapshot == -1)
        {
            return table;
        }

        try
        {
            var entry = new NativeMethods.ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>(),
                ExeFile = string.Empty
            };
            if (!NativeMethods.Process32First(snapshot, ref entry))
            {
                return table;
            }

            do
            {
                table[(int)entry.ProcessId] = ((int)entry.ParentProcessId, entry.ExeFile ?? string.Empty);
            }
            while (NativeMethods.Process32Next(snapshot, ref entry));
        }
        finally
        {
            _ = NativeMethods.CloseHandle(snapshot);
        }

        return table;
    }
}
