//! Glint Phase 0 frontend bridge.
//!
//! Ports `Glint.Phase0.App/MainViewModel.cs` (which called
//! `Glint.Phase0.Core` directly) onto Tauri commands backed by the
//! `Glint.Phase0.Cli` sidecar — see `bridge.rs` for the transport.
//! All user-facing strings mirror the WinUI view model verbatim.

use chrono::TimeZone;
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

/// Append a failure fingerprint for post-mortem diagnosis
/// (data_root/ask-failures.log): prompt hash + sizes + worker stderr head.
/// Never includes prompt text or captured content.
fn log_ask_failure(question: &str, prompt_chars: usize, scoped_count: usize, detail: &str) {
    let line = serde_json::json!({
        "ts": chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Nanos, true),
        "question_chars": question.chars().count(),
        "prompt_chars": prompt_chars,
        "scoped_count": scoped_count,
        "detail": detail.chars().take(500).collect::<String>(),
    });
    if let Ok(root) = crate::bridge::data_root() {
        let path = root.join("ask-failures.log");
        use std::io::Write;
        if let Ok(mut file) = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(&path)
        {
            let _ = writeln!(
                file,
                "{}",
                serde_json::to_string(&line).unwrap_or_default()
            );
        }
    }
}

/// Wording for the selected scope, so a week or all-history answer is not
/// narrated as "today".
fn scope_phrases(scope: &str) -> (&'static str, &'static str) {
    match scope {
        "week" => ("this week", "Here's what you did this week~!"),
        "all" => ("across your history", "Here's what you've been up to~!"),
        _ => ("today", "Here's what you did today~!"),
    }
}

/// Whether a second worker could plausibly succeed. Deterministic failures
/// fail the same way twice, and each retry costs a process spawn plus a full
/// ~2.5 GB model load, so they are not worth repeating.
fn is_retryable(stderr: &str) -> bool {
    let raw = stderr.to_ascii_lowercase();
    let deterministic = [
        "prompt too large",
        "prompt cannot be empty",
        "runtime is not ready",
    ];
    !deterministic.iter().any(|needle| raw.contains(needle))
}

