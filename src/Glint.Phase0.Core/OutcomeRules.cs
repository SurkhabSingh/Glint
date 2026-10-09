namespace Glint.Phase0.Core;

/// <summary>
/// Reads what the model wrote for an activity's task.
/// </summary>
public static class OutcomeRules
{
    /// <summary>
    /// Whether a model field actually says something.
    /// </summary>
    /// <remarks>
    /// The parser already nulls a literal "NONE" or "N/A", but the model
    /// usually declines in prose instead — "No explicit commitments,
    /// requests, deadlines... were found" — which is not null and would read
    /// as a signal. Six of eleven real sessions carried exactly that.
    /// </remarks>
    public static bool IsMeaningful(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        string[] declines =
        [
            "none",
            "n/a",
            "no explicit",
            "no commitments",
            "no specific",
            "no reminder",
            "no outstanding",
            "nothing"
        ];

        return !declines.Any(decline =>
            trimmed.StartsWith(decline, StringComparison.OrdinalIgnoreCase));
    }
}
