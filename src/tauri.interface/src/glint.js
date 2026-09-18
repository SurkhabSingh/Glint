import { invoke } from "@tauri-apps/api/core";

// ---------------------------------------------------------------------------
// invoke wrappers (one per Tauri command in src-tauri/src/glint.rs)
// ---------------------------------------------------------------------------

export const glintInitialize = () => invoke("glint_initialize");
export const glintProbe = () => invoke("glint_probe");
export const glintVerifyStorage = () => invoke("glint_verify_storage");
export const glintCheckCompatibility = () =>
  invoke("glint_check_compatibility");
export const glintRequestBorderless = () =>
  invoke("glint_request_borderless");
export const glintSearch = (query) => invoke("glint_search", { query });
export const glintHistory = (limit = 50) =>
  invoke("glint_history", { limit });
export const glintImportModel = (modelPath) =>
  invoke("glint_import_model", { modelPath });
export const glintEnsureRuntime = () => invoke("glint_ensure_runtime");
export const glintSetupRuntime = () => invoke("glint_setup_runtime");
export const glintTimeline = (date = null) =>
  invoke("glint_timeline", { date });
export const glintShortcutStatus = () => invoke("glint_shortcut_status");
export const glintAsk = (question, scope, day = null, threadId = null) =>
  invoke("glint_ask", { question, scope, day, threadId });
export const glintChatThreads = (limit = 50) =>
  invoke("glint_chat_threads", { limit });
export const glintChatThread = (id, limit = 200) =>
  invoke("glint_chat_thread", { id, limit });
export const glintChatCreate = (title, scope = null) =>
  invoke("glint_chat_create", { title, scope });
export const glintChatRename = (id, title) =>
  invoke("glint_chat_rename", { id, title });
export const glintChatDelete = (id) => invoke("glint_chat_delete", { id });
export const glintChatAppend = (thread, role, text, citations = null, scoped = null) =>
  invoke("glint_chat_append", { thread, role, text, citations, scoped });

/** Clock time with milliseconds: "7:34:13.043 PM" (accountability rows). */
export function formatClock(ms) {
  const date = new Date(Number(ms));
  let hours = date.getHours();
  const ampm = hours >= 12 ? "PM" : "AM";
  hours = hours % 12;
  if (hours === 0) hours = 12;
  const pad = (n, w = 2) => String(n).padStart(w, "0");
  return `${hours}:${pad(date.getMinutes())}:${pad(date.getSeconds())}.${pad(date.getMilliseconds(), 3)} ${ampm}`;
}
export const glintSetGlassTint = ({ r, g, b, alpha }) =>
  invoke("glint_set_glass_tint", { r, g, b, alpha });
export const glintStartScanning = () => invoke("glint_start_scanning");
export const glintPauseScanning = () => invoke("glint_pause_scanning");
export const glintScanState = () => invoke("glint_scan_state");
export const glintCaptureOnce = () => invoke("glint_capture_once");
export const glintOpenSearch = (query = null) =>
  invoke("glint_open_search", { query });
export const glintShowMain = () => invoke("glint_show_main");
export const glintSessions = (limit = 50) =>
  invoke("glint_sessions", { limit });
export const glintSetSessionOutcome = (id, outcome) =>
  invoke("glint_set_session_outcome", { id, outcome });

// ---------------------------------------------------------------------------
// View-model ports (mirror ManualScanItemViewModel /
// ContextSearchItemViewModel fallback and formatting rules)
// ---------------------------------------------------------------------------

const MONTHS = [
  "Jan", "Feb", "Mar", "Apr", "May", "Jun",
  "Jul", "Aug", "Sept", "Oct", "Nov", "Dec",
];

/** "MMM d, yyyy h:mm:ss tt" in local time (.NET renders Sept with 4 letters). */
export function formatTimestamp(capturedAtMs) {
  const date = new Date(Number(capturedAtMs));
  const month = MONTHS[date.getMonth()];
  const day = date.getDate();
  const year = date.getFullYear();
  let hours = date.getHours();
  const ampm = hours >= 12 ? "PM" : "AM";
  hours = hours % 12;
  if (hours === 0) hours = 12;
  const pad = (n) => String(n).padStart(2, "0");
  return `${month} ${day}, ${year} ${hours}:${pad(date.getMinutes())}:${pad(date.getSeconds())} ${ampm}`;
}

