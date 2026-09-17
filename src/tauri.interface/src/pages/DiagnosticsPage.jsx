import { useEffect, useState } from "react";
import { glintShortcutStatus } from "../glint";

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

/** Ports the WinUI Diagnostics page: privacy gate, storage, compat. */
function DiagnosticsPage({
  foregroundSummary,
  storageSummary,
  compatibilitySummary,
  onProbe,
  onVerifyStorage,
  onCompatibility,
}) {
  return (
    <div className="glint-page">
      <div className="glint-page-inner">
        <h1 className="glint-title">Diagnostics</h1>
        <p className="glint-hint">
          Privacy, encrypted storage, and machine compatibility checks.
        </p>

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
      </div>
    </div>
  );
}

export default DiagnosticsPage;