/// Translate raw worker/sidecar stderr into something actionable while
/// keeping the technical detail for diagnosis (capped in length).
fn friendly_model_error(stderr: &str) -> String {
    let raw = stderr.trim();
    if raw.is_empty() {
        return "The local model produced no output.".to_string();
    }
    let transient = raw.contains("litert_lm_conversation_send_message failed")
        || raw.contains("LiteRT-LM worker exited")
        || raw.contains("did not deliver")
        || raw.contains("timed out")
        || raw.contains("Timeout");
    let mut detail: String = raw.chars().take(500).collect();
    if detail.len() < raw.len() {
        detail.push('…');
    }
    if transient {
        format!(
            "The local model stumbled (worker error, usually transient — asked again automatically). If this persists, stop scanning and retry. Technical detail: {detail}"
        )
    } else {
        detail
    }
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

/// Ask the local agent over captured history (Agent tab).
/// scope: "day" (needs `day` YYYY-MM-DD, default today), "week", "all".
/// Built purely from existing verbs: runtime-status, manual-history,
/// model-generate. Citations are the scans actually placed in context —
/// never model-invented references.
///
/// `thread_id` threads the conversation: previous turns travel with the ask
/// (so follow-ups and corrections land on the same evidence), and this ask's
/// question + answer are appended to the thread. Follows-ups sample instead
/// of running deterministically, or a correction could never change the
/// answer. Thread failures are non-fatal: the ask proceeds unthreaded.
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
        return Err("Ask a question about your captured context.".to_string());
    }
    let thread_id = thread_id
        .as_deref()
        .map(str::trim)
        .filter(|id| !id.is_empty())
        .map(str::to_string);

    // 1. Resolve the LiteRT runtime (paths for model-generate).
    let resolution = crate::bridge::run_sidecar_async(&app, &["runtime-status"])
        .await
        .map_err(|e| format!("Gemma runtime check failed: {e}"))?
        .json;
    let (ready_summary, ready) = runtime_port(&resolution);
    if !ready {
        return Err(ready_summary);
    }
    let python = resolution
        .get("pythonExecutable")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_string();
    let worker = resolution
        .get("workerScript")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_string();
    let model = resolution
        .get("modelPath")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .to_string();

    // 1b. Thread history (best effort) + persist the question. History holds
    // previous turns only; the current question is appended first so a crash
    // mid-generation still leaves a truthful thread.
    let history_text = match &thread_id {
        Some(id) => load_thread_history(&app, id).await,
        None => String::new(),
    };
    if let Some(id) = &thread_id {
        append_chat(&app, id, "user", &question, None, None).await;
    }

    // 2. Pull scan records and filter by scope.
    let root = crate::bridge::data_root()?;
    let db_args = crate::bridge::db_args(&app, &root);
    let mut args = vec!["manual-history", "--limit", "500"];
    args.extend(db_args.iter().map(|s| s.as_str()));
    let scans = crate::bridge::run_sidecar_async(&app, &args)
        .await
        .map(|out| {
            out.json
                .get("scans")
                .cloned()
                .unwrap_or(serde_json::Value::Array(vec![]))
        })
        .unwrap_or(serde_json::Value::Array(vec![]));
    let all_scans: Vec<&serde_json::Value> = scans.as_array().map(|a| a.iter().collect()).unwrap_or_default();

    let day_key = day
        .filter(|d| !d.trim().is_empty())
        .unwrap_or_else(|| chrono::Local::now().format("%Y-%m-%d").to_string());
    let local_day_of = |ms: i64| -> String {
        chrono::DateTime::from_timestamp_millis(ms)
            .map(|utc| {
                chrono::Local
                    .from_utc_datetime(&utc.naive_utc())
                    .format("%Y-%m-%d")
                    .to_string()
            })
            .unwrap_or_default()
    };
    let in_scope = |scan: &serde_json::Value| -> bool {
        let ms = scan
            .get("capturedAtMilliseconds")
            .and_then(|v| v.as_i64())
            .unwrap_or(0);
        match scope.as_str() {
            "week" => {
                let today = chrono::Local::now().date_naive();
                chrono::NaiveDate::parse_from_str(&local_day_of(ms), "%Y-%m-%d")
                    .map(|d| (0..=6).contains(&(today - d).num_days()))
                    .unwrap_or(false)
            }
            "all" => true,
            _ => local_day_of(ms) == day_key,
        }
    };
    let scoped: Vec<&serde_json::Value> =
        all_scans.iter().filter(|s| in_scope(s)).copied().collect();
    if scoped.is_empty() {
        return Ok(serde_json::json!({
            "answer": "I couldn't find enough evidence in the selected scope. Try a wider scope or different wording.",
            "citations": [],
            "scopedCount": 0,
            "totalCount": all_scans.len(),
        }));
    }

    // 3. Build a budgeted context from SESSION blocks (not raw scans):
    // consecutive same-session scans merge into one entry carrying its
    // span + duration, so the model narrates activities ("played X for
    // 5 mins") instead of data-dumping scans. Hard cap ~3.5k chars: the
    // worker rejects inputs past ~1.9k tokens within milliseconds, and
    // token-dense content needs the margin. Deterministic pass > retry.
    fn clock(ms: i64) -> String {
        chrono::DateTime::from_timestamp_millis(ms)
            .map(|utc| {
                chrono::Local
                    .from_utc_datetime(&utc.naive_utc())
                    .format("%-I:%M %p")
                    .to_string()
            })
            .unwrap_or_default()
    }
    fn duration(ms: i64) -> String {
        if ms < 60_000 {
            "under a minute".to_string()
        } else if ms < 3_600_000 {
            format!("about {} mins", ms / 60_000)
        } else {
            format!(
                "about {} hrs {} mins",
                ms / 3_600_000,
                (ms % 3_600_000) / 60_000
            )
        }
    }
    const CONTEXT_BUDGET: usize = 3_500;
    // History shares the worker's input budget with the session evidence:
    // shrink the session side by whatever history takes, so the total prompt
    // stays within the token limit history or not.
    let session_budget = CONTEXT_BUDGET.saturating_sub(history_text.len());
    let mut context = String::new();
    let mut cited = Vec::new();
    let mut index = 0usize;
    // Sessions are time-contiguous, so same-session scans are always
    // adjacent: extend each block while the session id holds.
    let mut i = 0;
    while i < scoped.len() {
        let mut j = i + 1;
        while j < scoped.len() {
            let a = scoped[i].get("sessionId").and_then(|v| v.as_str());
            let b = scoped[j].get("sessionId").and_then(|v| v.as_str());
            match (a, b) {
                (Some(x), Some(y)) if x == y => j += 1,
                _ => break,
            }
        }
        let block = &scoped[i..j];
        i = j;
        let times: Vec<i64> = block
            .iter()
            .map(|s| {
                s.get("capturedAtMilliseconds")
                    .and_then(|v| v.as_i64())
                    .unwrap_or(0)
            })
            .collect();
        let start = times.iter().copied().min().unwrap_or(0);
        let end = times.iter().copied().max().unwrap_or(0);
        let head = block[0];
        let process = head
            .get("processName")
            .and_then(|v| v.as_str())
            .unwrap_or("?");
        let label = block
            .iter()
            .filter_map(|s| s.get("label").and_then(|v| v.as_str()))
            .find(|l| !l.trim().is_empty())
            .unwrap_or("Untitled activity");
        let mut notes: Vec<&str> = Vec::new();
        for scan in block {
            if let Some(summary) = scan.get("summary").and_then(|v| v.as_str()) {
                let trimmed = summary.trim();
                if !trimmed.is_empty() && !notes.contains(&trimmed) {
                    notes.push(trimmed);
                }
            }
            if notes.len() >= 2 {
                break;
            }
        }
        let mut key = String::new();
        for scan in block {
            for field in ["importantSignals", "reminderCandidate"] {
                if let Some(text) = scan.get(field).and_then(|v| v.as_str()) {
                    let trimmed = text.trim();
                    if !trimmed.is_empty() && !key.contains(trimmed) {
                        if !key.is_empty() {
                            key.push_str("; ");
                        }
                        key.push_str(trimmed);
                    }
                }
            }
            if key.len() > 300 {
                break;
            }
        }
        let entry = format!(
            "[{index}] {label} ({process}) — {dur} ({range})\nWhat happened: {notes}\nKey: {key}\n",
            dur = duration(end - start),
            range = format!("{}–{}", clock(start), clock(end)),
            notes = if notes.is_empty() {
                "no summary recorded".to_string()
            } else {
                notes.join(" / ")
            },
            key = if key.is_empty() { "-".to_string() } else { key },
        );
        if context.len() + entry.len() > session_budget {
            break;
        }
        context.push_str(&entry);
        for scan in block {
            if cited.len() >= 12 {
                break;
            }
            cited.push(serde_json::json!({
                "id": scan.get("id"),
                "label": scan.get("label").and_then(|v| v.as_str()).unwrap_or("Untitled"),
                "capturedAtMilliseconds": scan.get("capturedAtMilliseconds").and_then(|v| v.as_i64()).unwrap_or(0),
                "processName": scan.get("processName").and_then(|v| v.as_str()).unwrap_or("?"),
            }));
        }
        index += 1;
    }

    let (period, opener) = scope_phrases(&scope);
    let history_section = if history_text.is_empty() {
        String::new()
    } else {
        format!(
            "\n\nConversation so far (most recent last):\n{history_text}\n\n\
             The user may be correcting or disputing the previous answer. If so, \
             re-derive the disputed claims from the sessions below, state what \
             changed and why, and never restate a disputed claim without citing \
             the evidence for it."
        )
    };
    let prompt = build_ask_prompt(&period, &opener, &question, &context, &history_section);

    // 4. Generate via the existing model-generate verb (5 min cap inside).
    // Serialized with scan ticks (one model load at a time). Transient
    // native worker failures (RAM contention from an overlapping load, AV
    // holds, cold-start races) are retried once; deterministic ones are not.
    let model_state: State<ScanRuntime> = app.state();
    let _model = model_state.model_lock.lock().await;
    let mut last_error = "The local model produced no output.".to_string();
    let mut generated: Option<serde_json::Value> = None;
    for attempt in 0..2 {
        let mut args = vec![
            "model-generate".to_string(),
            "--python".to_string(),
            python.clone(),
            "--worker".to_string(),
            worker.clone(),
            "--model".to_string(),
            model.clone(),
            "--backend".to_string(),
            "cpu".to_string(),
            // model-generate defaults to 2048, which caps input + output
            // together; the summarizer already runs 4096 on this hardware.
            "--max-tokens".to_string(),
            "4096".to_string(),
            "--prompt".to_string(),
            prompt.clone(),
        ];
        // First answers stay deterministic (temp 0, seed 1) so the same
        // question repeats stably. Follow-ups with history sample instead:
        // temp 0 plus a fixed seed would regurgitate the previous answer no
        // matter what the correction said.
        if history_text.is_empty() {
            args.push("--temperature".to_string());
            args.push("0".to_string());
            args.push("--seed".to_string());
            args.push("1".to_string());
        } else {
            args.push("--temperature".to_string());
            args.push("0.7".to_string());
        }
        let blocking_app = app.clone();
        let started = std::time::Instant::now();
        let output = tokio::task::spawn_blocking(move || {
            let cli = crate::bridge::sidecar_path(&blocking_app)
                .map_err(|e| std::io::Error::new(std::io::ErrorKind::NotFound, e))?;
            let arg_refs: Vec<&str> = args.iter().map(|s| s.as_str()).collect();
            crate::bridge::hidden_command(&cli).args(&arg_refs).output()
        })
        .await
        .map_err(|error| format!("Agent task failed: {error}"))?
        .map_err(|error| format!("Agent failed to launch: {error}"))?;
        crate::bridge::record_spawn(
            "model-generate",
            started.elapsed().as_millis(),
            output.status.code().unwrap_or(-1),
            None,
        );
        match serde_json::from_str::<serde_json::Value>(&String::from_utf8_lossy(&output.stdout)) {
            Ok(value) => {
                generated = Some(value);
                break;
            }
            Err(_) => {
                let stderr = String::from_utf8_lossy(&output.stderr);
                last_error = friendly_model_error(&stderr);
                // A deterministic failure repeats, so skip the second
                // spawn and its full model load.
                if attempt == 0 && !is_retryable(&stderr) {
                    break;
                }
            }
        }
    }
    let generated = match generated {
        Some(value) => value,
        None => {
            log_ask_failure(&question, prompt.len(), scoped.len(), &last_error);
            if let Some(id) = &thread_id {
                append_chat(&app, id, "error", &last_error, None, None).await;
            }
            return Err(last_error);
        }
    };
    let answer = generated
        .get("text")
        .and_then(|v| v.as_str())
        .unwrap_or("")
        .trim()
        .to_string();
    if answer.is_empty() {
        if let Some(id) = &thread_id {
            append_chat(&app, id, "error", "The local model produced no answer.", None, None).await;
        }
        return Err("The local model produced no answer.".to_string());
    }
    if let Some(id) = &thread_id {
        let citations_json = serde_json::to_string(&cited).unwrap_or_else(|_| "[]".to_string());
        append_chat(&app, id, "agent", &answer, Some(citations_json), Some(scoped.len())).await;
    }
    Ok(serde_json::json!({
        "answer": answer,
        "citations": cited,
        "scopedCount": scoped.len(),
        "totalCount": all_scans.len(),
    }))
}

