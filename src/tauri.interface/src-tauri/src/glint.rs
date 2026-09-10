//! Glint Phase 0 frontend bridge.
//!
//! Ports `Glint.Phase0.App/MainViewModel.cs` (which called
//! `Glint.Phase0.Core` directly) onto Tauri commands backed by the
//! `Glint.Phase0.Cli` sidecar — see `bridge.rs` for the transport.
//! All user-facing strings mirror the WinUI view model verbatim.

use std::path::PathBuf;
use std::sync::Mutex;
use tauri::{AppHandle, Emitter, Manager, State};
use tokio::sync::watch;

// ---------------------------------------------------------------------------
// Outcome / reason maps (must match Core enum order; CLI emits numbers)
// ---------------------------------------------------------------------------

const OUTCOME_KINDS: [&str; 6] = [
    "Completed",
    "ModelFailed",
    "Unchanged",
    "Suppressed",
    "DroppedSecretFrame",
    "Failed",
];

const SUPPRESS_REASONS: [&str; 13] = [
    "NoForegroundWindow",
    "SelfCapture",
    "Minimized",
    "SecureDesktop",
    "DesktopStateUnknown",
    "ElevatedProcess",
    "ElevationStateUnknown",
    "DisplayProtected",
    "PasswordField",
    "AutomationStateUnknown",
    "BlocklistedApplication",
    "PrivateBrowsing",
    "SensitiveWindowTitle",
];

fn kind_name(kind: i64) -> &'static str {
    OUTCOME_KINDS
        .get(kind as usize)
        .copied()
        .unwrap_or("Failed")
}

fn reason_name(reason: i64) -> &'static str {
    SUPPRESS_REASONS
        .get(reason as usize)
        .copied()
        .unwrap_or("NoForegroundWindow")
}

// ---------------------------------------------------------------------------
// Overall status banner (ports SetOverallSuccess/Error/Information)
// ---------------------------------------------------------------------------

fn overall_success(message: String) -> serde_json::Value {
    serde_json::json!({
        "title": "Check completed",
        "message": message,
        "severity": "success",
    })
}

fn overall_error(message: String) -> serde_json::Value {
    serde_json::json!({
        "title": "Check failed",
        "message": message,
        "severity": "error",
    })
}

fn overall_info(message: String) -> serde_json::Value {
    serde_json::json!({
        "title": "Scanning status",
        "message": message,
        "severity": "info",
    })
}

// ---------------------------------------------------------------------------
// Summary text ports (MainViewModel.*Summary)
// ---------------------------------------------------------------------------

/// "MMM d, yyyy h:mm:ss tt" — .NET renders September as "Sept".
pub fn format_timestamp(captured_at_ms: i64) -> String {
    use chrono::{Local, TimeZone};
    let local = Local
        .timestamp_millis_opt(captured_at_ms)
        .single()
        .unwrap_or_else(Local::now);
    let text = local.format("%b %-d, %Y %-I:%M:%S %p").to_string();
    if let Some(rest) = text.strip_prefix("Sep ") {
        format!("Sept {rest}")
    } else {
        text
    }
}

fn foreground_port(
    window: Option<&serde_json::Value>,
    decision: &serde_json::Value,
) -> (String, serde_json::Value) {
    match window {
        None => {
            let message = "Windows did not report a foreground window.".to_string();
            (message.clone(), overall_error(message))
        }
        Some(window) => {
            let process = window
                .get("processName")
                .and_then(|v| v.as_str())
                .unwrap_or("Unknown");
            let allowed = decision.get("allowed").and_then(|v| v.as_bool()).unwrap_or(false);
            if allowed {
                (
                    format!("{process}: allowed; UI Automation state is known."),
                    overall_success("Foreground privacy state was determined.".to_string()),
                )
            } else {
                let reason = decision
                    .get("reason")
                    .and_then(|v| v.as_i64())
                    .map(reason_name)
                    .unwrap_or("NoForegroundWindow");
                let detail = decision
                    .get("detail")
                    .and_then(|v| v.as_str())
                    .unwrap_or("suppressed");
                (
                    format!("{process}: suppressed ({reason}) - {detail}"),
                    overall_success("Foreground privacy state was determined.".to_string()),
                )
            }
        }
    }
}

