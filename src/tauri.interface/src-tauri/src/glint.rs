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

const SUPPRESS_REASONS: [&str; 14] = [
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
    "DesktopBackground",
];

fn kind_name(kind: i64) -> &'static str {
    OUTCOME_KINDS
        .get(kind as usize)
        .copied()
        .unwrap_or("Failed")
}

pub(crate) fn reason_name(reason: i64) -> &'static str {
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

/// Monotonic tick ids so the frontend can match a live "capturing…"
/// card (`scan-tick-started`) with its eventual `scan-outcome`.
static TICK_COUNTER: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

fn outcome_payload(outcome: &serde_json::Value, tick_id: Option<u64>) -> serde_json::Value {
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
            format!("{process}: {label}. Captured; summary lands when scanning stops.")
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
            "The window was captured and saved. Its summary lands in the session when scanning stops."
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
        "tickId": tick_id,
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
    /// Serializes every local-model invocation (scan ticks, single
    /// captures, agent questions) so two model processes never contend
    /// for RAM with overlapping full loads.
    model_lock: tokio::sync::Mutex<()>,
}

impl Default for ScanRuntime {
    fn default() -> Self {
        let (cancel, _) = watch::channel(0);
        Self {
            state: Mutex::new(ScanState::default()),
            cancel,
            model_lock: tokio::sync::Mutex::new(()),
        }
    }
}



fn is_current(app: &AppHandle, generation: u64) -> bool {
    let runtime: State<ScanRuntime> = app.state();
    let state = runtime.state.lock().unwrap();
    state.scanning && state.generation == generation
}

/// Live read for the timeline hook pump: logging runs only inside an
/// initiated recording.
pub(crate) fn scanning_now(app: &AppHandle) -> bool {
    app.state::<ScanRuntime>()
        .state
        .lock()
        .map(|state| state.scanning)
        .unwrap_or(false)
}

/// 30 s dwell heartbeat tied to a scan generation (ports the heartbeat
/// half of the timeline fast lane).
async fn heartbeat_loop(app: AppHandle, generation: u64) {
    loop {
        {
            let runtime: State<ScanRuntime> = app.state::<ScanRuntime>();
            let mut rx = runtime.cancel.subscribe();
            tokio::select! {
                _ = tokio::time::sleep(std::time::Duration::from_secs(30)) => {}
                _ = rx.changed() => {
                    if !is_current(&app, generation) {
                        return;
                    }
                }
            }
        }
        if !is_current(&app, generation) {
            return;
        }
        crate::timeline::record_heartbeat(&app);
    }
}

/// Record the user's verdict on a session. Absolute: no rule overwrites it.
#[tauri::command(async)]
pub fn glint_set_session_outcome(
    app: AppHandle,
    id: String,
    outcome: String,
) -> Result<serde_json::Value, String> {
    let root = crate::bridge::data_root()?;
    let mut args = vec![
        "session-outcome".to_string(),
        "--id".to_string(),
        id,
        "--outcome".to_string(),
        outcome,
    ];
    args.extend(crate::bridge::db_args(&app, &root));
    let arg_refs: Vec<&str> = args.iter().map(|value| value.as_str()).collect();
    Ok(crate::bridge::run_sidecar(&app, &arg_refs)?.json)
}

/// Agent chat threads, most recently active first. Chat text lives only in
/// the encrypted store; these verbs carry user content the same way `ask`
/// does (sidecar argv, never metrics).
#[tauri::command(async)]
pub fn glint_chat_threads(app: AppHandle, limit: Option<u32>) -> Result<serde_json::Value, String> {
    let root = crate::bridge::data_root()?;
    let requested = limit.unwrap_or(50).clamp(1, 200).to_string();
    let mut args = vec!["chat-threads".to_string(), "--limit".to_string(), requested];
    args.extend(crate::bridge::db_args(&app, &root));
    let arg_refs: Vec<&str> = args.iter().map(|value| value.as_str()).collect();
    Ok(crate::bridge::run_sidecar(&app, &arg_refs)?.json)
}

/// One thread with its messages, oldest first.
#[tauri::command(async)]
pub fn glint_chat_thread(
    app: AppHandle,
    id: String,
    limit: Option<u32>,
) -> Result<serde_json::Value, String> {
    let root = crate::bridge::data_root()?;
    let requested = limit.unwrap_or(200).clamp(1, 2000).to_string();
    let mut args = vec![
        "chat-thread".to_string(),
        "--id".to_string(),
        id,
        "--limit".to_string(),
        requested,
    ];
    args.extend(crate::bridge::db_args(&app, &root));
    let arg_refs: Vec<&str> = args.iter().map(|value| value.as_str()).collect();
    Ok(crate::bridge::run_sidecar(&app, &arg_refs)?.json)
}