/**
 * Model output that says nothing. The summarizer emits a full sentence when
 * it found no commitments or deadlines, which is noise on every card.
 */
function meaningful(text) {
  const trimmed = (text ?? "").trim();
  if (!trimmed) return "";
  const empty = /^(none|n\/a|no explicit|no commitments|nothing)\b/i;
  return empty.test(trimmed) ? "" : trimmed;
}

// Enums cross the bridge as their numeric value, like scan.status does, so
// these must stay in the order the C# enums declare.
const OUTCOMES = ["unknown", "open", "settled", "superseded"];

/** A session's outcome as a name rather than an enum ordinal. */
export const outcomeOf = (session) => OUTCOMES[session.outcome ?? 0] ?? "unknown";
const OUTCOME_SOURCES = ["none", "rule", "recurrence", "user"];

/** Derived display text for one activity session. */
export function sessionView(session) {
  const started = session.startedAtMilliseconds;
  const ended = session.endedAtMilliseconds;
  const minutes = Math.round((ended - started) / 60000);
  const duration = minutes >= 1 ? `${minutes} min` : "under a minute";
  const captures = session.scanIds?.length ?? 0;
  const summarized = Boolean(session.summary);
  const minor = Boolean(session.isMinor);
  const outcome = OUTCOMES[session.outcome ?? 0] ?? "unknown";
  const outcomeSource = OUTCOME_SOURCES[session.outcomeSource ?? 0] ?? "none";
  return {
    summarized,
    minor,
    outcome,
    outcomeSource,
    outcomeLabel:
      outcome === "open"
        ? "Unfinished"
        : outcome === "superseded"
          ? "Superseded"
          : "Done",
    // An outstanding thing nobody has touched in a week is worth flagging,
    // but it is still only outstanding — not abandoned, and not done.
    stale: outcome === "open" && Date.now() - started > 7 * 86400000,
    supersededNote:
      outcome === "superseded"
        ? "A later session carried this forward."
        : "",
    // Only say where a verdict came from when the user set it, so their own
    // decision is visibly theirs rather than something Glint guessed.
    outcomeNote: outcomeSource === "user" ? "You marked this" : "",
    label: session.label ?? session.windowTitle ?? session.processName,
    summary: summarized
      ? session.summary
      : minor
        ? "Too little on screen to summarize."
        : "Not summarized yet — summaries run when scanning stops.",
    important: meaningful(session.importantSignals),
    reminder: meaningful(session.reminderCandidate),
    source: session.processName,
    span: `${formatClock(started)} – ${formatClock(ended)}`,
    meta: `${duration} · ${captures} capture${captures === 1 ? "" : "s"}`,
  };
}

/** Ports ManualScanItemViewModel derived text for a ManualScanRecord. */
export function scanView(scan) {
  // Captures no longer carry their own summary: the label is derived from
  // the window and the summary belongs to the session.
  const label = scan.label ?? scan.windowTitle ?? scan.processName;
  const grouped = Boolean(scan.sessionId);
  const summary =
    scan.summary ??
    scan.error ??
    (grouped
      ? "Part of a session — its summary is on the session above."
      : "Stored. It joins a session once this stretch of work ends.");
  const important = meaningful(scan.importantSignals);
  const reminder = meaningful(scan.reminderCandidate);
  const source = (scan.windowTitle ?? "").trim()
    ? `${scan.processName} | ${scan.windowTitle}`
    : scan.processName;
  const lang = (scan.ocrLanguage ?? "").trim() || "default";
  const inference = Math.round(scan.inferenceMilliseconds ?? 0);
  const metrics =
    `UIA ${scan.uiAutomationCharacters} chars | OCR ${scan.ocrCharacters} chars (${lang}) | ` +
    `${scan.redactions} redactions | capture ${Math.round(scan.captureMilliseconds)} ms | ` +
    `OCR ${Math.round(scan.ocrMilliseconds)} ms` +
    (inference > 0 ? ` | Gemma ${inference} ms` : "");
  const status = scan.error
    ? `Capture stored; ${scan.error}`
    : grouped
      ? "Stored and grouped into a session"
      : "Stored locally";
  const contextChars =
    scan.gemmaContextCharacters > 0
      ? Number(scan.gemmaContextCharacters).toLocaleString("en-US")
      : "not recorded";
  const inputText = scan.redactedInputText ?? "";
  return {
    timestamp: formatTimestamp(scan.capturedAtMilliseconds),
    label,
    summary,
    important: important ? `Important: ${important}` : "",
    reminder: reminder ? `Potential reminder: ${reminder}` : "",
    source,
    metrics,
    status,
    inputSummary:
      `Full redacted capture text: ${inputText.length.toLocaleString("en-US")} chars. ` +
      `Gemma context budget: ${contextChars} chars. ` +
      "This is stored locally for Phase 0 prompt debugging.",
    inputText,
    ocrText: scan.redactedOcrText ?? "",
    uiaText: scan.redactedUiAutomationText ?? "",
  };
}