fn storage_port(storage: &serde_json::Value, event_count: i64) -> String {
    let version = storage
        .get("sqlCipherVersion")
        .and_then(|v| v.as_str())
        .unwrap_or("unknown");
    let fts = storage
        .get("fts5Available")
        .and_then(|v| v.as_bool())
        .unwrap_or(false);
    let vec = storage
        .get("sqliteVecAvailable")
        .and_then(|v| v.as_bool())
        .unwrap_or(false);
    format!("SQLCipher {version}; FTS5: {fts}; sqlite-vec: {vec}; events: {event_count}.")
}

fn compatibility_port(report: &serde_json::Value) -> (String, Vec<serde_json::Value>, bool) {
    let build = report.get("osBuild").and_then(|v| v.as_i64()).unwrap_or(0);
    let arch = report
        .get("osArchitecture")
        .and_then(|v| v.as_str())
        .unwrap_or("unknown");
    let core = report
        .get("readyForCoreCapture")
        .and_then(|v| v.as_bool())
        .unwrap_or(false);
    let models = report
        .get("readyForLocalModels")
        .and_then(|v| v.as_bool())
        .unwrap_or(false);
    let checks = report
        .get("checks")
        .and_then(|v| v.as_array())
        .cloned()
        .unwrap_or_default();
    let failed: Vec<String> = checks
        .iter()
        .filter(|c| !c.get("passed").and_then(|v| v.as_bool()).unwrap_or(false))
        .filter_map(|c| {
            c.get("id")
                .and_then(|v| v.as_str())
                .map(|s| s.to_string())
        })
        .collect();
    let mut summary = format!(
        "Windows build {build}; {arch}; core capture: {}; local models: {}.",
        if core { "ready" } else { "blocked" },
        if models { "ready" } else { "limited" }
    );
    if failed.is_empty() {
        summary.push_str(" All checks passed.");
    } else {
        summary.push_str(&format!(" Review: {}.", failed.join(", ")));
    }
    for check in &checks {
        let passed = check.get("passed").and_then(|v| v.as_bool()).unwrap_or(false);
        let required = check
            .get("required")
            .and_then(|v| v.as_bool())
            .unwrap_or(false);
        let id = check.get("id").and_then(|v| v.as_str()).unwrap_or("?");
        let detail = check.get("detail").and_then(|v| v.as_str()).unwrap_or("");
        let verdict = if passed {
            "PASS"
        } else if required {
            "FAIL"
        } else {
            "WARN"
        };
        summary.push_str(&format!("\n{verdict} {id}: {detail}"));
    }
    (summary, checks, core)
}

fn runtime_port(resolution: &serde_json::Value) -> (String, bool) {
    let ready = resolution
        .get("isReady")
        .and_then(|v| v.as_bool())
        .unwrap_or(false);
    let base = if ready {
        "Gemma 4 E2B is ready through the local LiteRT worker.".to_string()
    } else {
        let missing = resolution
            .get("missing")
            .and_then(|v| v.as_array())
            .map(|items| {
                items
                    .iter()
                    .filter_map(|v| v.as_str())
                    .collect::<Vec<_>>()
                    .join(", ")
            })
            .unwrap_or_else(|| "unknown".to_string());
        format!("Gemma is unavailable. Missing: {missing}.")
    };
    let summary = match resolution.get("modelPath").and_then(|v| v.as_str()) {
        Some(path) => format!("{base} Model: {path}"),
        None => base,
    };
    (summary, ready)
}

fn history_summary(count: usize) -> String {
    match count {
        0 => "No scans yet.".to_string(),
        1 => "1 timestamped scan.".to_string(),
        _ => format!("{count} timestamped scans."),
    }
}

fn search_summary(query: &str, count: usize) -> String {
    match count {
        0 if query.trim().is_empty() => "Enter a person, application, topic, or phrase.".to_string(),
        0 => format!("No local context matched \"{query}\"."),
        1 => "1 local context result.".to_string(),
        _ => format!("{count} local context results."),
    }
}

// ---------------------------------------------------------------------------
// Scan-outcome payload (ports ApplyScanOutcome)
// ---------------------------------------------------------------------------

