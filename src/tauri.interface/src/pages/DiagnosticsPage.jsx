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

        <p className="glint-footnote">
          Captured pixels stay in memory. Only redacted text and derived
          summaries are stored in the encrypted local database.
        </p>
      </div>
    </div>
  );
}

export default DiagnosticsPage;
