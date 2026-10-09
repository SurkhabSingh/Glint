//! Glint's Tauri commands. Every one that touches the store, the screen or
//! the model is a request to the backend process (`backend.rs`); the host
//! itself only decides when to look and listens to Windows.

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
            format!("{process}: {label}. Captured.")
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
            "The window was captured and saved. Its summary is written once the activity ends and you step away."
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

/// Live read for the timeline hook pump: logging runs only inside an
/// initiated recording.
pub(crate) fn scanning_now(app: &AppHandle) -> bool {
    app.state::<ScanRuntime>()
        .state
        .lock()
        .map(|state| state.scanning)
        .unwrap_or(false)
}

/// One request to the backend from a command.
async fn call(app: &AppHandle, request: serde_json::Value) -> Result<serde_json::Value, String> {
    crate::backend::request(app, request, crate::backend::QUICK_TIMEOUT).await
}

/// Record the user's verdict on a session. Absolute: no rule overwrites it.
#[tauri::command]
pub async fn glint_set_session_outcome(
    app: AppHandle,
    id: String,
    outcome: String,
) -> Result<serde_json::Value, String> {
    call(&app, serde_json::json!({ "op": "session-outcome", "id": id, "outcome": outcome })).await
}

/// Agent chat threads, most recently active first. Chat text lives only in
/// the encrypted store and travels over the backend's pipe, never in argv.
#[tauri::command]
pub async fn glint_chat_threads(app: AppHandle, limit: Option<u32>) -> Result<serde_json::Value, String> {
    call(&app, serde_json::json!({ "op": "chat-threads", "limit": limit.unwrap_or(50).clamp(1, 200) })).await
}

/// One thread with its messages, oldest first.
#[tauri::command]
pub async fn glint_chat_thread(
    app: AppHandle,
    id: String,
    limit: Option<u32>,
) -> Result<serde_json::Value, String> {
    call(&app, serde_json::json!({ "op": "chat-thread", "id": id, "limit": limit.unwrap_or(200).clamp(1, 2000) })).await
}

/// Start a thread. Titles come from the first question (truncated upstream),
/// never model-generated, so opening a chat costs no inference.
#[tauri::command]
pub async fn glint_chat_create(
    app: AppHandle,
    title: String,
    scope: Option<String>,
) -> Result<serde_json::Value, String> {
    call(&app, serde_json::json!({ "op": "chat-create", "title": title, "scope": scope })).await
}

/// Rename a thread.
#[tauri::command]
pub async fn glint_chat_rename(
    app: AppHandle,
    id: String,
    title: String,
) -> Result<serde_json::Value, String> {
    call(&app, serde_json::json!({ "op": "chat-rename", "id": id, "title": title })).await
}

/// Delete a thread and its messages.
#[tauri::command]
pub async fn glint_chat_delete(app: AppHandle, id: String) -> Result<serde_json::Value, String> {
    call(&app, serde_json::json!({ "op": "chat-delete", "id": id })).await
}

/// Append one message. `glint_ask` keeps its own turns; this is for the rest.
#[tauri::command]
pub async fn glint_chat_append(
    app: AppHandle,
    thread: String,
    role: String,
    text: String,
    citations: Option<String>,
    scoped: Option<u32>,
) -> Result<serde_json::Value, String> {
    call(
        &app,
        serde_json::json!({
            "op": "chat-append",
            "thread": thread,
            "role": role,
            "text": text,
            "citations": citations,
            "scoped": scoped,
        }),
    )
    .await
}

/// Sessions with their summaries, newest first, worked out when asked.
#[tauri::command]
pub async fn glint_sessions(app: AppHandle, limit: Option<u32>) -> Result<serde_json::Value, String> {
    call(&app, serde_json::json!({ "op": "sessions", "limit": limit.unwrap_or(50).clamp(1, 200) })).await
}