fn outcome_payload(outcome: &serde_json::Value) -> serde_json::Value {
    let kind = outcome.get("kind").and_then(|v| v.as_i64()).unwrap_or(5);
    let kind_name = kind_name(kind);
    let detail = outcome
        .get("detail")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_string();
    let record = outcome.get("record").cloned().unwrap_or(serde_json::Value::Null);
    let reason = outcome
        .get("suppressReason")
        .and_then(|v| v.as_i64())
        .map(reason_name);

    let capture_summary = match kind_name {
        "Completed" => {
            let process = record
                .get("processName")
                .and_then(|v| v.as_str())
                .unwrap_or("?");
            let label = record.get("label").and_then(|v| v.as_str()).unwrap_or("");
            let ms = record
                .get("inferenceMilliseconds")
                .and_then(|v| v.as_f64())
                .unwrap_or(0.0);
            format!("{process}: {label} ({ms:.0} ms Gemma). Continuing to scan.")
        }
        "ModelFailed" | "DroppedSecretFrame" | "Failed" => {
            format!("{detail} Continuing to scan.")
        }
        "Unchanged" => "Scanning is active; the current visible content is unchanged.".to_string(),
        "Suppressed" => format!("Scanning is active; skipped the foreground window: {detail}"),
        _ => format!("{detail} Continuing to scan."),
    };

    let overall = match kind_name {
        "Completed" => overall_success(
            "The window was scanned, summarized locally, and saved. Scanning remains active."
                .to_string(),
        ),
        "Unchanged" | "Suppressed" => overall_info(capture_summary.clone()),
        _ => overall_error(capture_summary.clone()),
    };

    serde_json::json!({
        "kind": kind,
        "kindName": kind_name,
        "detail": detail,
        "record": record,
        "suppressReason": reason,
        "captureSummary": capture_summary,
        "overall": overall,
    })
}

// ---------------------------------------------------------------------------
// Scan loop state (ports StartScanningAsync / PauseScanningAsync)
// ---------------------------------------------------------------------------

#[derive(Default)]
struct ScanState {
    scanning: bool,
    generation: u64,
    /// In-flight scan child: taken by whichever select branch wins.
    /// Short synchronous takes only — never held across await.
    child: Option<tokio::process::Child>,
}

fn take_child(app: &AppHandle) -> Option<tokio::process::Child> {
    let runtime: State<ScanRuntime> = app.state();
    let child = runtime.state.lock().unwrap().child.take();
    child
}

pub struct ScanRuntime {
    state: Mutex<ScanState>,
    cancel: watch::Sender<u64>,
}

impl Default for ScanRuntime {
    fn default() -> Self {
        let (cancel, _) = watch::channel(0);
        Self {
            state: Mutex::new(ScanState::default()),
            cancel,
        }
    }
}

fn is_current(app: &AppHandle, generation: u64) -> bool {
    let runtime: State<ScanRuntime> = app.state();
    let state = runtime.state.lock().unwrap();
    state.scanning && state.generation == generation
}

fn manual_scan_args(app: &AppHandle, root: &std::path::Path) -> Result<(PathBuf, Vec<String>), String> {
    let cli = crate::bridge::sidecar_path(app)?;
    let mut args = vec!["manual-scan".to_string()];
    args.extend(crate::bridge::db_args(app, root));
    Ok((cli, args))
}

async fn run_manual_scan(
    app: &AppHandle,
    generation: u64,
) -> Option<serde_json::Value> {
    let root = crate::bridge::data_root().ok()?;
    let (cli, args) = manual_scan_args(app, &root).ok()?;

    let child = tokio::process::Command::new(&cli)
        .args(&args)
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::piped())
        .spawn()
        .ok()?;

    // Park the in-flight child in the shared slot — but only if this
    // generation is still current (a newer start may have taken over).
    // The lock scope ends before any await so the future stays Send.
    let mut slot = Some(child);
    let parked = {
        let runtime: State<ScanRuntime> = app.state();
        let mut state = runtime.state.lock().unwrap();
        if state.scanning && state.generation == generation {
            state.child = slot.take();
            true
        } else {
            false
        }
    };
    if !parked {
        if let Some(mut child) = slot {
            let _ = child.kill().await;
            let _ = child.wait().await;
        }
        return None;
    }

    // Await the scan, killing in-flight capture/inference the moment this
    // generation is cancelled (ports loop cancellation on Pause).
    let output: Option<std::process::Output> = {
        let runtime: State<ScanRuntime> = app.state();
        let mut cancel = runtime.cancel.subscribe();
        tokio::select! {
            output = async {
                match take_child(app) {
                    Some(child) => child.wait_with_output().await.ok(),
                    None => None,
                }
            } => output,
            _ = cancel.changed() => {
                if let Some(mut child) = take_child(app) {
                    let _ = child.kill().await;
                    let _ = child.wait().await;
                }
                None
            }
        }
    };
    let output = output?;

    if !is_current(app, generation) {
        return None; // finished stale: discard, like a cancelled loop iteration
    }
    serde_json::from_str::<serde_json::Value>(&String::from_utf8_lossy(&output.stdout)).ok()
}

