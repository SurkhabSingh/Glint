using System.Text.Json;
using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

/// The window-switch files of earlier versions, moved into the focus log.
public sealed class LegacyTimelineImportTests
{
    private static JsonElement Row(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void SwitchesBecomeStretchesThatEndAtTheNextSwitchOrAStop()
    {
        var rows = new[]
        {
            Row("""{"ts_wall_ms":1000,"kind":"scan.started"}"""),
            Row("""{"ts_wall_ms":1000,"kind":"window.focused","process":"Code","title":"notes for bob@example.com"}"""),
            Row("""{"ts_wall_ms":31000,"kind":"heartbeat","process":"Code"}"""),
            Row("""{"ts_wall_ms":40000,"kind":"window.focused","process":"KeePass","title":null,"suppressed":"BlocklistedApplication"}"""),
            Row("""{"ts_wall_ms":50000,"kind":"scan.stopped"}""")
        };

        var stretches = LegacyTimelineImport.ToStretches(rows, new DeterministicRedactor());

        Assert.Equal([(1000L, (long?)40000L), (40000L, (long?)50000L)], stretches.Select(row => (row.StartedAtMilliseconds, row.EndedAtMilliseconds)));
        // Titles are redacted on the way in, like every title stored now.
        Assert.DoesNotContain("bob@example.com", stretches[0].Title, StringComparison.Ordinal);
        Assert.Null(stretches[1].Title);
        Assert.Equal("BlocklistedApplication", stretches[1].Suppressed);
    }

    [Fact]
    public void AStretchNobodySawEndIsNotStretchedOverTheGap()
    {
        var rows = new[]
        {
            Row("""{"ts_wall_ms":0,"kind":"window.focused","process":"Code","title":"a.cs"}"""),
            Row("""{"ts_wall_ms":30000,"kind":"heartbeat","process":"Code"}"""),
            Row("""{"ts_wall_ms":7200000,"kind":"window.focused","process":"chrome","title":"Inbox"}""")
        };

        var first = LegacyTimelineImport.ToStretches(rows, new DeterministicRedactor())[0];

        Assert.Equal(30000, first.EndedAtMilliseconds);
    }

    [Fact]
    public void StartsStopsAndAbsencesBecomeMarkersOnce()
    {
        var rows = new[]
        {
            Row("""{"ts_wall_ms":1000,"kind":"eon.started","eon_id":"eon-1000"}"""),
            Row("""{"ts_wall_ms":1001,"kind":"scan.started"}"""),
            Row("""{"ts_wall_ms":20000,"kind":"user.away"}"""),
            Row("""{"ts_wall_ms":50000,"kind":"scan.stopped"}"""),
            Row("""{"ts_wall_ms":50000,"kind":"eon.ended","ended_ms":50001}"""),
            Row("""{"ts_wall_ms":40000,"kind":"window.focused","process":"Code"}""")
        };

        var markers = LegacyTimelineImport.ToMarkers(rows);

        Assert.Equal(["run.started", "user.away", "run.stopped"], markers.Select(marker => marker.Kind));
    }
}
