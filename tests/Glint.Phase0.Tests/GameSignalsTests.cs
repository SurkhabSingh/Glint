using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class GameSignalsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "glint-games-" + Guid.NewGuid().ToString("N"));

    private string Folder(string name, params string[] entries)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        foreach (var entry in entries)
        {
            if (entry.EndsWith('/'))
            {
                Directory.CreateDirectory(Path.Combine(folder, entry.TrimEnd('/')));
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, entry))!);
                File.WriteAllText(Path.Combine(folder, entry), string.Empty);
            }
        }

        return folder;
    }

    [Fact]
    public void ACrackedCopyStartedFromAnywhereIsAGame()
    {
        // A pirated port in a downloads folder: no store, no launcher, but CRI
        // archives and the Steam stand-ins next to the exe.
        var folder = Folder("Persona 4 Golden", "P4G.exe", "data.cpk", "steam_api.dll", "steam_emu.ini");

        Assert.True(GameSignals.LooksLikeGameInstall(Path.Combine(folder, "P4G.exe")));
    }

    [Fact]
    public void EngineLayoutsAreGames()
    {
        var unity = Folder("Hollow", "Hollow.exe", "Hollow_Data/");
        var unreal = Folder(@"Stellar\SB\Binaries\Win64", "SB-Win64-Shipping.exe");
        var unrealBootstrap = Folder("Wuthering", "Client.exe", "Engine/Binaries/");
        var renpy = Folder("VisualNovel", "VN.exe", "game/archive.rpa", "archive.rpa");

        Assert.True(GameSignals.LooksLikeGameInstall(Path.Combine(unity, "Hollow.exe")));
        Assert.True(GameSignals.LooksLikeGameInstall(Path.Combine(unreal, "SB-Win64-Shipping.exe")));
        Assert.True(GameSignals.LooksLikeGameInstall(Path.Combine(unrealBootstrap, "Client.exe")));
        Assert.True(GameSignals.LooksLikeGameInstall(Path.Combine(renpy, "VN.exe")));
    }

    [Fact]
    public void OrdinaryAppsAreNot()
    {
        // An Electron app ships .pak files and an "engine"-free layout.
        var electron = Folder("Notes", "Notes.exe", "resources.pak", "chrome_100_percent.pak", "ffmpeg.dll", "locales/");
        var tool = Folder("Tool", "tool.exe", "README.txt", "config.json");

        Assert.False(GameSignals.LooksLikeGameInstall(Path.Combine(electron, "Notes.exe")));
        Assert.False(GameSignals.LooksLikeGameInstall(Path.Combine(tool, "tool.exe")));
        Assert.False(GameSignals.LooksLikeGameInstall(null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static readonly Dictionary<int, (int ParentId, string Name)> Table = new()
    {
        [1] = (0, "explorer.exe"),
        [10] = (1, "steam.exe"),
        [11] = (10, "P4G.exe"),
        [12] = (10, "steamwebhelper.exe"),
        [20] = (1, "EpicGamesLauncher.exe"),
        [21] = (20, "Bootstrapper.exe"),
        [22] = (21, "Game-Win64-Shipping.exe"),
        [30] = (1, "notepad.exe"),
        [40] = (99, "orphan.exe")
    };

    [Theory]
    [InlineData(11, true)]
    [InlineData(22, true)]
    [InlineData(30, false)]
    [InlineData(40, false)]
    [InlineData(1, false)]
    public void AGameIsWhatALauncherStarted(int processId, bool expected) =>
        Assert.Equal(expected, GameSignals.Decide(processId, Table));

    [Fact]
    public void LauncherPartsAreNotGames() =>
        Assert.False(GameSignals.LaunchedByGameLauncher(12, "steamwebhelper"));
}