async fn scan_loop(app: AppHandle, generation: u64) {
    loop {
        if !is_current(&app, generation) {
            break;
        }
        match run_manual_scan(&app, generation).await {
            None => break, // paused or superseded
            Some(outcome) => {
                let payload = outcome_payload(&outcome);
                let _ = app.emit("scan-outcome", &payload);
            }
        }
        // 1 s cadence between scans (ports ScanInterval), abortable by pause.
        {
            let runtime: State<ScanRuntime> = app.state::<ScanRuntime>();
            let mut rx = runtime.cancel.subscribe();
            tokio::select! {
                _ = tokio::time::sleep(std::time::Duration::from_secs(1)) => {}
                _ = rx.changed() => {
                    if !is_current(&app, generation) {
                        break;
                    }
                }
            }
        }
    }

    // Loop exit owns the final paused state (ports the finally/catch block).
    let should_finalize = {
        let runtime: State<ScanRuntime> = app.state::<ScanRuntime>();
        let mut state = runtime.state.lock().unwrap();
        if state.generation == generation && state.scanning {
            state.scanning = false;
            true
        } else {
            false
        }
    };
    if should_finalize {
        let _ = app.emit(
            "scan-state",
            serde_json::json!({
                "scanning": false,
                "captureSummary": "Scanning paused.",
                "overall": overall_info("Continuous local scanning is paused.".to_string()),
            }),
        );
        crate::refresh_tray(&app, false);
    }
}

// ---------------------------------------------------------------------------
// Tauri commands
// ---------------------------------------------------------------------------

/// Full dashboard bootstrap (ports InitializeAsync + LoadScanHistory).
#[tauri::command]
pub fn glint_initialize(app: AppHandle) -> Result<serde_json::Value, String> {
    let root = crate::bridge::data_root()?;
    let db_args = crate::bridge::db_args(&app, &root);
    let arg_refs: Vec<&str> = db_args.iter().map(|s| s.as_str()).collect();

    // Compatibility (exit 2 tolerated: report still parses).
    let compat_out = crate::bridge::run_sidecar(&app, &["compatibility"])?;
    let (compatibility_summary, checks, core_ready) = compatibility_port(&compat_out.json);
    let mut overall = if core_ready {
        overall_success("This machine meets the required Phase 0 runtime checks.".to_string())
    } else {
        overall_error("This machine does not meet the required Phase 0 runtime checks.".to_string())
    };

    // Storage overwrites overall (last-writer-wins, like the view model).
    let mut storage_args = vec!["storage"];
    storage_args.extend(arg_refs.clone());
    let storage_summary = match crate::bridge::run_sidecar(&app, &storage_args) {
        Ok(out) => {
            let events = out.json.get("eventCount").and_then(|v| v.as_i64()).unwrap_or(0);
            let summary = storage_port(
                out.json.get("database").unwrap_or(&serde_json::Value::Null),
                events,
            );
            overall = overall_success(
                "Encrypted storage initialized without plaintext fallback.".to_string(),
            );
            summary
        }
        Err(error) => {
            let summary = format!("Storage failed: {error}");
            overall = overall_error(summary.clone());
            summary
        }
    };

    // Runtime status for the model line.
    let (model_summary, runtime_ready, model_path) =
        match crate::bridge::run_sidecar(&app, &["runtime-status"]) {
            Ok(out) => {
                let (summary, ready) = runtime_port(&out.json);
                let path = out
                    .json
                    .get("modelPath")
                    .and_then(|v| v.as_str())
                    .map(|s| s.to_string());
                (summary, ready, path)
            }
            Err(error) => (format!("Gemma runtime check failed: {error}"), false, None),
        };

    // Privacy probe overwrites overall last.
    let (foreground_summary, probe_overall) = match crate::bridge::run_sidecar(&app, &["probe"]) {
        Ok(out) => foreground_port(
            out.json.get("window").filter(|v| !v.is_null()),
            out.json.get("decision").unwrap_or(&serde_json::Value::Null),
        ),
        Err(error) => {
            let message = format!("Probe failed: {error}");
            (message.clone(), overall_error(message))
        }
    };
    overall = probe_overall;

    // Scan history.
    let mut history_args = vec!["manual-history", "--limit", "50"];
    history_args.extend(arg_refs);
    let scans = crate::bridge::run_sidecar(&app, &history_args)
        .ok()
        .and_then(|out| out.json.get("scans").cloned())
        .unwrap_or(serde_json::Value::Array(vec![]));
    let history_count = scans.as_array().map(|a| a.len()).unwrap_or(0);

    // Auto-wire the LiteRT runtime (python/worker env) at startup.
    let runtime = crate::runtime::ensure_runtime(&app);

    Ok(serde_json::json!({
        "overall": overall,
        "compatibilitySummary": compatibility_summary,
        "compatibilityReady": core_ready,
        "checks": checks,
        "storageSummary": storage_summary,
        "modelSummary": model_summary,
        "runtimeReady": runtime_ready,
        "modelPath": model_path,
        "runtime": runtime,
        "foregroundSummary": foreground_summary,
        "history": scans,
        "historySummary": history_summary(history_count),
    }))
}

