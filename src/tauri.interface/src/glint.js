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
export const glintSetGlassTint = ({ r, g, b, alpha }) =>
  invoke("glint_set_glass_tint", { r, g, b, alpha });
export const glintStartScanning = () => invoke("glint_start_scanning");
export const glintPauseScanning = () => invoke("glint_pause_scanning");
export const glintScanState = () => invoke("glint_scan_state");
export const glintCaptureOnce = () => invoke("glint_capture_once");
export const glintOpenSearch = (query = null) =>
  invoke("glint_open_search", { query });
export const glintShowMain = () => invoke("glint_show_main");

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

/** Ports ManualScanItemViewModel derived text for a ManualScanRecord. */
export function scanView(scan) {
  const label = scan.label ?? "Gemma summary failed";
  const summary = scan.summary ?? scan.error ?? "No summary was produced.";
  const important = (scan.importantSignals ?? "").trim();
  const reminder = (scan.reminderCandidate ?? "").trim();
  const source = (scan.windowTitle ?? "").trim()
    ? `${scan.processName} | ${scan.windowTitle}`
    : scan.processName;
  const lang = (scan.ocrLanguage ?? "").trim() || "default";
  const metrics =
    `UIA ${scan.uiAutomationCharacters} chars | OCR ${scan.ocrCharacters} chars (${lang}) | ` +
    `${scan.redactions} redactions | capture ${Math.round(scan.captureMilliseconds)} ms | ` +
    `OCR ${Math.round(scan.ocrMilliseconds)} ms | Gemma ${Math.round(scan.inferenceMilliseconds)} ms`;
  const status =
    scan.status === 0
      ? `Summarized locally with ${scan.modelId}`
      : `OCR saved; ${scan.modelId} failed`;
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
