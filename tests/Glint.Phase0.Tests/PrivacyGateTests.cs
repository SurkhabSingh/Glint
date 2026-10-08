using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class PrivacyGateTests
{
    private readonly PrivacyGate _gate = new(selfElevated: false);

    [Fact]
    public void AllowsOrdinaryUnelevatedWindowWithKnownAutomationState()
    {
        var decision = _gate.Evaluate(
            Window(),
            new(true, false, true, "known"));

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void SuppressesWhenPasswordFieldIsFocused()
    {
        var decision = _gate.Evaluate(
            Window(),
            new(true, true, true, "password"));

        Assert.Equal(SuppressReason.PasswordField, decision.Reason);
    }

    [Theory]
    [InlineData("Private Browsing - Firefox", SuppressReason.PrivateBrowsing)]
    [InlineData("C:\\work\\.env.local - Visual Studio Code", SuppressReason.SensitiveWindowTitle)]
    [InlineData("Recovery Phrase - Wallet", SuppressReason.BlocklistedApplication)]
    public void SuppressesSensitiveTitles(string title, SuppressReason reason)
    {
        var decision = _gate.Evaluate(
            Window(title: title),
            new(true, false, true, "known"));

        Assert.Equal(reason, decision.Reason);
    }

    [Fact]
    public void SuppressesUnknownElevationState()
    {
        var decision = _gate.Evaluate(
            Window(elevationDetermined: false),
            new(true, false, true, "known"));

        Assert.Equal(SuppressReason.ElevationStateUnknown, decision.Reason);
    }

    [Theory]
    [MemberData(nameof(UnsafeWindows))]
    public void SuppressesUnsafeWindowState(
        ForegroundWindowInfo window,
        SuppressReason expected)
    {
        var decision = _gate.Evaluate(
            window,
            new(true, false, true, "known"));

        Assert.Equal(expected, decision.Reason);
    }

    [Fact]
    public void SuppressesUnknownAutomationState()
    {
        var decision = _gate.Evaluate(
            Window(),
            new(false, false, false, "unknown"));

        Assert.Equal(SuppressReason.AutomationStateUnknown, decision.Reason);
    }

    [Fact]
    public void SuppressesDesktopBackgroundWithoutCapture()
    {
        var decision = _gate.Evaluate(
            Window("Program Manager") with { ProcessName = "explorer" },
            new(true, false, true, "known"));

        Assert.Equal(SuppressReason.DesktopBackground, decision.Reason);
    }

    [Fact]
    public void AnAdminAppIsReadWhenGlintRunsAsAdmin()
    {
        var decision = new PrivacyGate(selfElevated: true).Evaluate(
            Window() with { IsElevated = true },
            new(true, false, true, "known"));

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void AnAdminAppStillObeysTheBlocklist()
    {
        var decision = _gate.Evaluate(
            Window("Database.kdbx - KeePass") with { IsElevated = true, ProcessName = "KeePass" },
            new(true, false, true, "known"));

        Assert.Equal(SuppressReason.BlocklistedApplication, decision.Reason);
    }

    [Fact]
    public void AnAdminAppIsNameAndTitleOnlyEvenWhenAutomationCannotReachIt()
    {
        // Windows blocks UI Automation into admin apps; that is expected, not unknown.
        var decision = _gate.Evaluate(
            Window() with { IsElevated = true },
            new(false, false, false, "access denied"));

        Assert.Equal(SuppressReason.ElevatedProcess, decision.Reason);
    }

    public static TheoryData<ForegroundWindowInfo, SuppressReason> UnsafeWindows =>
        new()
        {
            { Window() with { IsSelf = true }, SuppressReason.SelfCapture },
            { Window() with { IsMinimized = true }, SuppressReason.Minimized },
            { Window() with { IsSecureDesktop = true }, SuppressReason.SecureDesktop },
            {
                Window() with { DesktopStateDetermined = false },
                SuppressReason.DesktopStateUnknown
            },
            { Window() with { IsElevated = true }, SuppressReason.ElevatedProcess },
            { Window() with { IsDisplayProtected = true }, SuppressReason.DisplayProtected }
        };

    private static ForegroundWindowInfo Window(
        string title = "README.md - Visual Studio Code",
        bool elevationDetermined = true) =>
        new(
            123,
            42,
            "Code",
            @"C:\Program Files\Microsoft VS Code\Code.exe",
            title,
            new(0, 0, 1200, 800),
            false,
            false,
            elevationDetermined,
            false,
            false,
            true,
            false);
}