/// Foreground privacy probe (ports ProbeAsync).
#[tauri::command]
pub fn glint_probe(app: AppHandle) -> Result<serde_json::Value, String> {
    match crate::bridge::run_sidecar(&app, &["probe"]) {
        Ok(out) => {
            let (foreground_summary, overall) = foreground_port(
                out.json.get("window").filter(|v| !v.is_null()),
                out.json.get("decision").unwrap_or(&serde_json::Value::Null),
            );
            Ok(serde_json::json!({
                "foregroundSummary": foreground_summary,
                "overall": overall,
            }))
        }
        Err(error) => {
            let message = format!("Probe failed: {error}");
            Ok(serde_json::json!({
                "foregroundSummary": message,
                "overall": overall_error(message),
            }))
        }
    }
}

/// Encrypted storage verification (ports CheckStorageAsync).
#[tauri::command]
pub fn glint_verify_storage(app: AppHandle) -> Result<serde_json::Value, String> {
    let root = crate::bridge::data_root()?;
    let db_args = crate::bridge::db_args(&app, &root);
    let mut args = vec!["storage"];
    args.extend(db_args.iter().map(|s| s.as_str()));
    match crate::bridge::run_sidecar(&app, &args) {
        Ok(out) => {
            let events = out.json.get("eventCount").and_then(|v| v.as_i64()).unwrap_or(0);
            let summary = storage_port(
                out.json.get("database").unwrap_or(&serde_json::Value::Null),
                events,
            );
            Ok(serde_json::json!({
                "storageSummary": summary,
                "overall": overall_success(
                    "Encrypted storage initialized without plaintext fallback.".to_string(),
                ),
            }))
        }
        Err(error) => {
            let summary = format!("Storage failed: {error}");
            Ok(serde_json::json!({
                "storageSummary": summary,
                "overall": overall_error(summary),
            }))
        }
    }
}

/// Machine compatibility check (ports CheckCompatibilityAsync).
#[tauri::command]
pub fn glint_check_compatibility(app: AppHandle) -> Result<serde_json::Value, String> {
    match crate::bridge::run_sidecar(&app, &["compatibility"]) {
        Ok(out) => {
            let (summary, checks, core_ready) = compatibility_port(&out.json);
            let overall = if core_ready {
                overall_success("This machine meets the required Phase 0 runtime checks.".to_string())
            } else {
                overall_error("This machine does not meet the required Phase 0 runtime checks.".to_string())
            };
            Ok(serde_json::json!({
                "compatibilitySummary": summary,
                "compatibilityReady": core_ready,
                "checks": checks,
                "overall": overall,
            }))
        }
        Err(error) => {
            let summary = format!("Compatibility check failed: {error}");
            Ok(serde_json::json!({
                "compatibilitySummary": summary,
                "overall": overall_error(summary),
            }))
        }
    }
}

