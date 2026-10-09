using System.Diagnostics;
using System.Text;
using System.Windows.Automation;

namespace Glint.Phase0.Core;

public interface IUiAutomationService
{
    AutomationSecurityProbe ProbeSecurity(ForegroundWindowInfo window);

    AutomationTextResult ExtractText(ForegroundWindowInfo window);
}

/// <summary>
/// What the window's structure says before any text is read: whether it is
/// a browser, which site it shows, where the page itself sits, and what the
/// user is typing.
/// </summary>
public sealed class PageProbe
{
    public static readonly PageProbe None = new(false, null, null, null, null);

    public PageProbe(
        bool isBrowser,
        string? site,
        WindowBounds? documentBounds,
        string? focusedInput,
        object? document)
    {
        IsBrowser = isBrowser;
        Site = site;
        DocumentBounds = documentBounds;
        FocusedInput = focusedInput;
        Document = document;
    }

    public bool IsBrowser { get; }

    /// Host from the address bar, or null.
    public string? Site { get; }

    /// Screen rectangle of the visible page, for cropping OCR to it.
    public WindowBounds? DocumentBounds { get; }

    /// Text of the control the user is typing in, if any and not a password.
    public string? FocusedInput { get; }

    internal object? Document { get; }
}

public interface IPageReader
{
    PageProbe ProbePage(ForegroundWindowInfo window);

    /// <summary>
    /// The page's own text when the probe found one, without the tab strip,
    /// bookmarks or sidebar; otherwise the whole window as before.
    /// </summary>
    AutomationTextResult ExtractPageText(ForegroundWindowInfo window, PageProbe probe);
}

public sealed class UiAutomationService : IUiAutomationService, IPageReader
{
    private const int ProbeMaxNodes = 1_500;
    private const int ProbeMaxDepth = 14;
    private const int FocusedInputCharacters = 600;

    /// <summary>
    /// A text-pattern call slower than this blocked the app. Some apps
    /// (Chromium/Electron ones such as Discord) answer "the text of the
    /// visible page" on their UI thread: measured at 570-940 ms per call, a
    /// freeze the user feels as a stuck scroll or drag. Others (Firefox,
    /// Zen) answer in tens of milliseconds without blocking.
    /// </summary>
    internal const long SlowTextPatternMilliseconds = 150;

    /// <summary>
    /// Apps whose text-pattern calls turned out slow, by process name. They
    /// are read element by element from then on (names and values), which
    /// measured no freeze in the same apps. Learned by timing, so no app
    /// list is needed; kept for the life of the capture worker.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> SlowTextPatternApps =
        new(StringComparer.OrdinalIgnoreCase);

    /// Whether this app's text-pattern calls are known to block it.
    internal static bool TextPatternIsSlow(string processName) =>
        SlowTextPatternApps.ContainsKey(processName);

    private static void TimeTextPattern(string processName, Stopwatch timer)
    {
        if (timer.ElapsedMilliseconds > SlowTextPatternMilliseconds)
        {
            SlowTextPatternApps[processName] = timer.ElapsedMilliseconds;
        }
    }

    /// For tests.
    internal static void ForgetSlowTextPatternApps() => SlowTextPatternApps.Clear();
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromMilliseconds(700);

    /// Controls whose insides never hold the page or the address bar. Not
    /// descending into them keeps the probe cheap: a vertical tab sidebar can
    /// hold hundreds of nodes.
    private static readonly HashSet<ControlType> ProbeSkipTypes =
    [
        ControlType.Tab,
        ControlType.TabItem,
        ControlType.List,
        ControlType.ListItem,
        ControlType.Tree,
        ControlType.TreeItem,
        ControlType.MenuBar,
        ControlType.Menu,
        ControlType.MenuItem,
        ControlType.ScrollBar,
        ControlType.TitleBar,
        ControlType.StatusBar,
        ControlType.Hyperlink,
        ControlType.Button,
        ControlType.Image,
        ControlType.Text,
        ControlType.CheckBox,
        ControlType.RadioButton,
        ControlType.Slider
    ];
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
        "chromium",
        "comet",
        "dia",
        "firefox",
        "floorp",
        "librewolf",
        "msedge",
        "opera",
        "opera_gx",
        "thorium",
        "vivaldi",
        "waterfox",
        "zen"
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
        var slowTextPattern = TextPatternIsSlow(window.ProcessName);
        if (slowTextPattern)
        {
            profile = profile with { TextPatternCharacters = 0 };
        }
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