/// Activities, newest first: one per thing the user did, each with its own
/// mode, time, events and checked summary. `from`/`to` narrow it to a window
/// (the last 30 days when left out).
#[tauri::command]
pub async fn glint_activities(
    app: AppHandle,
    limit: Option<u32>,
    from: Option<i64>,
    to: Option<i64>,
) -> Result<serde_json::Value, String> {
    call(
        &app,
        serde_json::json!({
            "op": "activities",
            "limit": limit.unwrap_or(300).clamp(1, 5000),
            "from": from,
            "to": to,
        }),
    )
    .await
}

/// Whether Glint is running as administrator. Apps that run as
/// administrator can only be read by a Glint that does too; otherwise only
/// their name and title are recorded.
#[tauri::command]
pub fn glint_admin_status() -> serde_json::Value {
    // SAFETY: no arguments; reads the current process token.
    let elevated = unsafe { windows::Win32::UI::Shell::IsUserAnAdmin() }.as_bool();
    serde_json::json!({ "elevated": elevated })
}

/// Restart Glint as administrator. Windows shows its own consent prompt; if
/// the user declines, nothing changes and this Glint keeps running.
#[tauri::command]
pub fn glint_restart_as_admin(app: AppHandle) -> Result<(), String> {
    use windows::core::{HSTRING, PCWSTR};
    use windows::Win32::UI::Shell::ShellExecuteW;
    use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

    let exe = std::env::current_exe().map_err(|error| error.to_string())?;
    let args = std::env::args()
        .skip(1)
        .map(|arg| {
            if arg.contains(' ') {
                format!("\"{arg}\"")
            } else {
                arg
            }
        })
        .collect::<Vec<_>>()
        .join(" ");
    let verb = HSTRING::from("runas");
    let file = HSTRING::from(exe.as_os_str());
    let params = HSTRING::from(args);
    // SAFETY: all strings outlive the call; no window handle is passed.
    let result = unsafe {
        ShellExecuteW(
            None,
            PCWSTR(verb.as_ptr()),
            PCWSTR(file.as_ptr()),
            PCWSTR(params.as_ptr()),
            PCWSTR::null(),
            SW_SHOWNORMAL,
        )
    };
    // ShellExecute reports success as a value above 32.
    if (result.0 as isize) <= 32 {
        return Err("Glint was not restarted (the administrator prompt was declined or failed).".to_string());
    }
    app.exit(0);
    Ok(())
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
#[tauri::command]
pub async fn glint_usage(app: AppHandle, from: i64, to: i64) -> Result<serde_json::Value, String> {
    if to <= from {
        return Err("The end of the range must be after its start.".to_string());
    }
    call(&app, serde_json::json!({ "op": "usage", "from": from, "to": to })).await
}

/// The user's verdict on an activity's task. Absolute: no rule rewrites it.
#[tauri::command]
pub async fn glint_set_activity_task(
    app: AppHandle,
    id: String,
    status: String,
) -> Result<serde_json::Value, String> {
    if !matches!(status.as_str(), "Open" | "LooksDone" | "Done" | "None") {
        return Err(format!("Unknown task status: {status}"));
    }
    call(&app, serde_json::json!({ "op": "activity-task", "id": id, "status": status })).await
}

/// The user's correction of how an app or site is treated. Activities are
/// worked out when read, so the correction shows everywhere straight away.
#[tauri::command]
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
    let profile = call(&app, serde_json::json!({ "op": "app-mode", "key": key, "mode": mode })).await?;
    let _ = app.emit("sessions-updated", serde_json::json!({ "corrected": 1 }));
    Ok(profile)
}

/// Record that the user went away or came back, or that recording started or
/// stopped. Grouping reads these to tell a real break from a screen that
/// simply did not change. Best effort: a lost marker only costs the fallback
/// gap rule. Blocks, so call it from a plain thread.
fn record_marker(app: &AppHandle, kind: &str) {
    record_marker_detail(app, kind, None);
}

