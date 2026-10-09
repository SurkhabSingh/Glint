using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Glint.Phase0.Core;

/// <summary>
/// Earlier versions logged window switches to sealed day files under
/// <c>timeline\</c>, beside the store. The focus log replaces them; this moves
/// what they hold into it, once, then sets the folder aside.
/// </summary>
public static class LegacyTimelineImport
{
    private const string MetaKey = "legacy-timeline-imported";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Glint.Timeline.v1");

    /// <summary>
    /// A stretch whose next row comes this much later than it was last seen
    /// ended unseen (the app was closed, the PC slept): it ends where it was
    /// last seen rather than claiming the gap.
    /// </summary>
    private const long LongestUnseenMilliseconds = 1_800_000;

    public static int Run(Phase0Database database, string dataRoot, DeterministicRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(database);
        var folder = Path.Combine(dataRoot, "timeline");
        if (database.GetMeta(MetaKey) is not null || !Directory.Exists(folder))
        {
            return 0;
        }

        var rows = new List<JsonElement>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.jsonl"))
        {
            foreach (var line in File.ReadLines(file))
            {
                if (Open(line) is { } row)
                {
                    rows.Add(row);
                }
            }
        }

        var stretches = ToStretches(rows, redactor);
        database.ImportFocus(stretches);

        // The files also hold every start, stop, absence and lock, some of
        // which never reached the store. Sittings are told apart by these.
        foreach (var marker in ToMarkers(rows))
        {
            var near = database.GetMarkers(marker.TimestampMilliseconds - 2_000, marker.TimestampMilliseconds + 2_000);
            if (!near.Any(existing => existing.Kind == marker.Kind))
            {
                database.RecordMarker(marker.Kind, marker.TimestampMilliseconds, marker.Detail);
            }
        }
        database.SetMeta(MetaKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            Directory.Move(folder, folder + ".imported");
        }
        catch (IOException)
        {
            // Left where it is; the meta row keeps it from being read twice.
        }

        return stretches.Count;
    }

    internal static IReadOnlyList<FocusRow> ToStretches(IEnumerable<JsonElement> rows, DeterministicRedactor redactor)
    {
        static string? Text(JsonElement row, string name) =>
            row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        var stretches = new List<FocusRow>();
        FocusRow? open = null;

        void Close(long at)
        {
            if (open is null)
            {
                return;
            }

            var end = at - open.LastSeenMilliseconds > LongestUnseenMilliseconds ? open.LastSeenMilliseconds : at;
            stretches.Add(open with { EndedAtMilliseconds = Math.Max(open.StartedAtMilliseconds, end), LastSeenMilliseconds = Math.Max(open.LastSeenMilliseconds, end) });
            open = null;
        }

        foreach (var row in rows
                     .Where(row => row.TryGetProperty("ts_wall_ms", out var at) && at.ValueKind == JsonValueKind.Number)
                     .OrderBy(row => row.GetProperty("ts_wall_ms").GetInt64()))
        {
            var at = row.GetProperty("ts_wall_ms").GetInt64();
            switch (Text(row, "kind"))
            {
                case "window.focused":
                    Close(at);
                    var title = Text(row, "title");
                    open = new FocusRow(
                        0,
                        at,
                        null,
                        at,
                        Text(row, "process") ?? "unknown",
                        title is null ? null : redactor.Redact(title).Text,
                        Text(row, "suppressed"),
                        Text(row, "suppressedDetail"));
                    break;
                case "heartbeat":
                    if (open is not null && open.ProcessName == Text(row, "process"))
                    {
                        open = open with { LastSeenMilliseconds = Math.Max(open.LastSeenMilliseconds, at) };
                    }

                    break;
                case "scan.stopped" or "scan.paused" or "user.away" or "user.locked" or "system.sleep" or "system.shutdown":
                    Close(at);
                    break;
            }
        }

        if (open is not null)
        {
            Close(open.LastSeenMilliseconds);
        }

        return stretches;
    }

    /// <summary>
    /// The recording's own starts and stops, and the user's absences, as the
    /// markers they would have been. Earlier versions wrote these to the files
    /// first; a marker in the store was best effort.
    /// </summary>
    internal static IReadOnlyList<ActivityMarker> ToMarkers(IEnumerable<JsonElement> rows)
    {
        var markers = new List<ActivityMarker>();
        foreach (var row in rows)
        {
            if (!row.TryGetProperty("ts_wall_ms", out var at) || at.ValueKind != JsonValueKind.Number
                || !row.TryGetProperty("kind", out var kindValue) || kindValue.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var timestamp = row.TryGetProperty("ended_ms", out var ended) && ended.ValueKind == JsonValueKind.Number
                ? ended.GetInt64()
                : at.GetInt64();
            var kind = kindValue.GetString() switch
            {
                "scan.started" or "eon.started" => "run.started",
                "scan.stopped" or "scan.paused" or "eon.ended" => "run.stopped",
                var other when other is not null && ActivityMarker.Kinds.Contains(other) && other != "app.closed" => other,
                _ => null
            };
            if (kind is not null)
            {
                markers.Add(new ActivityMarker(timestamp, kind));
            }
        }

        // One of each per moment: a start was written as both a run and a row.
        return markers
            .OrderBy(marker => marker.TimestampMilliseconds)
            .Aggregate(new List<ActivityMarker>(), (kept, marker) =>
            {
                if (!kept.Any(earlier => earlier.Kind == marker.Kind
                        && marker.TimestampMilliseconds - earlier.TimestampMilliseconds <= 2_000))
                {
                    kept.Add(marker);
                }

                return kept;
            });
    }

    private static JsonElement? Open(string line)
    {
        try
        {
            using var envelope = JsonDocument.Parse(line);
            if (!envelope.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var plain = ProtectedData.Unprotect(Convert.FromBase64String(data.GetString()!), Entropy, DataProtectionScope.CurrentUser);
            using var row = JsonDocument.Parse(plain);
            return row.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or FormatException or CryptographicException)
        {
            return null;
        }
    }
}
