namespace Glint.Phase0.Core;

public sealed class PrivacyGate
{
    private static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "1Password",
        "Bitwarden",
        "CredentialUIBroker",
        "Dashlane",
        "KeePass",
        "KeePassXC",
        "Ledger Live",
        "Proton Mail",
        "Signal",
        "SystemSettings",
        "Trezor Suite",
        "Wallet"
    };

    private static readonly string[] BlockedNameFragments =
    [
        "1password",
        "banking",
        "bitwarden",
        "credential",
        "crypto wallet",
        "dashlane",
        "keepass",
        "ledger live",
        "password",
        "seed phrase",
        "system settings",
        "trezor",
        "wallet"
    ];

    private static readonly string[] PrivateTitleFragments =
    [
        "incognito",
        "inprivate",
        "private browsing",
        "private window"
    ];

    private static readonly string[] SensitiveTitleFragments =
    [
        ".env",
        "id_dsa",
        "id_ed25519",
        "id_rsa",
        ".jks",
        ".key",
        ".keystore",
        ".p12",
        ".pem",
        ".pfx",
        "private key",
        "recovery phrase",
        "seed phrase"
    ];

    public PrivacyDecision Evaluate(
        ForegroundWindowInfo? window,
        AutomationSecurityProbe automation)
    {
        if (window is null)
        {
            return PrivacyDecision.Suppress(
                SuppressReason.NoForegroundWindow,
                "Windows did not report a foreground window");
        }

        if (window.IsSelf)
        {
            return PrivacyDecision.Suppress(SuppressReason.SelfCapture, "Glint never captures itself");
        }

        if (window.IsMinimized || window.Bounds.Width == 0 || window.Bounds.Height == 0)
        {
            return PrivacyDecision.Suppress(SuppressReason.Minimized, "foreground window is minimized or empty");
        }

        if (!window.DesktopStateDetermined)
        {
            return PrivacyDecision.Suppress(
                SuppressReason.DesktopStateUnknown,
                "input desktop could not be verified");
        }

        if (window.IsSecureDesktop)
        {
            return PrivacyDecision.Suppress(
                SuppressReason.SecureDesktop,
                "capture is disabled outside the default user desktop");
        }

        if (!window.ElevationDetermined)
        {
            return PrivacyDecision.Suppress(
                SuppressReason.ElevationStateUnknown,
                "target process elevation could not be verified");
        }

        if (window.IsElevated)
        {
            return PrivacyDecision.Suppress(
                SuppressReason.ElevatedProcess,
                "capture is disabled for elevated applications");
        }

        if (window.IsDisplayProtected)
        {
            return PrivacyDecision.Suppress(
                SuppressReason.DisplayProtected,
                "target window opted out of display capture");
        }

        if (BlockedProcesses.Contains(window.ProcessName)
            || BlockedNameFragments.Any(fragment =>
                window.ProcessName.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                || window.Title.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            return PrivacyDecision.Suppress(
                SuppressReason.BlocklistedApplication,
                "target application matches the shipped privacy blocklist");
        }

        if (PrivateTitleFragments.Any(fragment =>
                window.Title.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            return PrivacyDecision.Suppress(
                SuppressReason.PrivateBrowsing,
                "window title indicates private browsing");
        }

        if (SensitiveTitleFragments.Any(fragment =>
                window.Title.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            return PrivacyDecision.Suppress(
                SuppressReason.SensitiveWindowTitle,
                "window title indicates a sensitive file or secret");
        }

        if (!automation.Determined || !automation.BelongsToForegroundProcess)
        {
            return PrivacyDecision.Suppress(
                SuppressReason.AutomationStateUnknown,
                automation.Detail);
        }

        if (automation.IsPassword)
        {
            return PrivacyDecision.Suppress(SuppressReason.PasswordField, automation.Detail);
        }

        return PrivacyDecision.Allow();
    }
}