/// The full ask prompt, pure for tests. Glint talks like a normal chatbot
/// that happens to remember the user's screen: warm, direct, a few
/// sentences, structure only when the message calls for it. There is no
/// mandatory narration, no forced Summary section, no obligatory durations —
/// that rigidity is what made every greeting come back as a robot report.
/// Conversation turns are quarantined from evidence: [n] citations may only
/// point at the numbered sessions, never at a previous assistant turn.
fn build_ask_prompt(
    period: &str,
    _opener: &str,
    question: &str,
    context: &str,
    history_section: &str,
) -> String {
    format!(
        "You are Glint, a friendly local assistant with a memory of the user's captured \
         on-screen activity {period}. Talk like a normal chatbot: warm, direct, a few \
         sentences. Answer the user's actual message — a greeting gets a greeting, a \
         question gets an answer, a correction gets a revised answer that states what \
         changed and why. Structure (lists, headings) only when it genuinely helps; \
         never force a Summary section or bullet list onto a message that does not ask \
         for one. You have the user's activity log below as numbered sessions ([n]). \
         Use it whenever the message is about what they did, and put a [n] citation \
         right after any fact it supports. Never invent activities, people, times, or \
         durations that are not in the sessions or conversation above. If the evidence \
         is thin, say what you know briefly and offer to look wider. Describe what \
         happened; prefer times of day over durations. Only state how long something \
         took if asked, and then give it as a wall-clock span, never as engaged time. \
         Order oldest-first (entries are listed newest-first, so reorder); merge \
         trivial repeats silently. Conversation turns above are NOT evidence: [n] \
         citations may only point at the numbered sessions below, never at a previous \
         assistant turn.{history_section}\n\nQuestion: {question}\n\nSessions (newest first):\n{context}"
    )
}

