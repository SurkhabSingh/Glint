import { sessionView } from "../glint";

/**
 * One stretch of work: what it was, how long it took, and how many captures
 * it covers. This is the unit the model summarizes now — a capture is
 * evidence, a session is the thing worth reading.
 */
function SessionCard({ session }) {
  const view = sessionView(session);
  return (
    <div className={`session-card${view.summarized ? "" : " unsummarized"}`}>
      <div className="session-head">
        <span className="session-span">{view.span}</span>
        <span className="session-meta">{view.meta}</span>
      </div>
      <div className="session-label">{view.label}</div>
      <div className="session-summary">{view.summary}</div>
      {view.important && <div className="scan-important">{view.important}</div>}
      {view.reminder && <div className="scan-reminder">{view.reminder}</div>}
      <div className="session-source">{view.source}</div>
    </div>
  );
}

export default SessionCard;
