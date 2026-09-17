using System.Text.Json;

namespace Glint.Phase0.Core;

/// How a session's capture ids are stored in `activity_sessions`.
///
/// This is what remains of the old per-scan SessionManager. Grouping now
/// happens in a batch (see <see cref="Sessionizer"/>) rather than inside the
/// capture tick, and membership is also recorded on each capture's
/// `session_id`, which is what queries use; this column stays as the
/// session's own ordered list.
internal static class SessionScanIds
{
    private static readonly JsonSerializerOptions Options = new();

    public static string Serialize(IReadOnlyList<string> scanIds) =>
        JsonSerializer.Serialize(scanIds, Options);

    public static IReadOnlyList<string> Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
