namespace Glint.Phase0.Core;

/// <summary>
/// Things on screen that mark progress inside an activity: a dialog that
/// exports or saves, a confirmation that something was sent or paid.
/// </summary>
public static class ActivityEvidence
{
    /// Phrases apps show once something is actually finished.
    private static readonly string[] Confirmations =
    [
        "message sent",
        "your message has been sent",
        "email sent",
        "mail sent",
        "payment successful",
        "payment complete",
        "payment completed",
        "payment received",
        "payment confirmed",
        "thank you for your payment",
        "transaction successful",
        "transfer complete",
        "transfer successful",
        "order filled",
        "order placed",
        "order confirmed",
        "booking confirmed",
        "submitted successfully",
        "successfully submitted",
        "your submission has been received"
    ];

    /// Quote marks before a phrase mean it is being talked about, not shown.
    private static readonly char[] Quotes = ['"', '“', '”', '\'', '‘', '’', '`', '«'];

    /// <summary>
    /// The confirmation in a text, if one is shown the way apps show them: a
    /// short line that leads with the phrase ("✓ Payment successful",
    /// "Message sent"). A sentence that merely mentions the phrase, or quotes
    /// it, is not a confirmation; a conversation about payments must never
    /// mark anything as paid.
    /// </summary>
    public static string? FindConfirmation(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var start = 0;
            while (start < line.Length && !char.IsLetter(line[start]))
            {
                if (Array.IndexOf(Quotes, line[start]) >= 0)
                {
                    start = line.Length;
                    break;
                }

                start++;
            }

            if (start >= line.Length)
            {
                continue;
            }

            var body = line[start..];
            foreach (var phrase in Confirmations)
            {
                if (body.StartsWith(phrase, StringComparison.OrdinalIgnoreCase)
                    && body.Length <= phrase.Length + 40)
                {
                    return phrase;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// What a dialog's title says the user is doing. Unknown dialogs (a
    /// colour picker, preferences) are not events.
    /// </summary>
    public static string? DialogEventOf(string? dialogTitle)
    {
        if (string.IsNullOrWhiteSpace(dialogTitle))
        {
            return null;
        }

        var lower = dialogTitle.ToLowerInvariant();
        if (lower.Contains("save as", StringComparison.Ordinal) || lower.Contains("save a copy", StringComparison.Ordinal))
        {
            return "saved-as";
        }

        if (lower.Contains("export", StringComparison.Ordinal))
        {
            return "exported";
        }

        if (lower.Contains("print", StringComparison.Ordinal))
        {
            return "printed";
        }

        if (lower.Contains("render", StringComparison.Ordinal))
        {
            return "rendered";
        }

        if (lower.Contains("publish", StringComparison.Ordinal) || lower.Contains("upload", StringComparison.Ordinal))
        {
            return "published";
        }

        return null;
    }
}
