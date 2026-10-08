import { useState } from "react";
import { MODES, MODE_ORDER, activityView } from "../glint";

/**
 * One thing the user did: a game, a file being edited, an inbox, a video.
 * Says how Glint watched it, how far its summary was checked against the
 * screen, and what happened inside it: saves, exports, confirmations, short
 * glances elsewhere and longer pauses.
 */
function ActivityCard({ activity, onSetTask, onSetMode, compact = false, note = "" }) {
  const view = activityView(activity);
  const [changing, setChanging] = useState(false);
  const showTask = view.task && view.taskStatus !== "None";

  return (
    <div
      className={`activity-card mode-${view.mode}${
        view.taskStatus === "Open" ? " has-open-task" : ""
      }`}
    >
      <div className="activity-head">
        <span className="activity-span">{view.span}</span>
        <span className="activity-active">{view.active} active</span>
      </div>
      {note && <div className="activity-window-note">{note}</div>}

      <div className="activity-title-row">
        <span className={`activity-mode mode-${view.mode}`} title={MODES[view.mode]?.hint}>
          {view.modeLabel}
        </span>
        <span className="activity-label">{view.label}</span>
        {view.categoryLabel && (
          <span className="activity-category">{view.categoryLabel}</span>
        )}
      </div>

      {view.summary && !compact && <div className="activity-summary">{view.summary}</div>}

      {showTask && (
        <div className={`activity-task status-${view.taskStatus}`}>
          <span className="activity-task-text">
            {view.taskStatus === "LooksDone"
              ? "Looks done: "
              : view.taskStatus === "Done"
                ? "Done: "
                : "To do: "}
            {view.task}
          </span>
          {onSetTask && (
            <span className="activity-task-actions">
              {view.taskStatus === "LooksDone" && (
                <button className="session-action" onClick={() => onSetTask(activity.id, "Done")}>
                  Confirm done
                </button>
              )}
              {view.taskStatus === "Open" && (
                <button className="session-action" onClick={() => onSetTask(activity.id, "Done")}>
                  Mark done
                </button>
              )}
              {view.taskStatus !== "Open" && (
                <button className="session-action" onClick={() => onSetTask(activity.id, "Open")}>
                  Still open
                </button>
              )}
              {view.taskStatus !== "None" && (
                <button className="session-action" onClick={() => onSetTask(activity.id, "None")}>
                  Not a task
                </button>
              )}
            </span>
          )}
        </div>
      )}

      {!compact && (view.events.length > 0 || view.glances.length > 0 || view.phases.length > 1) && (
        <ul className="activity-facts">
          {view.events.map((event, index) => (
            <li key={`e${index}`} className={`fact-${event.kind}`}>
              <span className="fact-time">{event.at}</span>
              {event.text}
            </li>
          ))}
          {view.glances.length > 0 && (
            <li className="fact-glance">
              <span className="fact-time">glanced</span>
              {view.glances.slice(0, 4).join(", ")}
              {view.glances.length > 4 ? ` and ${view.glances.length - 4} more` : ""}
            </li>
          )}
          {view.phases.length > 1 && (
            <li className="fact-phases">
              <span className="fact-time">inside</span>
              {view.phases.slice(0, 6).join(" · ")}
            </li>
          )}
        </ul>
      )}

      <div className="activity-foot">
        <span className="activity-source">{view.source}</span>
        <span className={`activity-check check-${view.checkTone}`}>{view.checkText}</span>
        {onSetMode &&
          (changing ? (
            <label className="activity-mode-picker">
              <span>Treat {view.profileName} as</span>
              <select
                autoFocus
                defaultValue={view.mode}
                onBlur={() => setChanging(false)}
                onChange={(event) => {
                  setChanging(false);
                  if (event.target.value !== view.mode) {
                    onSetMode(view.profileKey, event.target.value);
                  }
                }}
              >
                {MODE_ORDER.map((mode) => (
                  <option key={mode} value={mode}>
                    {MODES[mode].label}: {MODES[mode].hint}
                  </option>
                ))}
              </select>
            </label>
          ) : (
            <button className="activity-change-mode" onClick={() => setChanging(true)}>
              Wrong kind?
            </button>
          ))}
      </div>
    </div>
  );
}

export default ActivityCard;