/// Start a thread. Titles come from the first question (truncated upstream),
/// never model-generated, so opening a chat costs no inference.
#[tauri::command(async)]
pub fn glint_chat_create(
    app: AppHandle,
    title: String,
    scope: Option<String>,
) -> Result<serde_json::Value, String> {
    let root = crate::bridge::data_root()?;
    let mut args = vec!["chat-create".to_string(), "--title".to_string(), title];
    if let Some(scope) = scope {
        args.push("--scope".to_string());
        args.push(scope);
    }
    args.extend(crate::bridge::db_args(&app, &root));
    let arg_refs: Vec<&str> = args.iter().map(|value| value.as_str()).collect();
    Ok(crate::bridge::run_sidecar(&app, &arg_refs)?.json)
}

/// Rename a thread.
#[tauri::command(async)]
pub fn glint_chat_rename(
    app: AppHandle,
    id: String,
    title: String,
) -> Result<serde_json::Value, String> {
    let root = crate::bridge::data_root()?;
    let mut args = vec![
        "chat-rename".to_string(),
        "--id".to_string(),
        id,
        "--title".to_string(),
        title,
    ];
    args.extend(crate::bridge::db_args(&app, &root));
    let arg_refs: Vec<&str> = args.iter().map(|value| value.as_str()).collect();
    Ok(crate::bridge::run_sidecar(&app, &arg_refs)?.json)
}

/// Delete a thread and its messages.
#[tauri::command(async)]
pub fn glint_chat_delete(app: AppHandle, id: String) -> Result<serde_json::Value, String> {
    let root = crate::bridge::data_root()?;
    let mut args = vec!["chat-delete".to_string(), "--id".to_string(), id];
    args.extend(crate::bridge::db_args(&app, &root));
    let arg_refs: Vec<&str> = args.iter().map(|value| value.as_str()).collect();
    Ok(crate::bridge::run_sidecar(&app, &arg_refs)?.json)
}

/// Append one message. Used by `glint_ask` to persist turns, and exposed for
/// completeness; chat text never reaches metrics or logs.
#[tauri::command(async)]
pub fn glint_chat_append(
    app: AppHandle,
    thread: String,
    role: String,
    text: String,
    citations: Option<String>,
    scoped: Option<u32>,
) -> Result<serde_json::Value, String> {
    let root = crate::bridge::data_root()?;
    let mut args = vec![
        "chat-append".to_string(),
        "--thread".to_string(),
        thread,
        "--role".to_string(),
        role,
        "--text".to_string(),
        text,
    ];
    if let Some(citations) = citations {
        args.push("--citations".to_string());
        args.push(citations);
    }
    if let Some(scoped) = scoped {
        args.push("--scoped".to_string());
        args.push(scoped.to_string());
    }
    args.extend(crate::bridge::db_args(&app, &root));
    let arg_refs: Vec<&str> = args.iter().map(|value| value.as_str()).collect();
    Ok(crate::bridge::run_sidecar(&app, &arg_refs)?.json)
}

/// Sessions with their summaries, newest first.
#[tauri::command(async)]
pub fn glint_sessions(app: AppHandle, limit: Option<u32>) -> Result<serde_json::Value, String> {
    let root = crate::bridge::data_root()?;
    let requested = limit.unwrap_or(50).clamp(1, 200).to_string();
    let mut args = vec!["sessions".to_string(), "--limit".to_string(), requested];
    args.extend(crate::bridge::db_args(&app, &root));
    let arg_refs: Vec<&str> = args.iter().map(|value| value.as_str()).collect();
    Ok(crate::bridge::run_sidecar(&app, &arg_refs)?.json)
}

/// Activities, newest first: one per thing the user did, each with its own
/// mode, time, events and checked summary.
#[tauri::command(async)]
pub fn glint_activities(app: AppHandle, limit: Option<u32>) -> Result<serde_json::Value, String> {
    let root = crate::bridge::data_root()?;
    let requested = limit.unwrap_or(300).clamp(1, 1000).to_string();
    let mut args = vec!["activities".to_string(), "--limit".to_string(), requested];
    args.extend(crate::bridge::db_args(&app, &root));
    let arg_refs: Vec<&str> = args.iter().map(|value| value.as_str()).collect();
    Ok(crate::bridge::run_sidecar(&app, &arg_refs)?.json)
}

/// Native page zoom for the dashboard window (WebView2 zoom, like Ctrl +/-
/// in a browser): everything scales, layout reflows. The command bar keeps
/// 100% because its window has a fixed size.
#[tauri::command]
pub fn glint_set_zoom(app: AppHandle, scale: f64) -> Result<f64, String> {
    if !scale.is_finite() {
        return Err("Zoom must be a number.".to_string());
    }
    let scale = scale.clamp(0.5, 2.0);
    let window = app
        .get_webview_window("main")
        .ok_or_else(|| "The main window is not open.".to_string())?;
    window.set_zoom(scale).map_err(|error| error.to_string())?;
    Ok(scale)
}

