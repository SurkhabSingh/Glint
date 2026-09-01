using System.Diagnostics;
using System.Text;
using System.Windows.Automation;

namespace Glint.Phase0.Core;

public interface IUiAutomationService
{
    AutomationSecurityProbe ProbeSecurity(ForegroundWindowInfo window);

    AutomationTextResult ExtractText(ForegroundWindowInfo window);
}

public sealed class UiAutomationService : IUiAutomationService
{
    private const int MaxSecurityAncestors = 16;
    private const int DefaultMaxTextNodes = 512;
    private const int BrowserMaxTextNodes = 5_000;
    private const int DefaultMaxTextCharacters = 48_000;
    private const int BrowserMaxTextCharacters = 96_000;
    private const int DefaultTextPatternCharacters = 8_192;
    private const int BrowserTextPatternCharacters = 64_000;
    private static readonly TimeSpan DefaultTextBudget = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan BrowserTextBudget = TimeSpan.FromMilliseconds(1_500);

    private static readonly string[] BrowserProcessNames =
    [
        "arc",
        "brave",
        "chrome",
        "dia",
        "firefox",
        "librewolf",
        "msedge",
        "opera",
        "opera_gx",
        "vivaldi",
        "waterfox"
    ];

    public AutomationSecurityProbe ProbeSecurity(ForegroundWindowInfo window)
    {
        try
        {
            var root = AutomationElement.FromHandle(window.Handle);
            var focused = AutomationElement.FocusedElement;
            if (root is null || focused is null)
            {
                return new(false, false, false, "UI Automation returned no root or focused element");
            }

            var focusedProcessId = ReadInt(focused, AutomationElement.ProcessIdProperty);
            if (focusedProcessId != window.ProcessId)
            {
                return new(
                    false,
                    false,
                    false,
                    $"focused UIA element belongs to process {focusedProcessId}, expected {window.ProcessId}");
            }

            var current = focused;
            for (var depth = 0; depth < MaxSecurityAncestors && current is not null; depth++)
            {
                if (ReadBool(current, AutomationElement.IsPasswordProperty))
                {
                    return new(true, true, true, $"password control detected at ancestor depth {depth}");
                }

                current = TreeWalker.ControlViewWalker.GetParent(current);
            }

            return new(true, false, true, "UI Automation security state determined");
        }
        catch (Exception error) when (IsAutomationFailure(error))
        {
            return new(false, false, false, $"UI Automation security probe failed: {error.Message}");
        }
    }

    public AutomationTextResult ExtractText(ForegroundWindowInfo window)
    {
        var timer = Stopwatch.StartNew();
        var profile = TextExtractionProfile.For(window);
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nodesVisited = 0;
        var characters = 0;
        var truncated = false;
        string? extractionError = null;

        try
        {
            var root = AutomationElement.FromHandle(window.Handle);
            if (root is null)
            {
                return new(string.Empty, 0, false, timer.Elapsed);
            }

            foreach (var walker in profile.Walkers)
            {
                if (!VisitTree(
                        root,
                        walker,
                        profile,
                        timer,
                        lines,
                        seen,
                        ref nodesVisited,
                        ref characters,
                        ref truncated))
                {
                    break;
                }

                if (!profile.IsBrowser || characters >= profile.BrowserUsefulCharacters)
                {
                    break;
                }
            }
        }
        catch (Exception error) when (IsAutomationFailure(error))
        {
            extractionError = $"{error.GetType().Name}: {error.Message}";
        }

        return new(
            string.Join(Environment.NewLine, lines),
            nodesVisited,
            truncated,
            timer.Elapsed,
            extractionError);
    }

