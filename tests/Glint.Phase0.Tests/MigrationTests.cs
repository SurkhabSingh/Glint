using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

/// Upgrades run when any Glint process opens the store, and several open it
/// at once. They must never be left half applied.
public sealed class MigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "glint-phase0-tests",
        Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "memory.db");

    private Phase0Database Open() =>
        Phase0Database.Open(DatabasePath, new DpapiKeyStore(Path.Combine(_directory, "key.bin")));

    [Fact]
    public void AStoreLeftHalfUpgradedIsRepairedOnOpen()
    {
        using (var database = Open())
        {
            database.RecordMarker("user.away", 1_000);
        }

        // What the first version of the upgrade could leave behind: its copy
        // table created, the version row never written.
        using (var connection = RawConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM schema_version WHERE version = 14;
                CREATE TABLE activity_markers_v14 (id INTEGER PRIMARY KEY, ts_ms INTEGER NOT NULL, kind TEXT NOT NULL, detail TEXT) STRICT;
                """;
            command.ExecuteNonQuery();
        }

        using var reopened = Open();
        reopened.RecordMarker("app.closed", 2_000, "P4G");
        var markers = reopened.GetMarkers(0, 3_000);
        Assert.Equal(["user.away", "app.closed"], markers.Select(marker => marker.Kind));
        Assert.Equal("p4g", markers[1].Detail);
    }

    [Fact]
    public async Task ProcessesOpeningAtOnceAllSucceed()
    {
        using (Open())
        {
        }

        // Back to before version 14, then six processes' worth of opens at once.
        using (var connection = RawConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM schema_version WHERE version = 14;";
            command.ExecuteNonQuery();
        }

        var opens = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            using var database = Open();
            return database.GetMarkers(0, 1).Count;
        }));
        var results = await Task.WhenAll(opens);

        Assert.All(results, count => Assert.Equal(0, count));
    }

    /// The store opened directly, with its key, to break it on purpose.
    private Microsoft.Data.Sqlite.SqliteConnection RawConnection()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var key = connection.CreateCommand();
        key.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(new DpapiKeyStore(Path.Combine(_directory, "key.bin")).GetOrCreateKey())}'\";";
        key.ExecuteNonQuery();
        return connection;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