fn record_marker_detail(app: &AppHandle, kind: &str, detail: Option<&str>) {
    // Stamped here, so the marker keeps its moment even if the backend is busy.
    let at = chrono::Utc::now().timestamp_millis();
    let mut request = serde_json::json!({ "op": "mark", "kind": kind, "at": at });
    if let Some(detail) = detail {
        request["detail"] = serde_json::Value::String(detail.to_string());
    }
    let Ok(reply) = crate::backend::request_blocking(app, request, crate::backend::QUICK_TIMEOUT) else {
        return;
    };
    // A start or stop is a row of its own on the live timeline, and coming
    // back starts a stretch in whatever is in front.
    for row in ["timeline", "focus"] {
        if let Some(row) = reply.get(row).filter(|row| row.is_object()) {
            let _ = app.emit("timeline-event", row);
        }
    }
}

/// Locking, sleep, shutdown and closed apps, from `system_events`. Runs on
/// a plain thread (never the async runtime), so the marker write can block.
/// The backend ends the stretch in front and, once the user is away,
/// describes what finished.
pub(crate) fn on_system_event(app: &AppHandle, kind: &str, detail: Option<&str>) {
    if !scanning_now(app) {
        return;
    }
    record_marker_detail(app, kind, detail);
}

// ---------------------------------------------------------------------------
// Stutter test mode: turns one suspect off for a run (never saved; back to
// normal when Glint restarts) and times every look, so a smooth or choppy
// run comes with numbers.
// ---------------------------------------------------------------------------

const TEST_MODES: [&str; 4] = ["normal", "no-accessibility", "no-capture", "titles-only"];

#[derive(Default)]
struct LookStats {
    looks: u64,
    total_ms: u64,
    max_ms: u64,
    since_ms: i64,
}

fn look_stats() -> &'static Mutex<LookStats> {
    static STATS: std::sync::OnceLock<Mutex<LookStats>> = std::sync::OnceLock::new();
    STATS.get_or_init(|| Mutex::new(LookStats { since_ms: chrono::Utc::now().timestamp_millis(), ..Default::default() }))
}

fn note_look(ms: u64) {
    let mut stats = look_stats().lock().unwrap();
    stats.looks += 1;
    stats.total_ms += ms;
    stats.max_ms = stats.max_ms.max(ms);
}

pub(crate) fn current_test_mode() -> String {
    std::env::var("GLINT_TEST_MODE")
        .ok()
        .filter(|mode| TEST_MODES.contains(&mode.as_str()))
        .unwrap_or_else(|| "normal".to_string())
}

fn test_mode_status() -> serde_json::Value {
    let stats = look_stats().lock().unwrap();
    serde_json::json!({
        "mode": current_test_mode(),
        "looks": stats.looks,
        "averageMs": if stats.looks == 0 { 0 } else { stats.total_ms / stats.looks },
        "slowestMs": stats.max_ms,
        "sinceMs": stats.since_ms,
    })
}

/// The stutter test mode and the look timings since it was set.
#[tauri::command]
pub fn glint_test_mode() -> serde_json::Value {
    test_mode_status()
}

/// Set the stutter test mode for this run and restart the timings. Every
/// capture started after this uses it; nothing is saved.
#[tauri::command]
pub fn glint_set_test_mode(mode: String) -> Result<serde_json::Value, String> {
    if !TEST_MODES.contains(&mode.as_str()) {
        return Err(format!("Unknown test mode: {mode}"));
    }
    if mode == "normal" {
        std::env::remove_var("GLINT_TEST_MODE");
    } else {
        std::env::set_var("GLINT_TEST_MODE", &mode);
    }
    *look_stats().lock().unwrap() = LookStats { since_ms: chrono::Utc::now().timestamp_millis(), ..Default::default() };
    Ok(test_mode_status())
}

// ---------------------------------------------------------------------------
// Where the local AI runs: the user's choice (GPU by default) and what
// actually happened (the GPU can be missing or fail to load the model).
// ---------------------------------------------------------------------------

