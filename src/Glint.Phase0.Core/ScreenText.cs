namespace Glint.Phase0.Core;

/// Text read from a window by more than one route, put together once.
public static class ScreenText
{
    /// <summary>
    /// Accessibility text first, then whatever OCR found that it did not, one
    /// copy of each line with its spacing evened out.
    /// </summary>
    public static string Combine(string automationText, string ocrText)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { automationText, ocrText })
        {
            foreach (var line in source.Split(
                         ['\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = string.Join(
                    ' ',
                    line.Split(
                        (char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                if (normalized.Length >= 2 && seen.Add(normalized))
                {
                    lines.Add(normalized);
                }
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
