namespace Glint.Phase0.Core;

/// <summary>
/// Temporary switches for finding what makes other apps stutter while Glint
/// records. The host sets <c>GLINT_TEST_MODE</c> for one run; it is never
/// saved. Each mode turns off one suspect so a run with it off can be
/// compared with a normal run:
/// <list type="bullet">
/// <item><c>no-accessibility</c>: no UI Automation calls into other apps;
/// page text comes from OCR only.</item>
/// <item><c>no-capture</c>: no screen capture; text comes from UI
/// Automation only.</item>
/// <item><c>titles-only</c>: neither; only window titles and apps.</item>
/// </list>
/// </summary>
/// <remarks>
/// With accessibility off, Glint cannot see that a password field has focus.
/// Password fields draw dots, so OCR cannot read them, and the redactor and
/// secret check still run on everything; but this is a test setting only.
/// </remarks>
public static class StutterTestMode
{
    public const string EnvironmentVariable = "GLINT_TEST_MODE";

    private static string Mode =>
        Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim().ToLowerInvariant() ?? string.Empty;

    public static bool AccessibilityOff => Mode is "no-accessibility" or "titles-only";

    public static bool CaptureOff => Mode is "no-capture" or "titles-only";

    public static string Name => Mode is "no-accessibility" or "no-capture" or "titles-only" ? Mode : "normal";
}

/// <summary>Stands in for UI Automation when the test mode turns it off: never calls into another app.</summary>
public sealed class InertAutomation : IUiAutomationService, IPageReader
{
    public AutomationSecurityProbe ProbeSecurity(ForegroundWindowInfo window) =>
        new(true, false, true, "UI Automation off (stutter test)");

    public AutomationTextResult ExtractText(ForegroundWindowInfo window) =>
        new(string.Empty, 0, false, TimeSpan.Zero);

    public PageProbe ProbePage(ForegroundWindowInfo window) => PageProbe.None;

    public AutomationTextResult ExtractPageText(ForegroundWindowInfo window, PageProbe probe) =>
        new(string.Empty, 0, false, TimeSpan.Zero);
}

/// <summary>Stands in for screen capture when the test mode turns it off.</summary>
public sealed class DisabledFrameCapture : IFrameCaptureService
{
    public Task<IScreenFrame> CaptureFrameAsync(ForegroundWindowInfo window, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Screen capture off (stutter test).");
}
