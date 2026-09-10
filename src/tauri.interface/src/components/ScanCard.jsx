import { scanView, searchView } from "../glint";

/**
 * Ports the WinUI scan-history item template (and the search-result
 * template): timestamp, label, summary, important signals, reminder,
 * source, metrics, status, plus the collapsible redacted-input inspector.
 * Pass `search` (a ContextSearchResult) instead of `scan` for the
 * search-page variant, which shows the FTS snippet instead of metrics.
 */
function ScanCard({ scan, search }) {
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

  return (
    <div className="scan-card">
      <div className="scan-time">{view.timestamp}</div>
      <div className="scan-label">{view.label}</div>
      <div className="scan-summary">{view.summary}</div>
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
