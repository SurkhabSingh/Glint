namespace Glint.Phase0.Core;

/// Ask's view of the encrypted store: activities worked out on demand,
/// including what is being recorded right now.
public sealed class ViewAskSource : IAskSource
{
    private readonly ActivityView _view;
    private readonly Func<long> _clock;

    public ViewAskSource(IActivityViewStore store, Func<long>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _view = new ActivityView(store, _clock);
    }

    public IReadOnlyList<ActivityRecord> GetActivitiesBetween(long fromMilliseconds, long toMilliseconds) =>
        _view.Build(fromMilliseconds, toMilliseconds).Activities;

    /// Already part of every window that reaches the present.
    public IReadOnlyList<ActivityRecord> GetLiveActivities() => [];

    public ActivityRecord? GetLatestActivity()
    {
        var now = _clock();
        foreach (var days in new[] { 1, 7, 30, 365 })
        {
            var latest = _view.Build(now - days * 86_400_000L, now + 1).Activities
                .MaxBy(activity => activity.StartedAtMilliseconds);
            if (latest is not null)
            {
                return latest;
            }
        }

        return null;
    }
}
