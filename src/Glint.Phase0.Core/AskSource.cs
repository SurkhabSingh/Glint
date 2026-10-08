namespace Glint.Phase0.Core;

/// Ask's view of the encrypted store: sealed activities plus what is being
/// recorded right now.
public sealed class DatabaseAskSource : IAskSource
{
    private readonly Phase0Database _database;

    public DatabaseAskSource(Phase0Database database)
    {
        _database = database;
    }

    public IReadOnlyList<ActivityRecord> GetActivitiesBetween(long fromMilliseconds, long toMilliseconds) =>
        _database.GetActivitiesBetween(fromMilliseconds, toMilliseconds);

    public IReadOnlyList<ActivityRecord> GetLiveActivities() =>
        new ActivityBuilder(_database, _database, null).BuildLive();

    public ActivityRecord? GetLatestActivity() =>
        _database.GetRecentActivities(1).FirstOrDefault();
}
