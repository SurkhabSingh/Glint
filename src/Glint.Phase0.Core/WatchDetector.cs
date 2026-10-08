using System.Text.RegularExpressions;

namespace Glint.Phase0.Core;

/// <summary>
/// Notices that the user is watching something, on any site and in any app,
/// so the look is treated as Watch (title and time, nothing read) instead of
/// as a page whose subtitles, time code and video frames keep "changing".
/// </summary>
/// <remarks>
/// A list of video sites can never be complete: the episode that exposed this
/// was on a site nobody would have listed. Three signals that do not depend
/// on the site, strongest first:
///
///   1. Windows says media is playing, and its title is this window's page.
///      Browsers report page media to the system media controls with the
///      page title when the site gives no metadata, so the match is by title.
///      For a browser, a session from the same app with another title is a
///      background tab and does not count.
///   2. The window fills the screen and most of the picture is moving.
///   3. The page carries a media player's position label ("3 seconds of
///      22 minutes") and a good part of the picture is moving.
/// </remarks>
public static partial class WatchDetector
{
    /// Share of the picture that must move for full-screen content to count as video.
    public const double FullscreenMotionShare = 0.20;

    /// Share that must move alongside a player's controls.
    public const double PlayerMotionShare = 0.10;

    /// <summary>
    /// Share of the picture that must have kept moving across recent looks.
    /// One big change (a slide advancing, a page loading) is not a video;
    /// a video moves look after look.
    /// </summary>
    public const double SustainedMotionShare = 0.15;

    public static string? Detect(
        ForegroundWindowInfo window,
        string subject,
        bool isBrowser,
        IReadOnlyList<MediaPlayback> media,
        FrameDiff? diff,
        string? pageText)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (MediaMatches(window, subject, isBrowser, media))
        {
            return "media";
        }

        var moved = diff is { FirstLook: false } ? (double)diff.ChangedCells / FrameSignature.CellCount : 0;
        var sustained = diff is { FirstLook: false }
            ? (double)diff.LiveCounts.Count(count => count >= 2) / FrameSignature.CellCount
            : 0;
        if (window.IsFullscreen && moved >= FullscreenMotionShare && sustained >= SustainedMotionShare)
        {
            return "fullscreen-motion";
        }

        if (moved >= PlayerMotionShare && pageText is not null && PlayerPositionRegex().IsMatch(pageText))
        {
            return "player";
        }

        return null;
    }

    public static bool MediaMatches(
        ForegroundWindowInfo window,
        string subject,
        bool isBrowser,
        IReadOnlyList<MediaPlayback> media)
    {
        ArgumentNullException.ThrowIfNull(window);
        foreach (var playback in media ?? [])
        {
            if (!playback.Playing)
            {
                continue;
            }

            var title = playback.Title.Trim();
            if (title.Length >= 4 && window.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (subject.Length >= 8 && title.Contains(subject, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // A desktop player owns its media outright. A browser does not:
            // its other tabs play too, and only the title says which.
            if (!isBrowser
                && playback.SourceApp.Length > 0
                && playback.SourceApp.Contains(window.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"\b\d+\s+(second|seconds|minute|minutes|hour|hours)\s+of\s+\d+\s+(second|seconds|minute|minutes|hour|hours)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlayerPositionRegex();
}