    public PageProbe ProbePage(ForegroundWindowInfo window) => ProbePage(window, null);

    /// <summary>
    /// The probe, optionally narrating what it saw: every edit box and page
    /// it met, and why it stopped. Only control types, names and parsed hosts
    /// are recorded, never field values.
    /// </summary>
    public PageProbe ProbePage(ForegroundWindowInfo window, List<string>? trace)
    {
        ArgumentNullException.ThrowIfNull(window);
        var timer = Stopwatch.StartNew();
        AutomationElement? bestDocument = null;
        WindowBounds? bestBounds = null;
        double bestArea = 0;
        string? site = null;
        try
        {
            var root = AutomationElement.FromHandle(window.Handle);
            if (root is null)
            {
                return PageProbe.None;
            }

            var queue = new Queue<(AutomationElement Element, int Depth)>();
            queue.Enqueue((root, 0));
            var nodes = 0;
            while (queue.Count > 0 && nodes < ProbeMaxNodes && timer.Elapsed < ProbeBudget)
            {
                if (trace is not null && queue.Peek().Depth > ProbeMaxDepth)
                {
                    break;
                }

                var (element, depth) = queue.Dequeue();
                nodes++;
                ControlType type;
                try
                {
                    type = element.Current.ControlType;
                }
                catch (ElementNotAvailableException)
                {
                    continue;
                }

                if (trace is not null && (type == ControlType.Document || type == ControlType.Edit || type == ControlType.ComboBox))
                {
                    var current = element.Current;
                    string? host = null;
                    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var tracePattern) && tracePattern is ValuePattern traceValue)
                    {
                        host = SiteParser.TryParseHost(traceValue.Current.Value) ?? "(value is not a web address)";
                    }

                    trace.Add($"depth {depth} {current.ControlType.ProgrammaticName} name='{Trim(current.Name)}' id='{Trim(current.AutomationId)}' offscreen={current.IsOffscreen} host={host ?? "(no value pattern)"}");
                }

                if (type == ControlType.Document)
                {
                    // The page itself. Never descended into: its contents are
                    // read later, and walking them here would cost the budget.
                    if (!element.Current.IsOffscreen)
                    {
                        var rect = element.Current.BoundingRectangle;
                        var area = rect.Width * rect.Height;
                        if (!rect.IsEmpty && area > bestArea)
                        {
                            bestArea = area;
                            bestDocument = element;
                            bestBounds = new WindowBounds(
                                (int)rect.Left,
                                (int)rect.Top,
                                (int)rect.Right,
                                (int)rect.Bottom);
                        }
                    }

                    continue;
                }

                // Address bars are Edit boxes in Chromium and ComboBoxes in
                // Firefox-based browsers (Zen, Firefox, LibreWolf).
                if (site is null
                    && (type == ControlType.Edit || type == ControlType.ComboBox)
                    && !ReadBool(element, AutomationElement.IsPasswordProperty)
                    && element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                    && pattern is ValuePattern value)
                {
                    site = SiteParser.TryParseHost(value.Current.Value);
                }

                if (depth >= ProbeMaxDepth || ProbeSkipTypes.Contains(type))
                {
                    continue;
                }

                for (var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                     child is not null;
                     child = TreeWalker.ControlViewWalker.GetNextSibling(child))
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }
        catch (Exception error) when (IsAutomationFailure(error))
        {
            trace?.Add($"failed: {error.GetType().Name}");
            return PageProbe.None;
        }

        trace?.Add($"finished after {timer.ElapsedMilliseconds} ms; site={site ?? "none"}; page found={bestDocument is not null}");