/// Time per activity inside [from, to): what the dashboard charts. Only
/// app, site, subject, mode, category and the active segments cross over;
/// no summaries or captured text.
#[tauri::command(async)]
pub fn glint_usage(app: AppHandle, from: i64, to: i64) -> Result<serde_json::Value, String> {
    if to <= from {
        return Err("The end of the range must be after its start.".to_string());
    }
    let root = crate::bridge::data_root()?;
    let mut args = vec![
        "usage".to_string(),
        "--from".to_string(),
        from.to_string(),
        "--to".to_string(),
        to.to_string(),
    ];
    args.extend(crate::bridge::db_args(&app, &root));
    let arg_refs: Vec<&str> = args.iter().map(|value| value.as_str()).collect();
    Ok(crate::bridge::run_sidecar(&app, &arg_refs)?.json)
}

/// The user's verdict on an activity's task. Absolute: no rule rewrites it.
#[tauri::command(async)]
pub fn glint_set_activity_task(
    app: AppHandle,
    id: String,
    status: String,
) -> Result<serde_json::Value, String> {
    if !matches!(status.as_str(), "Open" | "LooksDone" | "Done" | "None") {
        return Err(format!("Unknown task status: {status}"));
    }
    let root = crate::bridge::data_root()?;
    let mut args = vec![
        "activity-task".to_string(),
        "--id".to_string(),
        id,
        "--status".to_string(),
        status,
    ];
    args.extend(crate::bridge::db_args(&app, &root));
    let arg_refs: Vec<&str> = args.iter().map(|value| value.as_str()).collect();
    Ok(crate::bridge::run_sidecar(&app, &arg_refs)?.json)
}

/// The user's correction of how an app or site is treated. Rebuilds the
/// activity view so the correction shows on the timeline straight away.
#[tauri::command(async)]
pub async fn glint_set_app_mode(
    app: AppHandle,
    key: String,
    mode: String,
) -> Result<serde_json::Value, String> {
    if !(key.starts_with("app:") || key.starts_with("site:")) {
        return Err("The key must name an app or a site.".to_string());
    }
    if !matches!(mode.as_str(), "Read" | "Make" | "Play" | "Watch" | "Private") {
        return Err(format!("Unknown mode: {mode}"));
    }
    let root = crate::bridge::data_root()?;
    let mut args = vec![
        "app-mode".to_string(),
        "--key".to_string(),
        key,
        "--mode".to_string(),
        mode,
    ];
    args.extend(crate::bridge::db_args(&app, &root));
    let arg_refs: Vec<&str> = args.iter().map(|value| value.as_str()).collect();
    let profile = crate::bridge::run_sidecar_async(&app, &arg_refs).await?.json;
    // Re-segment without waiting for the next stop; descriptions already
    // written are kept, so this costs no model call.
    let scanning = app
        .state::<ScanRuntime>()
        .state
        .lock()
        .map(|state| state.scanning)
        .unwrap_or(false);
    if !scanning {
        if let Some(result) = run_sessionize(&app, false).await {
            let _ = app.emit("sessions-updated", &result);
        }
    }
    Ok(profile)
}

/// Record that the user went away or came back, or that recording started or
/// stopped. The sessionizer reads these to tell a real break from a screen
/// that simply did not change. Best effort: a lost marker only costs the
/// fallback gap rule, so it never blocks the loop.
fn record_marker(app: &AppHandle, kind: &'static str) {
    let Ok(root) = crate::bridge::data_root() else {
        return;
    };
    let mut args = vec!["mark".to_string(), "--kind".to_string(), kind.to_string()];
    args.extend(crate::bridge::db_args(app, &root));
    let arg_refs: Vec<&str> = args.iter().map(|value| value.as_str()).collect();
    let _ = crate::bridge::run_sidecar(app, &arg_refs);
}

/// Group captures into sessions and summarize the finished ones. Runs under
/// the model lock because it makes model calls, so it never overlaps a scan.
/// Called when a scan stops (`seal_open`), never mid-scan: captures stay
/// model-free while recording so inference cannot contend with them.
async fn run_sessionize(app: &AppHandle, seal_open: bool) -> Option<serde_json::Value> {
    let root = crate::bridge::data_root().ok()?;
    let cli = crate::bridge::sidecar_path(app).ok()?;
    let mut args = vec!["sessionize".to_string()];
    if !seal_open {
        // A rebuild after a correction: segment only, no model calls.
        args.push("--max-summaries".to_string());
        args.push("0".to_string());
    }
    args.extend(crate::bridge::db_args(app, &root));
    args.extend(crate::bridge::host_args());
    if seal_open {
        args.push("--seal-open".to_string());
    }

    let model_state: State<ScanRuntime> = app.state();
    let _model = model_state.model_lock.lock().await;
    let started = std::time::Instant::now();
    let output = tokio::task::spawn_blocking(move || {
        crate::bridge::hidden_command(&cli).args(&args).output()
    })
    .await
    .ok()?
    .ok()?;
    crate::bridge::record_spawn(
        "sessionize",
        started.elapsed().as_millis(),
        output.status.code().unwrap_or(-1),
        None,
    );
    serde_json::from_str::<serde_json::Value>(&String::from_utf8_lossy(&output.stdout)).ok()
}

