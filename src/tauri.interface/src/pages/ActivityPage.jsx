import StatusBanner from "../components/StatusBanner";
import ScanCard, { PendingScanCard } from "../components/ScanCard";
import SessionCard from "../components/SessionCard";
import { outcomeOf } from "../glint";

/** Ports the WinUI Activity page: header, InfoBar, Capture card, history. */
function ActivityPage({
  overall,
  modelSummary,
  captureSummary,
  historySummary,
  history,
  sessions,
  onSetSessionOutcome,
  pending,
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
  const visible = (sessions ?? []).filter((session) => !session.isMinor);
  const unfinished = visible.filter((session) => outcomeOf(session) === "open");
  // Everything else, so nothing appears twice on the page.
  const rest = visible.filter((session) => outcomeOf(session) !== "open");
  const minorSessions = (sessions ?? []).filter((session) => session.isMinor);
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
              changed context until stopped.
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
              Stop scanning
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

        {unfinished.length > 0 && (
          <>
            <h2 className="glint-section-title">Unfinished</h2>
            <p className="glint-section-sub">
              Sessions that left something outstanding. When the same thing
              comes up again, only the latest mention is listed here.
            </p>
            <div className="scan-list">
              {unfinished.map((session) => (
                <SessionCard
                  key={session.id}
                  session={session}
                  onSetOutcome={onSetSessionOutcome}
                />
              ))}
            </div>
          </>
        )}

        <h2 className="glint-section-title">Sessions</h2>
        <p className="glint-section-sub">
          {(sessions ?? []).length > 0
            ? "Each stretch of work, summarized once it ends."
            : scanning
              ? "The first session appears once you have worked for a little while and paused."
              : "Start scanning to build your first session."}
        </p>
        <div className="scan-list">
          {rest.map((session) => (
            <SessionCard
              key={session.id}
              session={session}
              onSetOutcome={onSetSessionOutcome}
            />
          ))}
        </div>
        {minorSessions.length > 0 && (
          <details className="minor-sessions">
            <summary>
              {`Show ${minorSessions.length} minor session${
                minorSessions.length === 1 ? "" : "s"
              } — glances with almost nothing on screen`}
            </summary>
            <div className="scan-list">
              {minorSessions.map((session) => (
                <SessionCard key={session.id} session={session} />
              ))}
            </div>
          </details>
        )}

        <h2 className="glint-section-title">Captures</h2>
        <p className="glint-section-sub">{historySummary}</p>
        <div className="scan-list">
          {(pending ?? []).map((tick) => (
            <PendingScanCard key={tick.tickId} tick={tick} />
          ))}
          {history.map((scan) => (
            <ScanCard key={scan.id} scan={scan} />
          ))}
        </div>
      </div>
    </div>
  );
}

export default ActivityPage;