        // The visible page reports its own address, which survives full
        // screen (no toolbar) and can never be a background tab's.
        if (bestDocument is not null)
        {
            try
            {
                if (bestDocument.TryGetCurrentPattern(ValuePattern.Pattern, out var documentPattern)
                    && documentPattern is ValuePattern documentValue
                    && SiteParser.TryParseHost(documentValue.Current.Value) is { } documentHost)
                {
                    site = documentHost;
                }
            }
            catch (Exception error) when (IsAutomationFailure(error))
            {
                // The address bar's reading, if any, stands.
            }
        }

        trace?.Add($"site after reading the page itself: {site ?? "none"}");

        // A browser by name, or by shape: a page plus an address bar.
        var isBrowser = IsBrowserProcess(window) || (bestDocument is not null && site is not null);
        return new PageProbe(
            isBrowser,
            isBrowser ? site : null,
            isBrowser ? bestBounds : null,
            ReadFocusedInput(window),
            isBrowser ? bestDocument : null);
    }

    public AutomationTextResult ExtractPageText(ForegroundWindowInfo window, PageProbe probe)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(probe);
        if (probe.Document is not AutomationElement document)
        {
            return ExtractText(window);
        }

        var timer = Stopwatch.StartNew();
        var profile = TextExtractionProfile.For(window);
        var slowTextPattern = TextPatternIsSlow(window.ProcessName);
        if (slowTextPattern)
        {
            profile = profile with { TextPatternCharacters = 0 };
        }
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nodesVisited = 0;
        var characters = 0;
        var truncated = false;
        string? extractionError = null;
        try
        {
            // What is on screen first: the visible part of the page is what
            // the user actually saw, and it is far smaller than the whole
            // document.
            if (!slowTextPattern
                && document.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)
                && pattern is TextPattern text)
            {
                var call = Stopwatch.StartNew();
                foreach (var range in text.GetVisibleRanges())
                {
                    foreach (var line in range.GetText(profile.TextPatternCharacters)
                                 .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        var normalized = Normalize(line);
                        if (normalized.Length > 0 && seen.Add(normalized))
                        {
                            lines.Add(normalized);
                            characters += normalized.Length + Environment.NewLine.Length;
                        }
                    }
                }

                TimeTextPattern(window.ProcessName, call);
            }

            if (characters < 200)
            {
                foreach (var walker in profile.Walkers)
                {
                    if (!VisitTree(
                            document,
                            walker,
                            profile,
                            timer,
                            lines,
                            seen,
                            ref nodesVisited,
                            ref characters,
                            ref truncated)
                        || characters >= profile.BrowserUsefulCharacters)
                    {
                        break;
                    }
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

    /// <summary>
    /// The text of the control the user is typing in: a reply, a search, a
    /// quantity. Never a password, never the address bar.
    /// </summary>
    private static string? ReadFocusedInput(ForegroundWindowInfo window)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null
                || ReadInt(focused, AutomationElement.ProcessIdProperty) != window.ProcessId
                || ReadBool(focused, AutomationElement.IsPasswordProperty))
            {
                return null;
            }

            string? value = null;
            if (focused.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                && pattern is ValuePattern valuePattern
                && !valuePattern.Current.IsReadOnly)
            {
                value = valuePattern.Current.Value;
            }
            else if (focused.Current.ControlType == ControlType.Edit
                     && focused.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern)
                     && textPattern is TextPattern text)
            {
                value = text.DocumentRange.GetText(FocusedInputCharacters);
            }

            value = value is null ? null : Normalize(value);
            if (string.IsNullOrWhiteSpace(value) || SiteParser.TryParseHost(value) is not null)
            {
                return null;
            }

            return value.Length > FocusedInputCharacters ? value[..FocusedInputCharacters] : value;
        }
        catch (Exception error) when (IsAutomationFailure(error))
        {
            return null;
        }
    }

    private static string Trim(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= 40 ? value : value[..40] + "…";

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
        // Skipped (0) for apps where text-pattern calls block the app.
        if (textPatternCharacters > 0
            && element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern)
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
