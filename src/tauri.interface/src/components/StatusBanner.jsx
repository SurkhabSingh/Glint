/** Ports the WinUI InfoBar: severity-tinted banner with title + message. */
function StatusBanner({ overall }) {
  if (!overall) return null;
  const severity = overall.severity ?? "info";
  const mark = severity === "success" ? "✓" : severity === "error" ? "!" : "i";
  return (
    <div className={`status-banner ${severity}`} role="status">
      <span className="status-dot">{mark}</span>
      <span>
        <span className="status-title">{overall.title}</span>
        {overall.message}
      </span>
    </div>
  );
}

export default StatusBanner;