/// Previous turns of a chat thread, oldest first, capped so the session
/// evidence keeps budget priority. Best effort: any failure yields no
/// history and the ask proceeds unthreaded.
async fn load_thread_history(app: &AppHandle, thread_id: &str) -> String {
    let owned = vec![
        "chat-thread".to_string(),
        "--id".to_string(),
        thread_id.to_string(),
        "--limit".to_string(),
        "200".to_string(),
    ];
    let root = match crate::bridge::data_root() {
        Ok(root) => root,
        Err(_) => return String::new(),
    };
    let mut args: Vec<String> = owned;
    args.extend(crate::bridge::db_args(app, &root));
    let refs: Vec<&str> = args.iter().map(|s| s.as_str()).collect();
    let messages = crate::bridge::run_sidecar_async(app, &refs)
        .await
        .ok()
        .and_then(|out| out.json.get("messages").cloned())
        .and_then(|v| v.as_array().cloned())
        .unwrap_or_default();
    shape_history(&messages, 800)
}

/// Last two exchanges (user/agent turns), oldest first, newest kept when
/// trimming. Pure for tests.
fn shape_history(messages: &[serde_json::Value], budget: usize) -> String {
    const PER_TURN: usize = 300;
    let mut turns: Vec<String> = Vec::new();
    for message in messages {
        let role = message.get("role").and_then(|v| v.as_str()).unwrap_or("");
        let label = match role {
            "user" => "User",
            "agent" => "Assistant",
            "error" => "System",
            _ => continue,
        };
        let text = message
            .get("text")
            .and_then(|v| v.as_str())
            .unwrap_or("")
            .trim();
        if text.is_empty() {
            continue;
        }
        let clipped: String = text.chars().take(PER_TURN).collect();
        let clipped = if text.chars().count() > PER_TURN {
            format!("{clipped}...")
        } else {
            clipped
        };
        turns.push(format!("{label}: {clipped}"));
    }
    // Keep the newest turns that fit; the latest turn (often the correction
    // itself) is never dropped while anything is kept.
    let mut kept: Vec<&String> = Vec::new();
    let mut used = 0usize;
    for turn in turns.iter().rev() {
        if !kept.is_empty() && used + turn.len() + 1 > budget {
            break;
        }
        used += turn.len() + 1;
        kept.push(turn);
    }
    kept.reverse();
    kept
        .iter()
        .map(|turn| turn.as_str())
        .collect::<Vec<_>>()
        .join("\n")
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
    use super::{build_ask_prompt, is_retryable, scope_phrases, shape_history};

    #[test]
    fn scope_wording_follows_the_selected_range() {
        assert_eq!(scope_phrases("day").0, "today");
        assert_eq!(scope_phrases("week").0, "this week");
        assert_eq!(scope_phrases("all").0, "across your history");
        // Unknown scopes fall back to the day wording, matching in_scope.
        assert_eq!(scope_phrases("nonsense").0, "today");
        assert!(scope_phrases("week").1.contains("this week"));
    }

    #[test]
    fn deterministic_failures_are_not_retried() {
        assert!(!is_retryable(
            "Prompt too large for the local worker: ~2100 estimated input tokens exceed the ~1,600-token budget."
        ));
        assert!(!is_retryable("Gemma runtime is not ready. Missing: model."));
        assert!(!is_retryable("Prompt cannot be empty."));
    }

    #[test]
    fn transient_failures_are_retried() {
        assert!(is_retryable("litert_lm_conversation_send_message failed"));
        assert!(is_retryable("LiteRT-LM worker exited unexpectedly"));
        assert!(is_retryable("timed out"));
        assert!(is_retryable(""));
    }

    fn history_message(role: &str, text: &str) -> serde_json::Value {
        serde_json::json!({"role": role, "text": text})
    }

    #[test]
    fn empty_history_shapes_to_nothing() {
        assert_eq!(shape_history(&[], 800), "");
    }

    #[test]
    fn history_keeps_speakers_in_order_and_skips_unknown_roles() {
        let messages = vec![
            history_message("user", "What did I do?"),
            history_message("agent", "You coded."),
            history_message("system", "migrated"),
            history_message("user", "thats wrong"),
        ];
        let shaped = shape_history(&messages, 800);
        assert!(shaped.starts_with("User: What did I do?"), "{shaped}");
        assert!(shaped.contains("Assistant: You coded."), "{shaped}");
        assert!(shaped.ends_with("User: thats wrong"), "{shaped}");
    }

    #[test]
    fn history_trims_oldest_first_but_never_drops_the_latest_turn() {
        let messages = vec![
            history_message("user", &"q".repeat(500)),
            history_message("agent", &"a".repeat(500)),
            history_message("user", "thats wrong"),
        ];
        let shaped = shape_history(&messages, 800);
        assert!(shaped.ends_with("User: thats wrong"), "{shaped}");
        assert!(shaped.len() <= 800, "{shaped}");
    }

    #[test]
    fn error_turns_shape_as_system_notes() {
        let messages = vec![history_message("error", "worker was busy")];
        assert_eq!(shape_history(&messages, 800), "System: worker was busy");
    }

    #[test]
    fn ask_prompt_has_no_mandatory_summary_section() {
        let prompt = build_ask_prompt("today", "Today", "whats up? hows today?", "[0] x", "");
        assert!(!prompt.contains("Then a 'Summary:'"), "{prompt}");
        assert!(!prompt.contains("3-6 dash-led bullet"), "{prompt}");
        assert!(prompt.contains("Answer the user's actual message"), "{prompt}");
        assert!(
            prompt.contains("Only state how long something took if asked"),
            "{prompt}"
        );
    }

    #[test]
    fn ask_prompt_quarantines_history_from_citations() {
        let with_history = build_ask_prompt(
            "today",
            "Today",
            "thats wrong",
            "[0] x",
            "\n\nConversation so far (most recent last):\nUser: hi",
        );
        assert!(with_history.contains("never at a previous assistant turn"), "{with_history}");
        assert!(with_history.contains("Conversation so far"), "{with_history}");
        let without_history = build_ask_prompt("today", "Today", "what did I do?", "[0] x", "");
        assert!(!without_history.contains("Conversation so far"), "{without_history}");
    }
}