    private static bool VisitTree(
        AutomationElement root,
        TreeWalker walker,
        TextExtractionProfile profile,
        Stopwatch timer,
        List<string> lines,
        HashSet<string> seen,
        ref int nodesVisited,
        ref int characters,
        ref bool truncated)
    {
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            if (nodesVisited >= profile.MaxNodes
                || characters >= profile.MaxCharacters
                || timer.Elapsed > profile.Budget)
            {
                truncated = true;
                return false;
            }

            var element = queue.Dequeue();
            nodesVisited++;
            if (!ReadBool(element, AutomationElement.IsPasswordProperty))
            {
                foreach (var text in ReadTextCandidates(element, profile.TextPatternCharacters))
                {
                    var normalized = Normalize(text);
                    if (normalized.Length == 0 || !seen.Add(normalized))
                    {
                        continue;
                    }

                    var remaining = profile.MaxCharacters - characters;
                    if (normalized.Length > remaining)
                    {
                        normalized = normalized[..remaining];
                        truncated = true;
                    }

                    lines.Add(normalized);
                    characters += normalized.Length + Environment.NewLine.Length;
                    if (characters >= profile.MaxCharacters)
                    {
                        truncated = true;
                        return false;
                    }
                }
            }

            for (var child = walker.GetFirstChild(element);
                 child is not null;
                 child = walker.GetNextSibling(child))
            {
                queue.Enqueue(child);
            }
        }

        return true;
    }

    private static IEnumerable<string> ReadTextCandidates(
        AutomationElement element,
        int textPatternCharacters)
    {
        // Browser/web views usually expose the page body through TextPattern on a
        // document element. Read that before Name/Value so page content outranks
        // toolbar chrome and address-bar fragments.
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern)
            && textPattern is TextPattern text)
        {
            var document = text.DocumentRange.GetText(textPatternCharacters);
            if (!string.IsNullOrWhiteSpace(document))
            {
                yield return document;
            }
        }

        var name = ReadString(element, AutomationElement.NameProperty);
        if (!string.IsNullOrWhiteSpace(name))
        {
            yield return name;
        }

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern)
            && valuePattern is ValuePattern value
            && !string.IsNullOrWhiteSpace(value.Current.Value))
        {
            yield return value.Current.Value;
        }
    }

    private static int ReadInt(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCurrentPropertyValue(property, true);
        return value is int result ? result : -1;
    }

    private static bool ReadBool(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCurrentPropertyValue(property, true);
        return value is bool result && result;
    }

    private static string? ReadString(AutomationElement element, AutomationProperty property)
    {
        var value = element.GetCurrentPropertyValue(property, true);
        return value as string;
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWhitespace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWhitespace)
                {
                    builder.Append(' ');
                    previousWhitespace = true;
                }
            }
            else if (!char.IsControl(character))
            {
                builder.Append(character);
                previousWhitespace = false;
            }
        }

        return builder.ToString().Trim();
    }

    private static bool IsAutomationFailure(Exception error) =>
        error is ElementNotAvailableException
            or InvalidOperationException
            or System.Runtime.InteropServices.COMException;

    internal static bool IsBrowserProcess(ForegroundWindowInfo window)
    {
        var process = Path.GetFileNameWithoutExtension(window.ProcessName);
        var executable = string.IsNullOrWhiteSpace(window.ExecutablePath)
            ? null
            : Path.GetFileNameWithoutExtension(window.ExecutablePath);
        return BrowserProcessNames.Any(name =>
            string.Equals(process, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(executable, name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record TextExtractionProfile(
        bool IsBrowser,
        int MaxNodes,
        int MaxCharacters,
        int TextPatternCharacters,
        int BrowserUsefulCharacters,
        TimeSpan Budget,
        IReadOnlyList<TreeWalker> Walkers)
    {
        public static TextExtractionProfile For(ForegroundWindowInfo window)
        {
            var isBrowser = IsBrowserProcess(window);
            return isBrowser
                ? new(
                    true,
                    BrowserMaxTextNodes,
                    BrowserMaxTextCharacters,
                    BrowserTextPatternCharacters,
                    1_000,
                    BrowserTextBudget,
                    [
                        TreeWalker.ContentViewWalker,
                        TreeWalker.ControlViewWalker,
                        TreeWalker.RawViewWalker
                    ])
                : new(
                    false,
                    DefaultMaxTextNodes,
                    DefaultMaxTextCharacters,
                    DefaultTextPatternCharacters,
                    0,
                    DefaultTextBudget,
                    [TreeWalker.ContentViewWalker]);
        }
    }
}