fn manual_scan_args(app: &AppHandle, root: &std::path::Path) -> Result<(PathBuf, Vec<String>), String> {
    let cli = crate::bridge::sidecar_path(app)?;
    let mut args = vec!["manual-scan".to_string()];
    args.extend(crate::bridge::db_args(app, root));
    args.extend(crate::bridge::host_args());
    Ok((cli, args))
}

async fn run_manual_scan(
    app: &AppHandle,
    generation: u64,
) -> Option<serde_json::Value> {
    let root = crate::bridge::data_root().ok()?;
    let (cli, args) = manual_scan_args(app, &root).ok()?;

    // Serialize model loads: no two inference processes at once.
    let model_state: State<ScanRuntime> = app.state();
    let _model = model_state.model_lock.lock().await;
    let started = std::time::Instant::now();
    let child = tokio::process::Command::new(&cli)
        .args(&args)
        .creation_flags(crate::bridge::CREATE_NO_WINDOW)
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
    let parsed =
        serde_json::from_str::<serde_json::Value>(&String::from_utf8_lossy(&output.stdout)).ok();
    crate::bridge::record_spawn(
        "manual-scan",
        started.elapsed().as_millis(),
        output.status.code().unwrap_or(-1),
        parsed
            .as_ref()
            .and_then(|v| v.get("workerStarts"))
            .and_then(|v| v.as_i64()),
    );
    parsed
}

