import StatusBanner from "../components/StatusBanner";
import ScanCard from "../components/ScanCard";

/** Ports the WinUI Activity page: header, InfoBar, Capture card, history. */
function ActivityPage({
  overall,
  modelSummary,
  captureSummary,
  historySummary,
  history,
  scanning,
  busy,
  onStart,
  onPause,
  onBorderless,
  onImport,
  runtimeSetup,
  settingUp,
  setupLog,
  onSetupRuntime,
}) {
  return (
    <div className="glint-page">
      <div className="glint-page-inner">
        <h1 className="glint-title">Glint</h1>
        <h2 className="glint-subtitle">Local context capture</h2>
        <p className="glint-hint">
          Glint lives in the system tray. Press Ctrl+Alt+G anywhere to open
          the command bar.
        </p>
        <StatusBanner overall={overall} />

        <div className="glint-card">
          <h3>Capture</h3>
          <p>
            Start scanning, switch between windows, and Glint will capture
            changed context until paused.
          </p>
          <p className="dim">{modelSummary}</p>
          {runtimeSetup && (
            <div className="runtime-setup">
              <p className="dim">{runtimeSetup.reason}</p>
              <div className="glint-btn-row">
                <button
                  className="glint-btn primary"
                  onClick={onSetupRuntime}
                  disabled={settingUp || scanning}
                >
                  {settingUp
                    ? "Setting up local AI runtime..."
                    : "Set up local AI runtime"}
                </button>
              </div>
              {setupLog.length > 0 && (
                <div className="compat-lines setup-log">
                  {setupLog.join("\n")}
                </div>
              )}
            </div>
          )}
          <div className="glint-btn-row">
            <button className="glint-btn" onClick={onStart} disabled={scanning}>
              Start scanning
            </button>
            <button className="glint-btn" onClick={onPause} disabled={!scanning}>
              Pause scanning
            </button>
            <button className="glint-btn" onClick={onBorderless}>
              Enable borderless capture
            </button>
            <button
              className="glint-btn"
              onClick={onImport}
              title="Pick a .litertlm Gemma model already on disk."
            >
              Import Gemma model...
            </button>
          </div>
          {busy && <div className="spinner" aria-label="Working" />}
          <p>{captureSummary}</p>
        </div>

        <h2 className="glint-section-title">Scanned activity</h2>
        <p className="glint-section-sub">{historySummary}</p>
        <div className="scan-list">
          {history.map((scan) => (
            <ScanCard key={scan.id} scan={scan} />
          ))}
        </div>
      </div>
    </div>
  );
}

export default ActivityPage;
