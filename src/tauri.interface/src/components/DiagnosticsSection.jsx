import { useEffect, useState } from "react";
import { glintSetTestMode, glintShortcutStatus, glintTestMode } from "../glint";

const TEST_MODES = [
  { id: "normal", label: "Normal", hint: "Everything on, as Glint runs today." },
  { id: "no-accessibility", label: "No accessibility", hint: "Never asks other apps for their contents. Text from OCR only." },
  { id: "no-capture", label: "No screen capture", hint: "Never grabs the screen. Text from accessibility only." },
  { id: "titles-only", label: "Titles only", hint: "Neither: window titles and apps only." },
];

/**
 * Finds what makes other apps stutter: turn one suspect off, record, scroll
 * and drag as usual, compare with a normal run. Not saved; Glint starts in
 * Normal every time.
 */
function StutterTestCard() {
  const [status, setStatus] = useState(null);

  useEffect(() => {
    let cancelled = false;
    const load = () =>
      glintTestMode()
        .then((value) => !cancelled && setStatus(value))
        .catch(() => {});
    load();
    const timer = setInterval(load, 3000);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, []);

  async function choose(mode) {
    try {
      setStatus(await glintSetTestMode(mode));
    } catch {
      // Unchanged; the next refresh shows the truth.
    }
  }

  const mode = status?.mode ?? "normal";
  const active = TEST_MODES.find((item) => item.id === mode);
  return (
    <div className="glint-card">
      <h3>Stutter test</h3>
      <p className="dim">
        For each mode: restart Zen, Steam and Claude (an app that was asked for its contents stays slow until restarted),
        pick the mode, start recording, then scroll and drag windows for a couple of minutes the same way each time.
        This setting isn't saved.
      </p>
      <div className="glint-btn-row">
        <div className="dash-seg" role="radiogroup" aria-label="Stutter test mode">
          {TEST_MODES.map((item) => (
            <button
              key={item.id}
              role="radio"
              aria-checked={mode === item.id}
              className={mode === item.id ? "active" : ""}
              onClick={() => choose(item.id)}
            >
              {item.label}
            </button>
          ))}
        </div>
      </div>
      <p>{active?.hint}</p>
      <p className="dim">
        {status && status.looks > 0
          ? `${status.looks} looks in this mode · average ${status.averageMs} ms · slowest ${status.slowestMs} ms`
          : "No looks yet in this mode. Start recording."}
      </p>
      {mode === "no-accessibility" || mode === "titles-only" ? (
        <p className="dim">
          While accessibility is off Glint can't tell when a password field has focus. Passwords show as dots, so OCR can't read
          them, but switch back to Normal after the test.
        </p>
      ) : null}
    </div>
  );
}

function ShortcutCard() {
  const [shortcuts, setShortcuts] = useState(null);
  useEffect(() => {
    let cancelled = false;
    glintShortcutStatus()
      .then((items) => {
        if (!cancelled) setShortcuts(items ?? []);
      })
      .catch(() => {
        if (!cancelled) setShortcuts([]);
      });
    return () => {
      cancelled = true;
    };
  }, []);
  return (
    <div className="glint-card">
      <h3>Keyboard shortcuts</h3>
      {shortcuts == null ? (
        <p className="dim">Checking shortcut registration…</p>
      ) : (
        <div className="scan-list">
          {shortcuts.map((item) => (
            <div className="scan-card" key={item.shortcut}>
              <div className="scan-label" style={{ fontSize: 15 }}>
                {item.shortcut}
                {!item.registered && (
                  <span className="tl-badge">unavailable</span>
                )}
              </div>
              <div className="scan-source">
                {item.action}
                {!item.registered &&
                  " — another app owns this combo; use the tray menu or command bar instead."}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

/**
 * Diagnostics, shown at the bottom of Settings: privacy gate, encrypted
 * storage, machine compatibility and keyboard shortcuts.
 */
function DiagnosticsSection({
  foregroundSummary,
  storageSummary,
  compatibilitySummary,
  onProbe,
  onVerifyStorage,
  onCompatibility,
}) {
  return (
    <section className="settings-diagnostics" aria-labelledby="diagnostics-title">
      <h2 className="settings-section-title" id="diagnostics-title">Diagnostics</h2>
      <p className="glint-hint">
        Privacy, encrypted storage, and machine compatibility checks.
      </p>

      <StutterTestCard />

      <div className="diag-grid">
        <div className="glint-card">
          <h3>Foreground privacy gate</h3>
          <p>{foregroundSummary}</p>
          <div className="glint-btn-row">
            <button className="glint-btn" onClick={onProbe}>
              Run privacy probe
            </button>
          </div>
        </div>
        <div className="glint-card">
          <h3>Encrypted local storage</h3>
          <p>{storageSummary}</p>
          <div className="glint-btn-row">
            <button className="glint-btn" onClick={onVerifyStorage}>
              Verify storage
            </button>
          </div>
        </div>
      </div>

      <div className="glint-card">
        <h3>Machine compatibility</h3>
        <div className="compat-lines">{compatibilitySummary}</div>
        <div className="glint-btn-row">
          <button className="glint-btn" onClick={onCompatibility}>
            Run compatibility check
          </button>
        </div>
      </div>

      <ShortcutCard />

      <p className="glint-footnote">
        Captured pixels stay in memory. Only redacted text and derived
        summaries are stored in the encrypted local database.
      </p>
    </section>
  );
}

export default DiagnosticsSection;
