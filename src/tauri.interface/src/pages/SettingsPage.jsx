import { useState } from "react";

/**
 * Dummy settings page (placeholder). Visual-only controls in the same
 * dashboard window and theme; wire to real behavior later.
 */
function SettingsPage() {
  const [launchAtLogin, setLaunchAtLogin] = useState(false);
  const [scanNotifications, setScanNotifications] = useState(true);

  function Toggle({ checked, onToggle, label }) {
    return (
      <button
        className={`setting-toggle ${checked ? "on" : ""}`}
        onClick={() => onToggle(!checked)}
        role="switch"
        aria-checked={checked}
        aria-label={label}
      >
        <span className="setting-knob" />
      </button>
    );
  }

  return (
    <div className="glint-page">
      <div className="glint-page-inner narrow">
        <h1 className="glint-title">Settings</h1>
        <p className="glint-hint">
          Placeholder settings. These controls are visual only for now.
        </p>

        <div className="glint-card">
          <h3>General</h3>
          <div className="setting-row">
            <div>
              <div className="setting-name">Launch at login</div>
              <div className="setting-desc">
                Start Glint in the system tray when Windows starts.
              </div>
            </div>
            <Toggle
              checked={launchAtLogin}
              onToggle={setLaunchAtLogin}
              label="Launch at login"
            />
          </div>
          <div className="setting-row">
            <div>
              <div className="setting-name">Scan notifications</div>
              <div className="setting-desc">
                Show a note when a new scan is summarized.
              </div>
            </div>
            <Toggle
              checked={scanNotifications}
              onToggle={setScanNotifications}
              label="Scan notifications"
            />
          </div>
        </div>

        <div className="glint-card">
          <h3>Appearance</h3>
          <p>
            Window background, opacity, and accent color live in the theme
            panel. Right-click the titlebar, then choose “Customize colors…”.
          </p>
        </div>
      </div>
    </div>
  );
}

export default SettingsPage;
