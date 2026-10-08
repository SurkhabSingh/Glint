import StatusBanner from "../components/StatusBanner";
import ScanCard, { PendingScanCard } from "../components/ScanCard";
import SessionCard from "../components/SessionCard";
import ActivityCard from "../components/ActivityCard";
import ActivityStrip from "../components/ActivityStrip";
import { durationText, groupBySession, outcomeOf, shortClock } from "../glint";

function sameDay(a, b) {
  const x = new Date(a);
  const y = new Date(b);
  return (
    x.getFullYear() === y.getFullYear() &&
    x.getMonth() === y.getMonth() &&
    x.getDate() === y.getDate()
  );
}

/** Ports the WinUI Activity page: header, InfoBar, Capture card, history. */
function ActivityPage({
  overall,
  modelSummary,
  captureSummary,
  historySummary,
  history,
  sessions,
  onSetSessionOutcome,
  activities,
  onSetActivityTask,
  onSetAppMode,
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
  const allActivities = activities ?? [];
  const hasActivities = allActivities.length > 0;
  const tasks = allActivities
    .filter((a) => a.taskStatus === "Open" || a.taskStatus === "LooksDone")
    .sort((a, b) =>
      a.taskStatus === b.taskStatus
        ? b.startedAtMilliseconds - a.startedAtMilliseconds
        : a.taskStatus === "LooksDone"
          ? -1
          : 1,
    );
  const newest = hasActivities
    ? Math.max(...allActivities.map((a) => a.startedAtMilliseconds))
    : 0;
  const latestDayStart = new Date(newest).setHours(0, 0, 0, 0);
  const latestDay = allActivities.filter(
    (a) => a.startedAtMilliseconds < latestDayStart + 86400000 && a.endedAtMilliseconds > latestDayStart,
  );
  const groups = groupBySession(allActivities).slice(0, 25);
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

        {hasActivities ? (
          <>
            {tasks.length > 0 && (
              <>
                <h2 className="glint-section-title">To do</h2>
                <p className="glint-section-sub">
                  Things you were asked to do or said you would, each linked to
                  where it came from. When Glint sees it finished it says
                  "Looks done" and waits for you to confirm.
                </p>
                <div className="scan-list">
                  {tasks.map((activity) => (
                    <ActivityCard
                      key={`task-${activity.id}`}
                      activity={activity}
                      onSetTask={onSetActivityTask}
                      compact
                    />
                  ))}
                </div>
              </>
            )}

            <h2 className="glint-section-title">
              {sameDay(newest, Date.now()) ? "Today" : "Latest day"}
            </h2>
            <p className="glint-section-sub">
              Each block is one thing you did, at its real times. Menus,
              loading screens and quick glances elsewhere stay inside the
              activity they interrupted.
            </p>
            <ActivityStrip activities={latestDay} />

            <h2 className="glint-section-title">Activities</h2>
            <p className="glint-section-sub">
              Grouped by stretch of work. Summaries are checked against what
              was on screen; anything that could not be found there is removed.
            </p>
            <div className="activity-groups">
              {groups.map((group) => (
                <section key={group.id} className="activity-group">
                  <div className="activity-group-head">
                    <span>
                      {shortClock(group.start)} – {shortClock(group.end)}
                    </span>
                    <span>
                      {group.activities.length} activit
                      {group.activities.length === 1 ? "y" : "ies"} ·{" "}
                      {durationText(group.active)}
                    </span>
                  </div>
                  <div className="scan-list">
                    {group.activities.map((activity) => (
                      <ActivityCard
                        key={activity.id}
                        activity={activity}
                        onSetTask={onSetActivityTask}
                        onSetMode={onSetAppMode}
                      />
                    ))}
                  </div>
                </section>
              ))}
            </div>
          </>
        ) : (
          <>
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
          </>
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
