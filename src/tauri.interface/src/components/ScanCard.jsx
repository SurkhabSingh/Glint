import { formatTimestamp, scanView, searchView } from "../glint";

/**
 * Live placeholder logged the moment a scan tick starts on a window.
 * Replaced by the real ScanCard when the tick's outcome arrives.
 */
export function PendingScanCard({ tick }) {
  const source =
    tick.process && tick.title
      ? `${tick.process} | ${tick.title}`
      : (tick.process ?? tick.title ?? "Current window");
  return (
    <div className="scan-card pending">
      <div className="scan-time">{formatTimestamp(tick.startedAtMs)}</div>
      <div className="scan-live-row">
        <span className="live-dot" aria-hidden="true" />
        <span className="scan-label">Capturing {tick.process ?? "current window"}…</span>
      </div>
      <div className="scan-summary">
        Reading on-screen text now. Capture no longer waits on the model; the
        summary arrives with this stretch's session.
      </div>
      <div className="scan-source">{source}</div>
    </div>
  );
}

/**
 * Ports the WinUI scan-history item template (and the search-result
 * template): timestamp, label, summary, important signals, reminder,
 * source, metrics, status, plus the collapsible redacted-input inspector.
 * Pass `search` (a ContextSearchResult) instead of `scan` for the
 * search-page variant, which shows the FTS snippet instead of metrics.
 */
function ScanCard({ scan, search, session }) {
  if (search) {
    const view = searchView(search);
    return (
      <div className="scan-card">
        <div className="scan-time">{view.timestamp}</div>
        <div className="scan-label">{view.label}</div>
        <div className="scan-summary">{view.summary}</div>
        {view.important && <div className="scan-important">{view.important}</div>}
        {view.reminder && <div className="scan-reminder">{view.reminder}</div>}
        {view.snippet && <div className="scan-snippet">{view.snippet}</div>}
        <div className="scan-source">{view.source}</div>
      </div>
    );
  }

  const view = scanView(scan);
  const hasDiagnostics = (view.inputText ?? "").trim().length > 0;
  const hasOcr = (view.ocrText ?? "").trim().length > 0;
  const hasUia = (view.uiaText ?? "").trim().length > 0;
  // A grouped card promises its summary is on the session above: only say
  // that when the session's summary is actually rendered there. Otherwise
  // say what is true (pending, too minor to summarize, or unknown state).
  const grouped = Boolean(scan.sessionId);
  const summaryText = !grouped
    ? view.summary
    : session?.summary
      ? view.summary
      : session?.isMinor
        ? "Too little on screen to summarize."
        : session
          ? "Grouped into a session — summary pending."
          : "Grouped into a session.";

  return (
    <div className="scan-card">
      <div className="scan-time">{view.timestamp}</div>
      <div className="scan-label">{view.label}</div>
      <div className="scan-summary">{summaryText}</div>
      {view.important && <div className="scan-important">{view.important}</div>}
      {view.reminder && <div className="scan-reminder">{view.reminder}</div>}
      <div className="scan-source">{view.source}</div>
      <div className="scan-metrics">{view.metrics}</div>
      <div className="scan-status">{view.status}</div>
      {hasDiagnostics && (
        <details className="scan-expander">
          <summary>Inspect redacted OCR/UIA input</summary>
          <div className="scan-expander-body">
            <div className="scan-source">{view.inputSummary}</div>
            <h5>Combined redacted text used for Gemma context selection</h5>
            <div className="scroll">
              <div className="mono">{view.inputText}</div>
            </div>
            {hasOcr && (
              <>
                <h5>OCR text only</h5>
                <div className="scroll short">
                  <div className="mono">{view.ocrText}</div>
                </div>
              </>
            )}
            {hasUia && (
              <>
                <h5>UI Automation text only</h5>
                <div className="scroll short">
                  <div className="mono">{view.uiaText}</div>
                </div>
              </>
            )}
          </div>
        </details>
      )}
    </div>
  );
}

export default ScanCard;
