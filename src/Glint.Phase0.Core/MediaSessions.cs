using Windows.Media.Control;

namespace Glint.Phase0.Core;

/// <param name="SourceApp">The app Windows attributes the media to ("Chrome", "Spotify.exe", or an id).</param>
/// <param name="Title">Title the player reports; browsers fall back to the page title.</param>
public sealed record MediaPlayback(string SourceApp, string Title, string Artist, bool Playing);

public interface IMediaSessionReader
{
    IReadOnlyList<MediaPlayback> Read();
}

/// <summary>
/// What Windows itself says is playing: the same sessions that drive the
/// media overlay on the volume keys. Browsers, Spotify, VLC and most players
/// report here, whatever site or app the media comes from, which is what makes
/// "is a video playing in this window?" answerable without a list of sites.
/// </summary>
public sealed class WindowsMediaSessionReader : IMediaSessionReader
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(600);

    public IReadOnlyList<MediaPlayback> Read()
    {
        try
        {
            // Bounded: a slow media service must never stall a look.
            var reading = ReadCoreAsync();
            return reading.Wait(Budget) ? reading.Result : [];
        }
        catch (Exception error) when (error is AggregateException or InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // No media information is never a failed look.
            return [];
        }
    }

    private static async Task<IReadOnlyList<MediaPlayback>> ReadCoreAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var playbacks = new List<MediaPlayback>();
        foreach (var session in manager.GetSessions())
        {
            var status = session.GetPlaybackInfo()?.PlaybackStatus;
            string title = string.Empty;
            string artist = string.Empty;
            try
            {
                var properties = await session.TryGetMediaPropertiesAsync();
                title = properties?.Title ?? string.Empty;
                artist = properties?.Artist ?? string.Empty;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Some players refuse properties; their playing state still counts.
            }

            playbacks.Add(new MediaPlayback(
                session.SourceAppUserModelId ?? string.Empty,
                title,
                artist,
                status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing));
        }

        return playbacks;
    }
}