#[derive(Default)]
struct AiBackendSeen {
    used: Option<String>,
    gpu_failure: Option<String>,
}

fn ai_backend_seen() -> &'static Mutex<AiBackendSeen> {
    static SEEN: std::sync::OnceLock<Mutex<AiBackendSeen>> = std::sync::OnceLock::new();
    SEEN.get_or_init(|| Mutex::new(AiBackendSeen::default()))
}

pub(crate) fn note_ai_backend(result: &serde_json::Value) {
    let mut seen = ai_backend_seen().lock().unwrap();
    if let Some(used) = result.get("aiBackend").and_then(|v| v.as_str()) {
        seen.used = Some(used.to_string());
    }
    if let Some(failure) = result.get("gpuFailure").and_then(|v| v.as_str()) {
        seen.gpu_failure = Some(failure.to_string());
    }
}

fn settings_path() -> Option<PathBuf> {
    crate::bridge::data_root().ok().map(|root| root.join("settings.json"))
}

fn preferred_ai_backend() -> String {
    settings_path()
        .and_then(|path| std::fs::read_to_string(path).ok())
        .and_then(|text| serde_json::from_str::<serde_json::Value>(&text).ok())
        .and_then(|json| json.get("aiBackend").and_then(|v| v.as_str()).map(str::to_string))
        .filter(|value| value == "cpu" || value == "gpu")
        .unwrap_or_else(|| "gpu".to_string())
}

/// The backend inherits the choice when it starts.
pub(crate) fn apply_ai_backend_env() {
    std::env::set_var("GLINT_LITERT_BACKEND", preferred_ai_backend());
}

/// The AI processor setting and what was last used.
#[tauri::command]
pub fn glint_ai_backend() -> serde_json::Value {
    let seen = ai_backend_seen().lock().unwrap();
    serde_json::json!({
        "preferred": preferred_ai_backend(),
        "lastUsed": seen.used,
        "gpuFailure": seen.gpu_failure,
    })
}

/// Choose GPU or CPU for the local AI. Saved, and the backend loads the
/// model on the new processor for its next request.
#[tauri::command]
pub async fn glint_set_ai_backend(app: AppHandle, backend: String) -> Result<serde_json::Value, String> {
    if backend != "gpu" && backend != "cpu" {
        return Err(format!("Unknown processor: {backend}"));
    }
    let path = settings_path().ok_or("No data folder to save the setting in.")?;
    let mut settings = std::fs::read_to_string(&path)
        .ok()
        .and_then(|text| serde_json::from_str::<serde_json::Value>(&text).ok())
        .filter(|value| value.is_object())
        .unwrap_or_else(|| serde_json::json!({}));
    settings["aiBackend"] = serde_json::Value::String(backend.clone());
    if let Some(dir) = path.parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    std::fs::write(&path, serde_json::to_string_pretty(&settings).unwrap_or_default())
        .map_err(|error| format!("Couldn't save the setting: {error}"))?;
    std::env::set_var("GLINT_LITERT_BACKEND", &backend);
    {
        let mut seen = ai_backend_seen().lock().unwrap();
        *seen = AiBackendSeen::default();
    }
    // The backend holds a model loaded on the old processor.
    call(&app, serde_json::json!({ "op": "ai-backend", "backend": backend })).await?;
    Ok(glint_ai_backend())
}