/** Ports ContextSearchItemViewModel derived text for a search hit. */
export function searchView(result) {
  const label = result.label ?? result.windowTitle;
  const summary =
    result.summary ?? "No derived summary is available for this capture.";
  const important = (result.importantSignals ?? "").trim();
  const reminder = (result.reminderCandidate ?? "").trim();
  const source = (result.windowTitle ?? "").trim()
    ? `${result.processName} | ${result.windowTitle}`
    : result.processName;
  return {
    timestamp: formatTimestamp(result.capturedAtMilliseconds),
    label,
    summary,
    important: important ? `Important: ${important}` : "",
    reminder: reminder ? `Potential reminder: ${reminder}` : "",
    source,
    snippet: result.snippet ?? "",
  };
}

export function historySummaryText(count) {
  if (count === 0) return "No scans yet.";
  if (count === 1) return "1 timestamped scan.";
  return `${count} timestamped scans.`;
}

// ---------------------------------------------------------------------------
// QuickCommandParser port (Glint.Phase0.Core/QuickCommand.cs)
// ---------------------------------------------------------------------------

export const QuickCommandKind = {
  CaptureCurrentWindow: "capture",
  StartScanning: "start",
  PauseScanning: "pause",
  SearchContext: "search",
  OpenDashboard: "open",
};

function removeLeadingIntent(value, ...intents) {
  const ordered = [...intents].sort((a, b) => b.length - a.length);
  for (const intent of ordered) {
    if (value.toLowerCase().startsWith(intent.toLowerCase())) {
      return value.slice(intent.length).replace(/^[\s:\-\t]+/, "");
    }
  }
  return value;
}

function containsAny(lower, ...terms) {
  return terms.some((term) => lower.includes(term));
}

/** Ports QuickCommandParser.Parse; returns { kind, query }. */
export function parseQuickCommand(input) {
  let command = input.trim();
  let lower = command.toLowerCase();
  if (lower.startsWith("@glint")) {
    command = command.slice(6).trim();
    lower = command.toLowerCase();
  }

  if (
    lower.startsWith("search") ||
    lower.startsWith("find") ||
    lower.startsWith("look up")
  ) {
    let query = removeLeadingIntent(command, "search", "find", "look up");
    if (/^(your context|my context|context)$/i.test(query)) {
      query = "";
    }
    return {
      kind: QuickCommandKind.SearchContext,
      query: query.trim() ? query.trim() : null,
    };
  }
  if (containsAny(lower, "pause", "stop scanning", "stop capture")) {
    return { kind: QuickCommandKind.PauseScanning, query: null };
  }
  if (
    containsAny(
      lower,
      "start scanning",
      "start capture",
      "resume scanning",
      "resume capture"
    )
  ) {
    return { kind: QuickCommandKind.StartScanning, query: null };
  }
  if (containsAny(lower, "capture", "scan this", "scan current", "grab this")) {
    return { kind: QuickCommandKind.CaptureCurrentWindow, query: null };
  }
  if (
    containsAny(lower, "open glint", "open dashboard", "show glint", "show dashboard")
  ) {
    return { kind: QuickCommandKind.OpenDashboard, query: null };
  }
  if (!command) {
    return { kind: QuickCommandKind.OpenDashboard, query: null };
  }
  return { kind: QuickCommandKind.SearchContext, query: command };
}
