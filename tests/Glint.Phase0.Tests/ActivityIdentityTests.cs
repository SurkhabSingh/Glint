using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

/// What counts as "the same thing" across the apps that broke the old model.
public sealed class ActivityIdentityTests
{
    [Fact]
    public void PhotoshopZoomAndUnsavedMarkDoNotChangeTheFile()
    {
        var before = Compose("Photoshop", "Adobe Photoshop", "poster.psd @ 50% (RGB/8)", ActivityMode.Make, ActivityCategory.Design);
        var after = Compose("Photoshop", "Adobe Photoshop", "poster.psd @ 66.7% (Layer 2, RGB/8) *", ActivityMode.Make, ActivityCategory.Design);

        Assert.Equal(before.Key, after.Key);
        Assert.Equal("poster.psd", after.Subject);
        Assert.False(before.Unsaved);
        Assert.True(after.Unsaved);
    }

    [Fact]
    public void AGameStaysOneThingWhateverItsTitleSays()
    {
        var menu = Compose("osu!", "osu!", "osu! - Main Menu", ActivityMode.Play, ActivityCategory.Game);
        var song = Compose("osu!", "osu!", "osu! - Camellia - Exit This Earth's Atomosphere [Extra]", ActivityMode.Play, ActivityCategory.Game);
        var fps = Compose("osu!", "osu!", "osu! (144 fps)", ActivityMode.Play, ActivityCategory.Game);

        Assert.Equal(menu.Key, song.Key);
        Assert.Equal(menu.Key, fps.Key);
        Assert.Equal("osu!", menu.Subject);
    }

    [Fact]
    public void LightroomModulesArePhasesOfOneActivity()
    {
        var library = Compose("lightroom", "Adobe Lightroom Classic", "Lightroom Catalog-v13 - Adobe Photoshop Lightroom Classic - Library", ActivityMode.Make, ActivityCategory.Photo);
        var develop = Compose("lightroom", "Adobe Lightroom Classic", "Lightroom Catalog-v13 - Adobe Photoshop Lightroom Classic - Develop", ActivityMode.Make, ActivityCategory.Photo);

        Assert.Equal(library.Key, develop.Key);
        Assert.NotEqual(library.Phase, develop.Phase);
    }

    [Fact]
    public void AnInboxIsOneActivityWithThreadsAsPhases()
    {
        var inbox = Compose("zen", "Zen", "(3) Inbox - Gmail - Zen Browser", ActivityMode.Read, ActivityCategory.Email, "mail.google.com");
        var thread = Compose("zen", "Zen", "Re: Venue deposit - Gmail - Zen Browser", ActivityMode.Read, ActivityCategory.Email, "mail.google.com");

        Assert.Equal(inbox.Key, thread.Key);
        Assert.Equal("Gmail", inbox.Subject);
        Assert.Equal("Inbox", inbox.Phase);
        Assert.Equal("Re: Venue deposit", thread.Phase);
    }

    [Fact]
    public void EachVideoIsItsOwnThing()
    {
        var first = Compose("zen", "Zen", "(2) Dynamic programming patterns - YouTube - Zen Browser", ActivityMode.Watch, ActivityCategory.Video, "youtube.com");
        var second = Compose("zen", "Zen", "Lo-fi beats to study to - YouTube - Zen Browser", ActivityMode.Watch, ActivityCategory.Video, "youtube.com");

        Assert.NotEqual(first.Key, second.Key);
        Assert.Equal("Dynamic programming patterns", first.Subject);
    }

    [Fact]
    public void CodeIsGroupedByProjectWithFilesAsPhases()
    {
        var first = Compose("Code", "Visual Studio Code", "● Program.cs - glint - Visual Studio Code", ActivityMode.Read, ActivityCategory.Coding);
        var second = Compose("Code", "Visual Studio Code", "SessionBuilder.cs - glint - Visual Studio Code", ActivityMode.Read, ActivityCategory.Coding);

        Assert.Equal(first.Key, second.Key);
        Assert.Equal("glint", first.Subject);
        Assert.Equal("Program.cs", first.Phase);
    }

    [Fact]
    public void ConstantTitleSegmentsAreLearnedPerApp()
    {
        var learned = TitleNormalizer.LearnConstantSegments(
        [
            "Report.docx - My Editor Pro",
            "Notes.txt - My Editor Pro",
            "Plan.md - My Editor Pro",
            "Todo.md - My Editor Pro"
        ]);

        Assert.Contains("My Editor Pro", learned);
        Assert.Equal("Notes.txt", TitleNormalizer.Clean("Notes.txt - My Editor Pro", learned));
    }

    [Fact]
    public void OneLongLivedWindowIsNeverStrippedBare()
    {
        var learned = TitleNormalizer.LearnConstantSegments(["Stellar Blade", "Stellar Blade"]);

        Assert.Empty(learned);
        Assert.Equal("Stellar Blade", TitleNormalizer.Clean("Stellar Blade", learned));
    }