/// One look, through the backend. Pause abandons the wait at once; the
/// backend finishes that look on its own and its late reply is dropped.
async fn run_manual_scan(app: &AppHandle, generation: u64) -> Option<serde_json::Value> {
    let started = std::time::Instant::now();
    let reply = {
        let runtime: State<ScanRuntime> = app.state();
        let mut cancel = runtime.cancel.subscribe();
        tokio::select! {
            reply = crate::backend::request(
                app,
                serde_json::json!({ "op": "scan" }),
                crate::backend::SCAN_TIMEOUT,
            ) => Some(reply),
            _ = cancel.changed() => None,
        }
    };
    if !is_current(app, generation) {
        return None; // paused or superseded: discard
    }
    match reply? {
        Ok(outcome) => {
            note_look(started.elapsed().as_millis() as u64);
            crate::bridge::record_spawn("scan", started.elapsed().as_millis(), 0, None);
            Some(outcome)
        }
        // A look that failed is not the end of recording: the next one tries again.
        Err(error) => Some(serde_json::json!({ "kind": 5, "detail": error })),
    }
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
    // settled title change is treated like a switch — one focus row plus
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
                    let hop_app = app.clone();
                    let _ = tokio::task::spawn_blocking(move || {
                        crate::timeline::record_retitle(&hop_app, foreground)
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
                    // The user left: the backend ends the stretch in front
                    // and describes what finished while they are gone.
                    away = true;
                    let marker_app = app.clone();
                    let _ = tokio::task::spawn_blocking(move || record_marker(&marker_app, "user.away")).await;
                }
                cadence::POLL_INTERVAL_MS
            }
            CaptureDecision::Wait(ms) => ms,
            CaptureDecision::Capture(reason) => {
                if away {
                    away = false;
                    let marker_app = app.clone();
                    let _ = tokio::task::spawn_blocking(move || record_marker(&marker_app, "user.returned")).await;
                }
                foreground_changed_at = None;

                // Live tick announcement first: the frontend logs that a scan
                // started immediately instead of waiting out the capture.
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
    // generation behind with scanning off. A superseded generation (newer
    // Start, scanning back on) skips: the new loop owns the flow.
    let should_finalize = {
        let runtime: State<ScanRuntime> = app.state::<ScanRuntime>();
        let mut state = runtime.state.lock().unwrap();
        let paused = state.generation == generation.wrapping_add(1) && !state.scanning;
        if state.generation == generation || paused {
            state.scanning = false;
            true
        } else {
            false
        }
    };
    if should_finalize {
        // Stopping is one marker: the sitting ends there, and the backend
        // describes whatever finished without being asked.
        let marker_app = app.clone();
        let _ = tokio::task::spawn_blocking(move || record_marker(&marker_app, "run.stopped")).await;
        let _ = app.emit("sessions-updated", serde_json::json!({ "stopped": 1 }));
        let _ = app.emit(
            "scan-state",
            serde_json::json!({
                "scanning": false,
                "captureSummary": "Scanning stopped.",
                "overall": overall_info("Continuous local scanning is stopped.".to_string()),
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
pub async fn glint_initialize(app: AppHandle) -> Result<serde_json::Value, String> {
    // Compatibility reads no store, so it runs as a one-off check
    // (exit 2 tolerated: its report still parses).
    let compat_out = crate::bridge::run_sidecar_async(&app, &["compatibility"]).await?;
    let (compatibility_summary, checks, core_ready) = compatibility_port(&compat_out.json);

    let storage_summary = match call(&app, serde_json::json!({ "op": "storage" })).await {
        Ok(out) => {
            let events = out.get("eventCount").and_then(|v| v.as_i64()).unwrap_or(0);
            storage_port(out.get("database").unwrap_or(&serde_json::Value::Null), events)
        }
        Err(error) => format!("Storage failed: {error}"),
    };

    // Runtime status for the model line.
    let (model_summary, runtime_ready, model_path) =
        match call(&app, serde_json::json!({ "op": "runtime-status" })).await {
            Ok(out) => {
                let (summary, ready) = runtime_port(&out);
                let path = out.get("modelPath").and_then(|v| v.as_str()).map(|s| s.to_string());
                (summary, ready, path)
            }
            Err(error) => (format!("Gemma runtime check failed: {error}"), false, None),
        };

    // The banner shows the privacy probe, the last check (as the view model did).
    let (foreground_summary, overall) = match call(&app, serde_json::json!({ "op": "probe" })).await {
        Ok(out) => foreground_port(
            out.get("window").filter(|v| !v.is_null()),
            out.get("decision").unwrap_or(&serde_json::Value::Null),
        ),
        Err(error) => {
            let message = format!("Probe failed: {error}");
            (message.clone(), overall_error(message))
        }
    };

    // Scan history.
    let scans = call(&app, serde_json::json!({ "op": "history", "limit": 50 }))
        .await
        .ok()
        .and_then(|out| out.get("scans").cloned())
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
pub async fn glint_probe(app: AppHandle) -> Result<serde_json::Value, String> {
    match call(&app, serde_json::json!({ "op": "probe" })).await {
        Ok(out) => {
            let (foreground_summary, overall) = foreground_port(
                out.get("window").filter(|v| !v.is_null()),
                out.get("decision").unwrap_or(&serde_json::Value::Null),
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
pub async fn glint_verify_storage(app: AppHandle) -> Result<serde_json::Value, String> {
    match call(&app, serde_json::json!({ "op": "storage" })).await {
        Ok(out) => {
            let events = out.get("eventCount").and_then(|v| v.as_i64()).unwrap_or(0);
            let summary = storage_port(out.get("database").unwrap_or(&serde_json::Value::Null), events);
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

/// Machine compatibility check (ports CheckCompatibilityAsync). Reads no
/// store, so it runs as a one-off check.
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

/// Borderless-capture consent (ports RequestBorderlessAsync). A one-off
/// Windows prompt; reads no store.
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

/// Search the page lines Glint has kept, then the windows they were in.
#[tauri::command]
pub async fn glint_search(app: AppHandle, query: String) -> Result<serde_json::Value, String> {
    let trimmed = query.trim().to_string();
    if trimmed.is_empty() {
        return Ok(serde_json::json!({
            "results": [],
            "searchSummary": "Enter a person, application, topic, or phrase.",
        }));
    }
    match call(&app, serde_json::json!({ "op": "search", "query": trimmed, "limit": 30 })).await {
        Ok(out) => {
            let results = out.get("results").cloned().unwrap_or(serde_json::Value::Array(vec![]));
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

/// Recent looks, each with the session it falls in (ports LoadScanHistory).
#[tauri::command]
pub async fn glint_history(app: AppHandle, limit: Option<u32>) -> Result<serde_json::Value, String> {
    let scans = call(&app, serde_json::json!({ "op": "history", "limit": limit.unwrap_or(50).clamp(1, 500) }))
        .await
        .map(|out| out.get("scans").cloned().unwrap_or(serde_json::Value::Array(vec![])))
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
pub async fn glint_import_model(app: AppHandle, model_path: String) -> Result<serde_json::Value, String> {
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

    match call(&app, serde_json::json!({ "op": "runtime-status" })).await {
        Ok(out) => {
            let (model_summary, ready) = runtime_port(&out);
            // Auto-wire python/worker now that a model is selected.
            let runtime = crate::runtime::ensure_runtime(&app);
            let overall = if ready {
                overall_success(format!(
                    "Imported Gemma model: {}",
                    out
                        .get("modelPath")
                        .and_then(|v| v.as_str())
                        .unwrap_or(&model_path)
                ))
            } else {
                let missing = out
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

/// Whether the local AI runtime is installed, and what is missing if not.
async fn runtime_ready(app: &AppHandle) -> (bool, String) {
    match call(app, serde_json::json!({ "op": "runtime-status" })).await {
        Ok(out) => {
            let missing = out
                .get("missing")
                .and_then(|v| v.as_array())
                .map(|items| items.iter().filter_map(|v| v.as_str()).collect::<Vec<_>>().join(", "))
                .unwrap_or_else(|| "unknown".to_string());
            (runtime_port(&out).1, missing)
        }
        Err(_) => (false, "unknown".to_string()),
    }
}

/// Start continuous scanning (ports StartScanningAsync; owns the loop).
#[tauri::command]
pub async fn glint_start_scanning(app: AppHandle) -> Result<serde_json::Value, String> {
    {
        let runtime: State<ScanRuntime> = app.state();
        if runtime.state.lock().unwrap().scanning {
            return Ok(serde_json::json!({ "scanning": true }));
        }
    }

    // Runtime gate (ports the Missing check).
    let (ready, missing) = runtime_ready(&app).await;
    if !ready {
        let summary = format!("Gemma runtime is not ready. Missing: {missing}.");
        return Ok(serde_json::json!({
            "scanning": false,
            "captureSummary": summary,
            "overall": overall_error(summary.clone()),
        }));
    }

    // Borderless pre-request on start (ports StartScanningAsync).
    let borderless_allowed = crate::bridge::run_sidecar_async(&app, &["request-borderless"])
        .await
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
        if state.scanning {
            return Ok(serde_json::json!({ "scanning": true }));
        }
        state.scanning = true;
        state.generation = state.generation.wrapping_add(1);
        let generation = state.generation;
        runtime.cancel.send(generation).ok();
        generation
    };
    // The start marker first, so the backend logs window switches from here.
    let marker_app = app.clone();
    let _ = tokio::task::spawn_blocking(move || record_marker(&marker_app, "run.started")).await;
    let task_app = app.clone();
    tauri::async_runtime::spawn(async move {
        scan_loop(task_app, generation).await;
    });
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

/// Pause continuous scanning (ports PauseScanningAsync; abandons the look in flight).
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
    // The loop sees the generation change, writes the stop and finishes.
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
    let (ready, missing) = runtime_ready(&app).await;
    if !ready {
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
    let outcome = crate::backend::request(&app, serde_json::json!({ "op": "scan" }), crate::backend::SCAN_TIMEOUT).await?;
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

/// Ask: the question goes to the backend, which reads the activities on
/// demand, works out the period and the kind of activity, writes summaries
/// from the facts, uses the local model for everything else, and checks the
/// model's answer against the log. With a thread, the recent turns go with
/// the question and both question and answer are kept. The model stays
/// loaded between questions, shared with the summaries.
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
    crate::backend::request(
        &app,
        serde_json::json!({
            "op": "ask",
            "question": question,
            "scope": scope,
            "day": day.filter(|d| !d.trim().is_empty()),
            "threadId": thread_id.filter(|id| !id.trim().is_empty()),
        }),
        crate::backend::ASK_TIMEOUT,
    )
    .await
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

/// The focus log for one local day (YYYY-MM-DD, default today), oldest
/// first, with the recording's starts and stops between the switches.
#[tauri::command]
pub async fn glint_timeline(app: AppHandle, date: Option<String>) -> Result<serde_json::Value, String> {
    use chrono::{Local, NaiveDate, TimeZone};
    let day = date
        .filter(|d| !d.trim().is_empty())
        .unwrap_or_else(crate::timeline::local_day);
    let date = NaiveDate::parse_from_str(&day, "%Y-%m-%d").map_err(|_| format!("Not a day: {day}"))?;
    let local_ms = |date: NaiveDate| -> Result<i64, String> {
        Local
            .from_local_datetime(&date.and_hms_opt(0, 0, 0).expect("midnight"))
            .earliest()
            .map(|at| at.timestamp_millis())
            .ok_or_else(|| format!("No local midnight on {date}"))
    };
    let from = local_ms(date)?;
    let to = local_ms(date.succ_opt().ok_or("Day out of range")?)?;
    let out = call(&app, serde_json::json!({ "op": "timeline", "from": from, "to": to })).await?;
    Ok(serde_json::json!({
        "date": day,
        "events": out.get("events").cloned().unwrap_or(serde_json::Value::Array(vec![])),
        "eons": out.get("eons").cloned().unwrap_or(serde_json::Value::Array(vec![])),
        "droppedHooks": crate::timeline::dropped_count(),
    }))
}