async fn scan_loop(app: AppHandle, generation: u64) {
    use crate::cadence::{self, CaptureDecision};
    use std::time::Instant;

    // Capture is signal-driven rather than a fixed tick (see cadence.rs):
    // react to window switches, keep up while typing, tick slowly while
    // reading, and stop while the user is away.
    let mut last_scan: Option<Instant> = None;
    let mut last_foreground = cadence::foreground_handle();
    let mut foreground_changed_at: Option<Instant> = None;
    // In-app navigation inside one OS window (Discord server hop, browser
    // tab): the HWND never changes, so the foreground hook stays silent. A
    // settled title change is treated like a switch — one timeline row plus
    // one capture — once it has outlasted the switch debounce.
    let mut last_title: Option<String> = None;
    let mut pending_title: Option<(String, Instant)> = None;
    let mut away = false;
    // A game or video whose screen was moving at the last look keeps the
    // user "present" without input, until a look says otherwise.
    let mut watching_until: Option<Instant> = None;
    // The last look was a game or a video.
    let mut visual_foreground = false;

    loop {
        if !is_current(&app, generation) {
            break;
        }

        let foreground = cadence::foreground_handle();
        if foreground != last_foreground {
            last_foreground = foreground;
            foreground_changed_at = Some(Instant::now());
            // Baseline re-read silently on the next pass: the OS hook already
            // logged this window, so there is nothing to report yet.
            last_title = None;
            pending_title = None;
        } else {
            // Same window: watch the title for settled in-app navigation.
            // Empty reads carry no information and are ignored, so a
            // transient failed read can never fake a hop; ticking titles
            // (progress %, "typing…") never settle and produce nothing.
            let title = cadence::foreground_title();
            if title.is_empty() {
                // No information; keep the previous state untouched.
            } else if last_title.as_deref() == Some(title.as_str()) {
                pending_title = None;
            } else if last_title.is_none() {
                last_title = Some(title);
            } else {
                let now = Instant::now();
                let since = match &pending_title {
                    Some((pending, at)) if *pending == title => *at,
                    _ => {
                        pending_title = Some((title.clone(), now));
                        now
                    }
                };
                if since.elapsed().as_millis() as u64 >= cadence::SWITCH_DEBOUNCE_MS {
                    // Settled: log the hop and capture it like a switch,
                    // backdating to when the title first appeared so the
                    // capture fires on the next decision instead of waiting
                    // out another debounce.
                    last_title = Some(title);
                    pending_title = None;
                    foreground_changed_at = Some(since);
                    let marker_app = app.clone();
                    let _ = tokio::task::spawn_blocking(move || {
                        crate::timeline::record_retitle(&marker_app, foreground)
                    })
                    .await;
                    if !is_current(&app, generation) {
                        break;
                    }
                }
            }
        }

        let decision = cadence::decide(cadence::CaptureSignals {
            idle_ms: cadence::input_idle_ms(),
            since_last_scan_ms: last_scan.map(|at| at.elapsed().as_millis() as u64),
            since_foreground_change_ms: foreground_changed_at
                .map(|at| at.elapsed().as_millis() as u64),
            on_battery: cadence::on_battery(),
            screen_active: watching_until.is_some_and(|until| Instant::now() < until),
            visual_foreground,
        });

        let wait_ms = match decision {
            CaptureDecision::Idle => {
                if !away {
                    away = true;
                    crate::timeline::record_lifecycle(&app, "user.away");
                    let marker_app = app.clone();
                    let _ = tokio::task::spawn_blocking(move || {
                        record_marker(&marker_app, "user.away")
                    })
                    .await;
                }
                cadence::POLL_INTERVAL_MS
            }
            CaptureDecision::Wait(ms) => ms,
            CaptureDecision::Capture(reason) => {
                if away {
                    away = false;
                    crate::timeline::record_lifecycle(&app, "user.returned");
                    let marker_app = app.clone();
                    let _ = tokio::task::spawn_blocking(move || {
                        record_marker(&marker_app, "user.returned")
                    })
                    .await;
                }
                foreground_changed_at = None;

                // Live tick announcement first: the frontend logs that a scan
                // started immediately instead of waiting out capture +
                // inference. Deliberately no window lookup here — the only
                // window reads are the ones inside the scan below;
                // process/title fill in when its outcome arrives.
                let tick_id = TICK_COUNTER.fetch_add(1, std::sync::atomic::Ordering::SeqCst) + 1;
                let started_at_ms = chrono::Utc::now().timestamp_millis();
                let _ = app.emit(
                    "scan-tick-started",
                    serde_json::json!({
                        "tickId": tick_id,
                        "startedAtMs": started_at_ms,
                        "reason": reason.as_str(),
                    }),
                );
                match run_manual_scan(&app, generation).await {
                    None => break, // paused or superseded
                    Some(outcome) => {
                        let mode = outcome.get("mode").and_then(|v| v.as_str()).unwrap_or("");
                        let moving = outcome
                            .get("screenMoving")
                            .and_then(|v| v.as_bool())
                            .unwrap_or(false);
                        visual_foreground = matches!(mode, "Play" | "Watch");
                        watching_until = if matches!(mode, "Play" | "Watch") && moving {
                            Some(
                                Instant::now()
                                    + std::time::Duration::from_millis(cadence::PASSIVE_INTERVAL_MS * 2),
                            )
                        } else {
                            None
                        };
                        let payload = outcome_payload(&outcome, Some(tick_id));
                        let _ = app.emit("scan-outcome", &payload);
                    }
                }

                // Time the next interval from the end of this scan, so a slow
                // scan does not immediately qualify for the next one.
                last_scan = Some(Instant::now());
                continue;
            }
        };

        // Deliberately no sessionizing here: captures stay model-free while
        // a scan is active, so inference never contends with capture for
        // CPU/RAM mid-run. Grouping and summarizing happen once, when the
        // scan stops (see the finalize block below).

        // Abortable wait, so a pause is noticed within one poll interval.
        {
            let runtime: State<ScanRuntime> = app.state::<ScanRuntime>();
            let mut rx = runtime.cancel.subscribe();
            tokio::select! {
                _ = tokio::time::sleep(std::time::Duration::from_millis(wait_ms)) => {}
                _ = rx.changed() => {
                    if !is_current(&app, generation) {
                        break;
                    }
                }
            }
        }
    }

    // Loop exit owns the final stopped state. Pause bumps the generation so
    // in-flight work dies promptly, which means a paused loop is exactly one
    // generation behind with scanning off — without the second clause below
    // the seal-and-summarize-on-stop path would never run. A superseded
    // generation (newer Start, scanning back on) still skips: the new loop
    // owns the flow and its own stop will finalize.
    let should_finalize = {
        let runtime: State<ScanRuntime> = app.state::<ScanRuntime>();
        let mut state = runtime.state.lock().unwrap();
        let paused =
            state.generation == generation.wrapping_add(1) && !state.scanning;
        if state.generation == generation || paused {
            state.scanning = false;
            true
        } else {
            false
        }
    };
    if should_finalize {
        // Recorded before sealing, so the final session sees the stop as the
        // boundary it is.
        let marker_app = app.clone();
        let _ = tokio::task::spawn_blocking(move || {
            record_marker(&marker_app, "run.stopped")
        })
        .await;

        // Nothing is being captured any more, so seal and summarize
        // everything now: one stop must drain the whole backlog, because
        // nothing sessionizes mid-scan any more. Rounds that summarize
        // nothing make no progress (failures stay for a later stop), which
        // bounds the loop; each round emits so the views fill in live.
        for _ in 0..12 {
            match run_sessionize(&app, true).await {
                Some(result) => {
                    let progressed = result
                        .get("summarized")
                        .and_then(|v| v.as_u64())
                        .unwrap_or(0)
                        > 0;
                    let _ = app.emit("sessions-updated", &result);
                    if !progressed {
                        break;
                    }
                }
                None => break,
            }
        }
        let _ = app.emit(
            "scan-state",
            serde_json::json!({
                "scanning": false,
                "captureSummary": "Scanning stopped.",
                "overall": overall_info("Continuous local scanning is stopped.".to_string()),
            }),
        );
        crate::timeline::record_lifecycle(&app, "scan.stopped");
        crate::timeline::eon_ended(&app);
        crate::refresh_tray(&app, false);
    }
}