/// Borderless-capture consent (ports RequestBorderlessAsync).
#[tauri::command]
pub fn glint_request_borderless(app: AppHandle) -> Result<serde_json::Value, String> {
    match crate::bridge::run_sidecar(&app, &["request-borderless"]) {
        Ok(out) => {
            let allowed = out
                .json
                .get("allowed")
                .and_then(|v| v.as_bool())
                .unwrap_or(false);
            let summary = if allowed {
                "Borderless capture permission is allowed.".to_string()
            } else {
                "Borderless capture permission was not granted; the system border remains."
                    .to_string()
            };
            Ok(serde_json::json!({ "allowed": allowed, "captureSummary": summary }))
        }
        Err(error) => {
            let summary = format!("Borderless permission request failed: {error}");
            Ok(serde_json::json!({
                "allowed": false,
                "captureSummary": summary,
                "overall": overall_error(summary),
            }))
        }
    }
}

/// Rich local-context search (ports SearchContextAsync via search-context).
#[tauri::command]
pub fn glint_search(app: AppHandle, query: String) -> Result<serde_json::Value, String> {
    let trimmed = query.trim().to_string();
    if trimmed.is_empty() {
        return Ok(serde_json::json!({
            "results": [],
            "searchSummary": "Enter a person, application, topic, or phrase.",
        }));
    }
    let root = crate::bridge::data_root()?;
    let db_args = crate::bridge::db_args(&app, &root);
    let mut args = vec!["search-context", "--query", trimmed.as_str(), "--limit", "30"];
    args.extend(db_args.iter().map(|s| s.as_str()));
    match crate::bridge::run_sidecar(&app, &args) {
        Ok(out) => {
            let results = out
                .json
                .get("results")
                .cloned()
                .unwrap_or(serde_json::Value::Array(vec![]));
            let count = results.as_array().map(|a| a.len()).unwrap_or(0);
            Ok(serde_json::json!({
                "results": results,
                "searchSummary": search_summary(&trimmed, count),
            }))
        }
        Err(error) => Ok(serde_json::json!({
            "results": [],
            "searchSummary": format!("Search failed: {error}"),
        })),
    }
}

/// Recent scan history (ports LoadScanHistory).
#[tauri::command]
pub fn glint_history(app: AppHandle, limit: Option<u32>) -> Result<serde_json::Value, String> {
    let root = crate::bridge::data_root()?;
    let db_args = crate::bridge::db_args(&app, &root);
    let limit_text = limit.unwrap_or(50).clamp(1, 500).to_string();
    let mut args = vec!["manual-history", "--limit", limit_text.as_str()];
    args.extend(db_args.iter().map(|s| s.as_str()));
    let scans = crate::bridge::run_sidecar(&app, &args)
        .map(|out| out.json.get("scans").cloned().unwrap_or(serde_json::Value::Array(vec![])))
        .unwrap_or(serde_json::Value::Array(vec![]));
    let count = scans.as_array().map(|a| a.len()).unwrap_or(0);
    Ok(serde_json::json!({
        "history": scans,
        "historySummary": history_summary(count),
    }))
}