    [Fact]
    public void SiteNamesAreStrippedEvenWhenTheSiteWasNotRecorded()
    {
        Assert.Equal("Dynamic programming patterns", TitleNormalizer.Clean("Dynamic programming patterns - YouTube - Zen Browser"));
    }

    [Fact]
    public void ASiteNamingItselfAtTheEndOfTitlesIsStripped()
    {
        Assert.Equal(
            "Cyberpunk: Edgerunners Episode 2 English Sub/Dub",
            TitleNormalizer.Clean("Cyberpunk: Edgerunners Episode 2 English Sub/Dub - AnimeX — Zen Browser", null, "animex.one"));
        Assert.Equal("example", TitleNormalizer.SiteName("shop.example.co.uk"));
        Assert.Equal("animex", TitleNormalizer.SiteName("animex.one"));
    }

    [Fact]
    public void AssistantsAndVideoSitesHaveTheirOwnCategories()
    {
        Assert.Equal((ActivityMode.Read, ActivityCategory.Assistant), ActivityCatalog.ForProcess("claude"));
        Assert.Equal((ActivityMode.Read, ActivityCategory.Assistant), ActivityCatalog.ForSite("claude.ai"));
        Assert.Equal((ActivityMode.Watch, ActivityCategory.Video), ActivityCatalog.ForSite("animex.one"));
        Assert.Null(ActivityCatalog.ForSite("player.example.org"));
    }

    [Fact]
    public void ABrowsersOtherTabsNeverCountAsThisWindowPlaying()
    {
        var window = new ForegroundWindowInfo(
            1, 1, "zen", null, "Inbox - Gmail — Zen Browser", new WindowBounds(0, 0, 10, 10),
            false, false, true, false, false, true, false);
        var spotify = window with { ProcessName = "Spotify", Title = "Spotify Premium" };
        var media = new[] { new MediaPlayback("zen", "Lo-fi beats", string.Empty, true) };

        Assert.False(WatchDetector.MediaMatches(window, "Inbox", true, media));
        Assert.True(WatchDetector.MediaMatches(spotify, "Spotify Premium", false, [new MediaPlayback("Spotify.exe", "Song", "Artist", true)]));
        Assert.False(WatchDetector.MediaMatches(window, "Inbox", true, [new MediaPlayback("x", "Inbox - Gmail", string.Empty, false)]));
    }

    [Fact]
    public void VeryLongTitlesAreCapped()
    {
        var identity = Compose("zen", "Zen", new string('a', 300), ActivityMode.Read, ActivityCategory.Browsing);

        Assert.True(identity.Subject.Length <= ActivityIdentityResolver.MaxSubjectCharacters + 1);
    }

    [Theory]
    [InlineData("https://mail.google.com/mail/u/0/#inbox", "mail.google.com")]
    [InlineData("youtube.com/watch?v=abc123", "youtube.com")]
    [InlineData("www.example.co.uk:8443/path?token=secret", "example.co.uk")]
    [InlineData("https://user:pass@bank.example.com/login", "bank.example.com")]
    [InlineData("how to cook rice", null)]
    [InlineData("about:blank", null)]
    [InlineData("chrome://settings", null)]
    [InlineData("192.168.1.1", null)]
    [InlineData("", null)]
    public void SitesAreReadFromTheAddressBarHostOnly(string value, string? expected)
    {
        Assert.Equal(expected, SiteParser.TryParseHost(value));
    }

    [Fact]
    public void GameFoldersAndCreativeFilesAreRecognized()
    {
        Assert.True(ActivityCatalog.IsInGameFolder(@"D:\SteamLibrary\steamapps\common\Stellar Blade\SB-Win64-Shipping.exe"));
        Assert.False(ActivityCatalog.IsInGameFolder(@"C:\Program Files\Adobe\Photoshop.exe"));
        Assert.Equal("poster.psd", ActivityCatalog.MakeFileIn("poster.psd @ 50% (RGB/8) *"));
        Assert.Null(ActivityCatalog.MakeFileIn("Program.cs - glint - Visual Studio Code"));
        Assert.Equal((ActivityMode.Private, ActivityCategory.Finance), ActivityCatalog.ForSite("netbanking.examplebank.com"));
        Assert.Equal((ActivityMode.Watch, ActivityCategory.Music), ActivityCatalog.ForSite("music.youtube.com"));
        Assert.Equal((ActivityMode.Read, ActivityCategory.Browsing), ActivityCatalog.ForProcess("zen"));
    }

    private static PageIdentity Compose(
        string process,
        string app,
        string title,
        ActivityMode mode,
        ActivityCategory category,
        string? site = null) =>
        ActivityIdentityResolver.Compose(process, app, title, site, mode, category, null);
}
