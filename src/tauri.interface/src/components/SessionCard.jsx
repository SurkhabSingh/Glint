import { sessionView } from "../glint";

/**
 * One stretch of work: what it was, how long it took, and how many captures
 * it covers. This is the unit the model summarizes now — a capture is
 * evidence, a session is the thing worth reading.
 *
 * A session also carries whether it left something outstanding. That verdict
 * is only ever a proposal until the user taps one of the buttons, and an
 * unknown one is shown as unknown rather than quietly counted as done.
 */
function SessionCard({ session, onSetOutcome }) {
  const view = sessionView(session);
  return (
    <div
      className={`session-card${view.summarized ? "" : " unsummarized"}${
        view.minor ? " minor" : ""
      }${view.outcome === "open" ? " open-loop" : ""}`}
    >
      <div className="session-head">
        <span className="session-span">{view.span}</span>
        <span className="session-meta">{view.meta}</span>
      </div>
      <div className="session-label">
        {view.label}
        {view.outcome !== "unknown" && (
          <span className={`session-state ${view.outcome}`}>
            {view.outcomeLabel}
          </span>
        )}
      </div>
      <div className="session-summary">{view.summary}</div>
      {view.supersededNote && (
        <div className="session-note">{view.supersededNote}</div>
      )}
      {view.stale && (
        <div className="session-stale">Untouched for over a week</div>
      )}
      {view.important && <div className="scan-important">{view.important}</div>}
      {view.reminder && <div className="scan-reminder">{view.reminder}</div>}
      <div className="session-foot">
        <span className="session-source">{view.source}</span>
        {onSetOutcome && !view.minor && (
          <span className="session-actions">
            {view.outcomeNote && (
              <span className="session-note">{view.outcomeNote}</span>
            )}
            {view.outcome !== "settled" && (
              <button
                className="session-action"
                onClick={() => onSetOutcome(session.id, "settled")}
              >
                Mark done
              </button>
            )}
            {view.outcome !== "open" && (
              <button
                className="session-action"
                onClick={() => onSetOutcome(session.id, "open")}
              >
                Still open
              </button>
            )}
          </span>
        )}
      </div>
    </div>
  );
}

export default SessionCard;