/// Gemma model import (ports ImportGemmaModelAsync; writes the
/// `models/active.json` pointer the runtime locator reads).
#[tauri::command]
pub fn glint_import_model(app: AppHandle, model_path: String) -> Result<serde_json::Value, String> {
    let fail = |summary: String| {
        serde_json::json!({
            "modelSummary": summary,
            "overall": overall_error(summary.clone()),
            "runtimeReady": false,
        })
    };
    if model_path.trim().is_empty() || !std::path::Path::new(&model_path).is_file() {
        return Ok(fail(format!("Import failed: file not found at {model_path}.")));
    }
    let extension = std::path::Path::new(&model_path)
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("");
    if !extension.eq_ignore_ascii_case("litertlm") {
        return Ok(fail(format!(
            "Import failed: Glint's LiteRT worker only accepts .litertlm models, not \
             '{extension}'. Download gemma-4-E2B-it.litertlm from litert-community on Hugging Face."
        )));
    }

    let root = crate::bridge::data_root()?;
    let models_dir = root.join("models");
    if let Err(error) = std::fs::create_dir_all(&models_dir) {
        return Ok(fail(format!("Import failed: {error}")));
    }
    // Plain absolute path: canonicalize() would emit a `\\?\` verbatim
    // prefix that leaks into the UI and worker argv.
    let full = crate::runtime::absolute_normalized(std::path::Path::new(&model_path));
    let pointer = serde_json::json!({
        "path": full.to_string_lossy(),
        "importedAt": chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Nanos, true),
    });
    if let Err(error) = std::fs::write(
        models_dir.join("active.json"),
        serde_json::to_string_pretty(&pointer).unwrap_or_default(),
    ) {
        return Ok(fail(format!("Import failed: {error}")));
    }

    match crate::bridge::run_sidecar(&app, &["runtime-status"]) {
        Ok(out) => {
            let (model_summary, ready) = runtime_port(&out.json);
            // Auto-wire python/worker now that a model is selected.
            let runtime = crate::runtime::ensure_runtime(&app);
            let overall = if ready {
                overall_success(format!(
                    "Imported Gemma model: {}",
                    out.json
                        .get("modelPath")
                        .and_then(|v| v.as_str())
                        .unwrap_or(&model_path)
                ))
            } else {
                let missing = out
                    .json
                    .get("missing")
                    .and_then(|v| v.as_array())
                    .map(|items| {
                        items
                            .iter()
                            .filter_map(|v| v.as_str())
                            .collect::<Vec<_>>()
                            .join(", ")
                    })
                    .unwrap_or_else(|| "unknown".to_string());
                overall_error(format!(
                    "Model pointer saved, but the LiteRT runtime is still incomplete: {missing}"
                ))
            };
            Ok(serde_json::json!({
                "modelSummary": model_summary,
                "overall": overall,
                "runtimeReady": ready,
                "runtime": runtime,
            }))
        }
        Err(error) => Ok(fail(format!("Import failed: {error}"))),
    }
}

/// LiteRT runtime state with auto-wiring (fast, no network).
#[tauri::command]
pub fn glint_ensure_runtime(app: AppHandle) -> Result<serde_json::Value, String> {
    Ok(crate::runtime::ensure_runtime(&app))
}

/// Re-tint the frosted-glass backdrop to match the window theme.
/// `alpha` is the theme background opacity (0..1); Rust maps it onto the
/// acrylic tint band so the glass stays frosted at any opacity.
#[tauri::command]
pub fn glint_set_glass_tint(
    app: AppHandle,
    r: u8,
    g: u8,
    b: u8,
    alpha: f32,
) -> Result<(), String> {
    crate::glass::apply_tint(&app, r, g, b, alpha);
    Ok(())
}

/// Provision the isolated LiteRT venv + pip install (explicit user action;
/// emits `runtime-setup-progress` events, no timeout).
#[tauri::command]
pub async fn glint_setup_runtime(app: AppHandle) -> Result<serde_json::Value, String> {
    crate::runtime::setup_runtime(app).await
}

/// Start continuous scanning (ports StartScanningAsync; owns the 1 s loop).
#[tauri::command]
pub fn glint_start_scanning(app: AppHandle) -> Result<serde_json::Value, String> {
    {
        let runtime: State<ScanRuntime> = app.state();
        if runtime.state.lock().unwrap().scanning {
            return Ok(serde_json::json!({ "scanning": true }));
        }
    }

    // Runtime gate (ports the Missing check).
    let runtime_outcome = crate::bridge::run_sidecar(&app, &["runtime-status"]);
    let runtime_ready = runtime_outcome
        .as_ref()
        .map(|out| runtime_port(&out.json).1)
        .unwrap_or(false);
    if !runtime_ready {
        let missing = runtime_outcome
            .ok()
            .and_then(|out| out.json.get("missing").cloned())
            .and_then(|v| v.as_array().cloned())
            .map(|items| {
                items
                    .iter()
                    .filter_map(|v| v.as_str())
                    .collect::<Vec<_>>()
                    .join(", ")
            })
            .unwrap_or_else(|| "unknown".to_string());
        let summary = format!("Gemma runtime is not ready. Missing: {missing}.");
        return Ok(serde_json::json!({
            "scanning": false,
            "captureSummary": summary,
            "overall": overall_error(summary.clone()),
        }));
    }

    // Borderless pre-request on start (ports StartScanningAsync).
    let borderless_allowed = crate::bridge::run_sidecar(&app, &["request-borderless"])
        .ok()
        .and_then(|out| out.json.get("allowed").and_then(|v| v.as_bool()))
        .unwrap_or(false);
    let capture_summary = if borderless_allowed {
        "Scanning is active without the Windows capture border. Switch to any target window."
            .to_string()
    } else {
        "Scanning is active. Windows may show its capture border; switch to any target window."
            .to_string()
    };

    let generation = {
        let runtime: State<ScanRuntime> = app.state();
        let mut state = runtime.state.lock().unwrap();
        state.scanning = true;
        state.generation = state.generation.wrapping_add(1);
        let generation = state.generation;
        runtime.cancel.send(generation).ok();
        generation
    };
    let task_app = app.clone();
    tauri::async_runtime::spawn(async move {
        scan_loop(task_app, generation).await;
    });
    crate::refresh_tray(&app, true);

    Ok(serde_json::json!({
        "scanning": true,
        "captureSummary": capture_summary,
        "overall": overall_info(
            "Continuous local scanning is active. Return to Glint and select Pause scanning to stop."
                .to_string()
        ),
    }))
}