// ---------------------------------------------------------------------------
// Tauri commands
// ---------------------------------------------------------------------------

/// Full dashboard bootstrap (ports InitializeAsync + LoadScanHistory).
#[tauri::command(async)]
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
#[tauri::command(async)]
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
#[tauri::command(async)]
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
#[tauri::command(async)]
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
#[tauri::command(async)]
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
#[tauri::command(async)]
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
#[tauri::command(async)]
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
#[tauri::command(async)]
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
#[tauri::command(async)]
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
    blur: Option<bool>,
) -> Result<(), String> {
    crate::glass::apply_tint(&app, r, g, b, alpha, blur.unwrap_or(true));
    Ok(())
}

/// Provision the isolated LiteRT venv + pip install (explicit user action;
/// emits `runtime-setup-progress` events, no timeout).
#[tauri::command]
pub async fn glint_setup_runtime(app: AppHandle) -> Result<serde_json::Value, String> {
    crate::runtime::setup_runtime(app).await
}

/// Start continuous scanning (ports StartScanningAsync; owns the 1 s loop).
#[tauri::command(async)]
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
    let heartbeat_app = app.clone();
    tauri::async_runtime::spawn(async move {
        heartbeat_loop(heartbeat_app, generation).await;
    });
    // EON + lifecycle markers: the run and its recording state are now
    // first-class timeline rows, not just banner text.
    crate::timeline::eon_started(&app);
    crate::timeline::record_lifecycle(&app, "scan.started");
    record_marker(&app, "run.started");
    crate::refresh_tray(&app, true);

    Ok(serde_json::json!({
        "scanning": true,
        "captureSummary": capture_summary,
        "overall": overall_info(
            "Continuous local scanning is active. Return to Glint and select Stop scanning to stop."
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
        "captureSummary": "Stopping…",
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
    let ready = crate::bridge::run_sidecar_async(&app, &["runtime-status"])
        .await
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
    // Serialize model loads: no two inference processes at once.
    let model_state: State<ScanRuntime> = app.state();
    let _model = model_state.model_lock.lock().await;
    let started = std::time::Instant::now();
    let output = tokio::task::spawn_blocking(move || {
        crate::bridge::hidden_command(&cli).args(&args).output()
    })
    .await
    .map_err(|error| format!("Capture task failed: {error}"))?
    .map_err(|error| format!("Capture failed to launch: {error}"))?;
    crate::bridge::record_spawn(
        "manual-scan",
        started.elapsed().as_millis(),
        output.status.code().unwrap_or(-1),
        None,
    );
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
    let payload = outcome_payload(&outcome, None);
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

/// Ask: the user's question goes to the long-lived answer process
/// (`ask-serve`), which reads their activities, works out the period and the
/// kind of activity, writes summaries from the facts, uses the local model
/// for everything else, and checks the model's answer against the log.
///
/// The process keeps the model loaded between questions, so only the first
/// one pays for loading it. Questions and history travel over its stdin,
/// never on a command line. `thread_id` threads the conversation: recent
/// turns go with the question, and the question and answer are stored.
#[tauri::command]
pub async fn glint_ask(
    app: AppHandle,
    question: String,
    scope: String,
    day: Option<String>,
    thread_id: Option<String>,
) -> Result<serde_json::Value, String> {
    let question = question.trim().to_string();
    if question.is_empty() {
        return Err("Type a question or a message.".to_string());
    }
    let thread_id = thread_id
        .as_deref()
        .map(str::trim)
        .filter(|id| !id.is_empty())
        .map(str::to_string);

    // Previous turns first, then the question is stored, so a crash during
    // the answer still leaves a truthful thread.
    let history = match &thread_id {
        Some(id) => load_thread_turns(&app, id).await,
        None => Vec::new(),
    };
    if let Some(id) = &thread_id {
        append_chat(&app, id, "user", &question, None, None).await;
    }

    let request = serde_json::json!({
        "question": question,
        "scope": scope,
        "day": day.filter(|d| !d.trim().is_empty()),
        "history": history,
    });

    // One model at a time: the answer process shares the machine with the
    // sessionizer's summaries.
    let model_state: State<ScanRuntime> = app.state();
    let _model = model_state.model_lock.lock().await;
    let reply = ask_server_request(&app, &request).await;
    drop(_model);

    match reply {
        Ok(answer) => {
            if let Some(id) = &thread_id {
                let text = answer.get("answer").and_then(|v| v.as_str()).unwrap_or("");
                let citations = answer
                    .get("citations")
                    .map(|v| v.to_string())
                    .unwrap_or_else(|| "[]".to_string());
                let scoped = answer
                    .get("scopedCount")
                    .and_then(|v| v.as_u64())
                    .map(|v| v as usize);
                append_chat(&app, id, "agent", text, Some(citations), scoped).await;
            }
            Ok(answer)
        }
        Err(error) => {
            if let Some(id) = &thread_id {
                append_chat(&app, id, "error", &error, None, None).await;
            }
            Err(error)
        }
    }
}

/// The running answer process, if any.
#[derive(Default)]
pub struct AskServer {
    process: tokio::sync::Mutex<Option<AskProcess>>,
}

struct AskProcess {
    child: tokio::process::Child,
    stdin: tokio::process::ChildStdin,
    stdout: tokio::io::Lines<tokio::io::BufReader<tokio::process::ChildStdout>>,
}

fn spawn_ask_server(app: &AppHandle) -> Result<AskProcess, String> {
    use tokio::io::AsyncBufReadExt;
    let root = crate::bridge::data_root()?;
    let cli = crate::bridge::sidecar_path(app)?;
    let mut args = vec!["ask-serve".to_string()];
    args.extend(crate::bridge::db_args(app, &root));
    let mut child = tokio::process::Command::new(&cli)
        .args(&args)
        .creation_flags(crate::bridge::CREATE_NO_WINDOW)
        .stdin(std::process::Stdio::piped())
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::null())
        .kill_on_drop(true)
        .spawn()
        .map_err(|error| format!("Could not start the answer process: {error}"))?;
    let stdin = child
        .stdin
        .take()
        .ok_or_else(|| "The answer process has no input.".to_string())?;
    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| "The answer process has no output.".to_string())?;
    Ok(AskProcess {
        child,
        stdin,
        stdout: tokio::io::BufReader::new(stdout).lines(),
    })
}

/// Sends one request and waits for its answer. A process that has exited
/// (idle shutdown, crash) is replaced once, transparently.
async fn ask_server_request(
    app: &AppHandle,
    request: &serde_json::Value,
) -> Result<serde_json::Value, String> {
    use tokio::io::AsyncWriteExt;
    let server: State<AskServer> = app.state();
    let mut guard = server.process.lock().await;
    let line = format!("{request}\n");
    for _ in 0..2 {
        if guard.is_none() {
            *guard = Some(spawn_ask_server(app)?);
        }
        let process = guard.as_mut().expect("just set");
        let sent = async {
            process.stdin.write_all(line.as_bytes()).await?;
            process.stdin.flush().await
        }
        .await;
        if sent.is_ok() {
            match tokio::time::timeout(
                std::time::Duration::from_secs(300),
                process.stdout.next_line(),
            )
            .await
            {
                Ok(Ok(Some(reply))) => return parse_ask_reply(&reply),
                Ok(_) => {}
                Err(_) => {
                    if let Some(mut stuck) = guard.take() {
                        let _ = stuck.child.kill().await;
                    }
                    return Err("The local model took too long to answer. Try again.".to_string());
                }
            }
        }
        // The process is gone: clear it and start a fresh one.
        if let Some(mut dead) = guard.take() {
            let _ = dead.child.kill().await;
        }
    }
    Err("The answer process stopped unexpectedly. Try again.".to_string())
}

/// One line from the answer process: an answer, or an error to show.
fn parse_ask_reply(line: &str) -> Result<serde_json::Value, String> {
    let value: serde_json::Value = serde_json::from_str(line)
        .map_err(|_| "The answer process returned something unreadable.".to_string())?;
    match value.get("error").and_then(|v| v.as_str()) {
        Some(error) => Err(error.to_string()),
        None => Ok(value),
    }
}

/// The last turns of a thread, oldest first, as the answer process takes
/// them. Best effort: any failure means no history.
async fn load_thread_turns(app: &AppHandle, thread_id: &str) -> Vec<serde_json::Value> {
    let Ok(root) = crate::bridge::data_root() else {
        return Vec::new();
    };
    let mut args = vec![
        "chat-thread".to_string(),
        "--id".to_string(),
        thread_id.to_string(),
        "--limit".to_string(),
        "200".to_string(),
    ];
    args.extend(crate::bridge::db_args(app, &root));
    let refs: Vec<&str> = args.iter().map(|s| s.as_str()).collect();
    let messages = crate::bridge::run_sidecar_async(app, &refs)
        .await
        .ok()
        .and_then(|out| out.json.get("messages").cloned())
        .and_then(|v| v.as_array().cloned())
        .unwrap_or_default();
    recent_turns(&messages, 8)
}

/// User and agent turns only, newest `keep`, oldest first. Pure for tests.
fn recent_turns(messages: &[serde_json::Value], keep: usize) -> Vec<serde_json::Value> {
    let turns: Vec<serde_json::Value> = messages
        .iter()
        .filter_map(|message| {
            let role = message.get("role").and_then(|v| v.as_str())?;
            let text = message.get("text").and_then(|v| v.as_str())?.trim();
            (matches!(role, "user" | "agent") && !text.is_empty())
                .then(|| serde_json::json!({ "role": role, "text": text }))
        })
        .collect();
    let skip = turns.len().saturating_sub(keep);
    turns.into_iter().skip(skip).collect()
}

/// Best-effort chat persistence: failures are swallowed so a store hiccup
/// can never fail an answer.
async fn append_chat(
    app: &AppHandle,
    thread_id: &str,
    role: &str,
    text: &str,
    citations_json: Option<String>,
    scoped: Option<usize>,
) {
    let root = match crate::bridge::data_root() {
        Ok(root) => root,
        Err(_) => return,
    };
    let mut args = vec![
        "chat-append".to_string(),
        "--thread".to_string(),
        thread_id.to_string(),
        "--role".to_string(),
        role.to_string(),
        "--text".to_string(),
        text.to_string(),
    ];
    if let Some(citations) = citations_json {
        args.push("--citations".to_string());
        args.push(citations);
    }
    if let Some(scoped) = scoped {
        args.push("--scoped".to_string());
        args.push(scoped.to_string());
    }
    args.extend(crate::bridge::db_args(app, &root));
    let refs: Vec<&str> = args.iter().map(|s| s.as_str()).collect();
    let _ = crate::bridge::run_sidecar_async(app, &refs).await;
}

/// Global shortcut registration state (never silent on conflict: the
/// Diagnostics page lists any combo the OS refused).
#[tauri::command]
pub fn glint_shortcut_status(app: AppHandle) -> Result<serde_json::Value, String> {
    use tauri_plugin_global_shortcut::GlobalShortcutExt;
    let shortcuts = app.global_shortcut();
    let items = [
        ("ctrl+alt+g", "Open command bar"),
        ("ctrl+alt+s", "Start scanning"),
        ("ctrl+alt+p", "Stop scanning"),
    ]
    .into_iter()
    .map(|(shortcut, action)| {
        let registered = shortcuts.is_registered(shortcut);
        serde_json::json!({
            "shortcut": shortcut,
            "action": action,
            "registered": registered,
        })
    })
    .collect::<Vec<_>>();
    Ok(serde_json::Value::Array(items))
}

/// Timeline fast-lane read: sealed day file for `date` (YYYY-MM-DD,
/// default today) → hook-exact + heartbeat rows, oldest first.
#[tauri::command(async)]
pub fn glint_timeline(app: AppHandle, date: Option<String>) -> Result<serde_json::Value, String> {
    let day = date
        .filter(|d| !d.trim().is_empty())
        .unwrap_or_else(crate::timeline::local_day);
    let events = crate::timeline::load_day(&app, &day)?;
    Ok(serde_json::json!({
        "date": day,
        "events": events,
        "eons": crate::timeline::load_eons(&app),
        "droppedHooks": crate::timeline::dropped_count(),
    }))
}


#[cfg(test)]
mod tests {
    use super::{parse_ask_reply, recent_turns};

    fn message(role: &str, text: &str) -> serde_json::Value {
        serde_json::json!({ "role": role, "text": text })
    }

    #[test]
    fn history_keeps_the_newest_user_and_agent_turns_in_order() {
        let messages = vec![
            message("user", "one"),
            message("agent", "two"),
            message("error", "worker was busy"),
            message("user", "  "),
            message("user", "three"),
            message("agent", "four"),
        ];
        let turns = recent_turns(&messages, 3);
        let texts: Vec<&str> = turns.iter().map(|t| t["text"].as_str().unwrap()).collect();
        assert_eq!(texts, ["two", "three", "four"]);
        assert!(recent_turns(&[], 8).is_empty());
    }

    #[test]
    fn answer_process_errors_reach_the_user() {
        assert_eq!(
            parse_ask_reply(r#"{"error":"InvalidDataException: Empty request."}"#),
            Err("InvalidDataException: Empty request.".to_string())
        );
        let ok = parse_ask_reply(r#"{"answer":"You watched Episode 3 [1].","citations":[]}"#).unwrap();
        assert_eq!(ok["answer"], "You watched Episode 3 [1].");
        assert!(parse_ask_reply("not json").is_err());
    }
}