/// Pause continuous scanning (ports PauseScanningAsync; kills in-flight work).
#[tauri::command]
pub async fn glint_pause_scanning(app: AppHandle) -> Result<serde_json::Value, String> {
    let became_idle = {
        let runtime: State<ScanRuntime> = app.state();
        let mut state = runtime.state.lock().unwrap();
        if !state.scanning {
            false
        } else {
            state.scanning = false;
            state.generation = state.generation.wrapping_add(1);
            let generation = state.generation;
            runtime.cancel.send(generation).ok();
            true
        }
    };
    if !became_idle {
        return Ok(serde_json::json!({ "scanning": false }));
    }
    // The in-flight scan observes the generation bump and kills its own
    // capture/inference work (see run_manual_scan).
    crate::refresh_tray(&app, false);
    Ok(serde_json::json!({
        "scanning": false,
        "captureSummary": "Pausing the active scan...",
    }))
}

/// Current scan-loop state (ports IsScanning for tray/menu enablement).
#[tauri::command]
pub fn glint_scan_state(app: AppHandle) -> Result<serde_json::Value, String> {
    let runtime: State<ScanRuntime> = app.state();
    Ok(serde_json::json!({ "scanning": runtime.state.lock().unwrap().scanning }))
}

/// Single capture of the current window (ports CaptureCurrentWindowAsync).
#[tauri::command]
pub async fn glint_capture_once(app: AppHandle) -> Result<serde_json::Value, String> {
    let ready = crate::bridge::run_sidecar(&app, &["runtime-status"])
        .ok()
        .map(|out| runtime_port(&out.json).1)
        .unwrap_or(false);
    if !ready {
        let missing = "unknown".to_string();
        let summary = format!("Gemma runtime is not ready. Missing: {missing}.");
        return Ok(serde_json::json!({
            "kind": -1,
            "kindName": "NotReady",
            "detail": summary,
            "record": serde_json::Value::Null,
            "suppressReason": serde_json::Value::Null,
            "captureSummary": summary,
            "overall": overall_error(summary),
        }));
    }
    let root = crate::bridge::data_root()?;
    let (cli, args) = manual_scan_args(&app, &root)?;
    let output = tokio::task::spawn_blocking(move || {
        std::process::Command::new(&cli).args(&args).output()
    })
    .await
    .map_err(|error| format!("Capture task failed: {error}"))?
    .map_err(|error| format!("Capture failed to launch: {error}"))?;
    let outcome: serde_json::Value =
        serde_json::from_str(&String::from_utf8_lossy(&output.stdout))
            .map_err(|_| {
                let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
                if stderr.is_empty() {
                    "Capture produced no JSON output.".to_string()
                } else {
                    stderr
                }
            })?;
    // Broadcast so every window (dashboard, command bar invocations)
    // applies the outcome like the scan loop does.
    let payload = outcome_payload(&outcome);
    let _ = app.emit("scan-outcome", &payload);
    Ok(payload)
}

/// Show the dashboard and route the Search page (ports OpenSearchAsync).
#[tauri::command]
pub fn glint_open_search(app: AppHandle, query: Option<String>) -> Result<(), String> {
    crate::show_main(&app);
    let _ = app.emit("open-search", serde_json::json!({ "query": query }));
    Ok(())
}

/// Show the dashboard (ports ShowDashboard).
#[tauri::command]
pub fn glint_show_main(app: AppHandle) -> Result<(), String> {
    crate::show_main(&app);
    Ok(())
}
