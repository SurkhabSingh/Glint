//! Phase 1 v1 — live event timeline (no CLI changes).
//!
//! Fast lane, hook-exact: a Win32 `EVENT_SYSTEM_FOREGROUND` hook logs every
//! window switch the millisecond it happens; a 30 s heartbeat marks dwell.
//! Rows are timestamped twice (wall ms for display, tick-count ms for
//! durations), enriched with the Core privacy decision via the existing
//! `probe` verb, and persisted per-line-DPAPI-sealed JSONL so a crash loses
//! at most one in-flight payload. Logging runs only while scanning.

use std::path::PathBuf;
use std::sync::{
    atomic::{AtomicU64, Ordering},
    mpsc, Mutex, OnceLock,
};
use tauri::{AppHandle, Emitter, Manager};
use base64::Engine as _;
use windows::Win32::Foundation::{CloseHandle, HLOCAL, HWND, LocalFree};
use windows::Win32::Security::Cryptography::{
    CryptProtectData, CryptUnprotectData, CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN,
};
use windows::Win32::System::Threading::{
    OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32,
    PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows::Win32::UI::Accessibility::{SetWinEventHook, HWINEVENTHOOK};
use windows::Win32::UI::WindowsAndMessaging::{
    DispatchMessageW, GetForegroundWindow, GetMessageW, GetWindowTextW, GetWindowThreadProcessId,
    EVENT_SYSTEM_FOREGROUND, MSG, OBJID_WINDOW, WINEVENT_OUTOFCONTEXT, WINEVENT_SKIPOWNPROCESS,
};
use windows::core::PCWSTR;

const DPAPI_ENTROPY: &[u8] = b"Glint.Timeline.v1";
const HOOK_CHANNEL_CAP: usize = 64;
const SESSION_LOG_CAP: usize = 5000;

static HOOK_TX: OnceLock<mpsc::SyncSender<isize>> = OnceLock::new();
static SESSION_LOG: OnceLock<Mutex<Vec<serde_json::Value>>> = OnceLock::new();
static ROW_COUNTER: AtomicU64 = AtomicU64::new(0);
static INSTANCE_ID: OnceLock<String> = OnceLock::new();
static DROPPED_HOOKS: AtomicU64 = AtomicU64::new(0);
static PROBE_IN_FLIGHT: AtomicU64 = AtomicU64::new(0);
/// Live EON (one Start→Pause run): (id, started_wall_ms).
static CURRENT_EON: OnceLock<Mutex<Option<(String, i64)>>> = OnceLock::new();

fn current_eon() -> &'static Mutex<Option<(String, i64)>> {
    CURRENT_EON.get_or_init(|| Mutex::new(None))
}
static BOOT_WALL_MS: OnceLock<i64> = OnceLock::new();
static BOOT_INSTANT: OnceLock<std::time::Instant> = OnceLock::new();

fn session_log() -> &'static Mutex<Vec<serde_json::Value>> {
    SESSION_LOG.get_or_init(|| Mutex::new(Vec::new()))
}

fn instance_id() -> &'static str {
    INSTANCE_ID.get_or_init(|| {
        let pid = std::process::id();
        format!("{pid}-{}", mono_ms())
    })
}

// ---------------------------------------------------------------------------
// Clocks + identity
// ---------------------------------------------------------------------------

fn wall_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

/// Monotonic ms on a wall-anchored scale: stable across NTP jumps,
/// comparable with ts_wall_ms for durations.
fn mono_ms() -> u64 {
    let boot_wall = *BOOT_WALL_MS.get_or_init(wall_ms);
    let boot_instant = *BOOT_INSTANT.get_or_init(std::time::Instant::now);
    (boot_wall as u64).saturating_add(boot_instant.elapsed().as_millis() as u64)
}

pub fn local_day() -> String {
    chrono::Local::now().format("%Y-%m-%d").to_string()
}

struct FgIdentity {
    hwnd: isize,
    process: String,
    title: String,
}

fn identity_of(hwnd_raw: isize) -> Option<FgIdentity> {
    if hwnd_raw == 0 {
        return None;
    }
    let hwnd = HWND(hwnd_raw as *mut core::ffi::c_void);
    let title = unsafe {
        let mut buf = [0u16; 512];
        let read = GetWindowTextW(hwnd, &mut buf).max(0) as usize;
        String::from_utf16_lossy(&buf[..read.min(buf.len())])
    };
    let mut pid: u32 = 0;
    unsafe { GetWindowThreadProcessId(hwnd, Some(&mut pid as *mut u32)) };
    if pid == 0 {
        return None;
    }
    let process = unsafe {
        match OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) {
            Ok(handle) if !handle.is_invalid() => {
                let mut buf = [0u16; 512];
                let mut len = buf.len() as u32;
                // windows crate PWSTR wrapper removed; use raw parts via from_raw_parts_mut equivalent:
                let name = {
                    if QueryFullProcessImageNameW(
                        handle,
                        PROCESS_NAME_WIN32,
                        windows::core::PWSTR(buf.as_mut_ptr()),
                        &mut len,
                    )
                    .is_ok()
                    {
                        let path = String::from_utf16_lossy(&buf[..len as usize]);
                        PathBuf::from(&path)
                            .file_stem()
                            .map(|s| s.to_string_lossy().into_owned())
                            .unwrap_or(path)
                    } else {
                        format!("pid:{pid}")
                    }
                };
                let _ = CloseHandle(handle);
                name
            }
            _ => format!("pid:{pid}"),
        }
    };
    Some(FgIdentity {
        hwnd: hwnd_raw,
        process,
        title,
    })
}

fn current_foreground() -> Option<FgIdentity> {
    let hwnd = unsafe { GetForegroundWindow() };
    identity_of(hwnd.0 as isize)
}

// ---------------------------------------------------------------------------
// Per-line DPAPI envelope (file at rest is fully opaque)
// ---------------------------------------------------------------------------

fn dpapi_protect(plain: &[u8]) -> Result<Vec<u8>, String> {
    unsafe {
        let blob_in = CRYPT_INTEGER_BLOB {
            cbData: plain.len() as u32,
            pbData: plain.as_ptr() as *mut u8,
        };
        let blob_entropy = CRYPT_INTEGER_BLOB {
            cbData: DPAPI_ENTROPY.len() as u32,
            pbData: DPAPI_ENTROPY.as_ptr() as *mut u8,
        };
        let mut blob_out = std::mem::zeroed::<CRYPT_INTEGER_BLOB>();
        CryptProtectData(
            &blob_in,
            PCWSTR::null(),
            Some(&blob_entropy as *const _),
            Some(std::ptr::null()),
            Some(std::ptr::null()),
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut blob_out,
        )
        .map_err(|e| format!("DPAPI protect failed: {e}"))?;
        let out =
            std::slice::from_raw_parts(blob_out.pbData, blob_out.cbData as usize).to_vec();
        LocalFree(Some(HLOCAL(blob_out.pbData as *mut core::ffi::c_void)));
        Ok(out)
    }
}

fn dpapi_unprotect(sealed: &[u8]) -> Result<Vec<u8>, String> {
    unsafe {
        let blob_in = CRYPT_INTEGER_BLOB {
            cbData: sealed.len() as u32,
            pbData: sealed.as_ptr() as *mut u8,
        };
        let blob_entropy = CRYPT_INTEGER_BLOB {
            cbData: DPAPI_ENTROPY.len() as u32,
            pbData: DPAPI_ENTROPY.as_ptr() as *mut u8,
        };
        let mut blob_out = std::mem::zeroed::<CRYPT_INTEGER_BLOB>();
        CryptUnprotectData(
            &blob_in,
            None,
            Some(&blob_entropy as *const _),
            Some(std::ptr::null()),
            Some(std::ptr::null()),
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut blob_out,
        )
        .map_err(|e| format!("DPAPI unprotect failed: {e}"))?;
        let out =
            std::slice::from_raw_parts(blob_out.pbData, blob_out.cbData as usize).to_vec();
        LocalFree(Some(HLOCAL(blob_out.pbData as *mut core::ffi::c_void)));
        Ok(out)
    }
}

fn seal_line(row: &serde_json::Value) -> Result<String, String> {
    let json = serde_json::to_string(row).map_err(|e| format!("JSON encode failed: {e}"))?;
    let sealed = dpapi_protect(json.as_bytes())?;
    let envelope = serde_json::json!({ "v": 1, "data": base64::Engine::encode(&base64::engine::general_purpose::STANDARD, &sealed) });
    serde_json::to_string(&envelope).map_err(|e| format!("JSON encode failed: {e}"))
}

fn open_line(line: &str) -> Option<serde_json::Value> {
    let envelope: serde_json::Value = serde_json::from_str(line).ok()?;
    if envelope.get("v").and_then(|v| v.as_i64()) != Some(1) {
        return None;
    }
    let data = envelope.get("data").and_then(|v| v.as_str())?;
    let sealed = base64::Engine::decode(&base64::engine::general_purpose::STANDARD, data).ok()?;
    let plain = dpapi_unprotect(&sealed).ok()?;
    serde_json::from_slice(&plain).ok()
}

// ---------------------------------------------------------------------------
// Persistence: data_root/timeline/YYYY-MM-DD.jsonl (append + fsync per row)
// ---------------------------------------------------------------------------

fn day_file(app: &AppHandle, day: &str) -> Result<PathBuf, String> {
    let root = crate::bridge::data_root()?;
    let dir = root.join("timeline");
    std::fs::create_dir_all(&dir).map_err(|e| format!("Timeline dir failed: {e}"))?;
    Ok(dir.join(format!("{day}.jsonl")))
}

fn persist_row(app: &AppHandle, day: &str, row: &serde_json::Value) {
    let path = match day_file(app, day) {
        Ok(path) => path,
        Err(e) => {
            eprintln!("Timeline persist failed: {e}");
            return;
        }
    };
    let line = match seal_line(row) {
        Ok(line) => line,
        Err(e) => {
            eprintln!("Timeline seal failed: {e}");
            return;
        }
    };
    (|| -> std::io::Result<()> {
        use std::io::Write;
        let mut file = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(&path)?;
        writeln!(file, "{line}")?;
        file.sync_all()?;
        Ok(())
    })()
    .unwrap_or_else(|e| eprintln!("Timeline append failed: {e}"));
}

/// Read a day file back (sealed lines → values, oldest first).
pub fn load_day(app: &AppHandle, day: &str) -> Result<Vec<serde_json::Value>, String> {
    let path = day_file(app, day)?;
    if !path.is_file() {
        return Ok(Vec::new());
    }
    let text =
        std::fs::read_to_string(&path).map_err(|e| format!("Timeline read failed: {e}"))?;
    Ok(text.lines().filter_map(open_line).collect())
}

// ---------------------------------------------------------------------------
// EONs: one Start→Pause run. Append-only eons.jsonl with started/ended rows;
// readers pair them. An EON left open (crash/kill) reads as still recording.
// ---------------------------------------------------------------------------

fn eons_path(app: &AppHandle) -> Result<PathBuf, String> {
    let root = crate::bridge::data_root()?;
    let dir = root.join("timeline");
    std::fs::create_dir_all(&dir).map_err(|e| format!("Timeline dir failed: {e}"))?;
    Ok(dir.join("eons.jsonl"))
}

fn append_eons_line(app: &AppHandle, row: &serde_json::Value) {
    let path = match eons_path(app) {
        Ok(path) => path,
        Err(e) => {
            eprintln!("EON persist failed: {e}");
            return;
        }
    };
    let line = match seal_line(row) {
        Ok(line) => line,
        Err(e) => {
            eprintln!("EON seal failed: {e}");
            return;
        }
    };
    (|| -> std::io::Result<()> {
        use std::io::Write;
        let mut file = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(&path)?;
        writeln!(file, "{line}")?;
        file.sync_all()?;
        Ok(())
    })()
    .unwrap_or_else(|e| eprintln!("EON append failed: {e}"));
}

/// Open an EON for this recording run. Returns its id.
pub fn eon_started(app: &AppHandle) -> String {
    let started = wall_ms();
    let id = format!("eon-{started}");
    *current_eon().lock().unwrap() = Some((id.clone(), started));
    let mut row = base_row("eon.started");
    row["eon_id"] = serde_json::Value::String(id.clone());
    append_eons_line(app, &row);
    let _ = app.emit("timeline-event", &row);
    id
}

/// Close the live EON (single owner: the scan-loop finalize path).
pub fn eon_ended(app: &AppHandle) {
    let live = current_eon().lock().unwrap().take();
    if let Some((id, _started)) = live {
        let mut row = base_row("eon.ended");
        row["eon_id"] = serde_json::Value::String(id);
        row["ended_ms"] = serde_json::Value::from(wall_ms());
        append_eons_line(app, &row);
        let _ = app.emit("timeline-event", &row);
    }
}

/// Paired EON spans, oldest first: {id, started_ms, ended_ms|null}.
/// Open-ended (crash/kill mid-run) reads as still recording.
pub fn load_eons(app: &AppHandle) -> Vec<serde_json::Value> {
    let path = match eons_path(app) {
        Ok(path) => path,
        Err(_) => return Vec::new(),
    };
    let text = std::fs::read_to_string(&path).unwrap_or_default();
    let mut spans: Vec<serde_json::Value> = Vec::new();
    let mut open_index: std::collections::HashMap<String, usize> =
        std::collections::HashMap::new();
    for line in text.lines() {
        let Some(row) = open_line(line) else {
            continue;
        };
        let kind = row.get("kind").and_then(|v| v.as_str()).unwrap_or("");
        let id = row
            .get("eon_id")
            .and_then(|v| v.as_str())
            .unwrap_or("")
            .to_string();
        if id.is_empty() {
            continue;
        }
        match kind {
            "eon.started" => {
                let started = row.get("ts_wall_ms").and_then(|v| v.as_i64()).unwrap_or(0);
                open_index.insert(id.clone(), spans.len());
                spans.push(serde_json::json!({
                    "id": id, "started_ms": started, "ended_ms": serde_json::Value::Null,
                }));
            }
            "eon.ended" => {
                if let Some(&index) = open_index.get(&id) {
                    let ended = row
                        .get("ended_ms")
                        .and_then(|v| v.as_i64())
                        .or_else(|| row.get("ts_wall_ms").and_then(|v| v.as_i64()))
                        .unwrap_or(0);
                    spans[index]["ended_ms"] = serde_json::Value::from(ended);
                    open_index.remove(&id);
                }
            }
            _ => {}
        }
    }
    spans
}

/// Recording-state lifecycle marker in the day feed (also emitted live).
pub fn record_lifecycle(app: &AppHandle, kind: &str) {
    push_row(app, base_row(kind));
}

// ---------------------------------------------------------------------------
// Row construction + session log
// ---------------------------------------------------------------------------

fn push_row(app: &AppHandle, mut row: serde_json::Value) {
    row["instance_id"] = serde_json::Value::String(instance_id().to_string());
    let day = local_day();
    persist_row(app, &day, &row);
    let mut log = session_log().lock().unwrap();
    log.push(row.clone());
    while log.len() > SESSION_LOG_CAP {
        log.remove(0);
    }
    let _ = app.emit("timeline-event", &row);
}

fn base_row(kind: &str) -> serde_json::Value {
    let id = ROW_COUNTER.fetch_add(1, Ordering::SeqCst) + 1;
    serde_json::json!({
        "id": format!("{}-{}", wall_ms(), id),
        "ts_wall_ms": wall_ms(),
        "ts_mono_ms": mono_ms(),
        "kind": kind,
        "accuracy": "hook-exact",
    })
}

// ---------------------------------------------------------------------------
// Foreground hook (dedicated OS thread with a message loop)
// ---------------------------------------------------------------------------

unsafe extern "system" fn hook_proc(
    _hook: HWINEVENTHOOK,
    event: u32,
    hwnd: HWND,
    id_object: i32,
    _id_child: i32,
    _thread: u32,
    _time: u32,
) {
    if event != EVENT_SYSTEM_FOREGROUND || id_object != OBJID_WINDOW.0 {
        return;
    }
    if let Some(tx) = HOOK_TX.get() {
        if tx.try_send(hwnd.0 as isize).is_err() {
            DROPPED_HOOKS.fetch_add(1, Ordering::Relaxed);
        }
    }
}

fn hook_thread() {
    use windows::Win32::Foundation::HMODULE;
    unsafe {
        let _hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            Some(HMODULE::default()),
            Some(hook_proc),
            0,
            0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS,
        );
        let mut msg = std::mem::zeroed::<MSG>();
        // Blocks forever pumping this thread's queue; the hook lives here.
        while GetMessageW(&mut msg, None, 0, 0).as_bool() {
            DispatchMessageW(&msg);
        }
    }
}

/// Turn a probe attempt into a decision. Fails closed (README invariant 1:
/// "Secure or indeterminate state suppresses capture"): if the probe was
/// skipped or produced nothing we cannot tell whether the window is
/// sensitive, so the row keeps its process but loses its title.
fn resolve_probe(
    busy: bool,
    probed: Option<(bool, Option<String>, Option<String>)>,
) -> (bool, Option<String>, Option<String>) {
    if busy {
        return (
            false,
            Some("ProbeUnavailable".to_string()),
            Some("privacy probe was busy; title suppressed".to_string()),
        );
    }
    probed.unwrap_or_else(|| {
        (
            false,
            Some("ProbeUnavailable".to_string()),
            Some("privacy probe returned no decision; title suppressed".to_string()),
        )
    })
}

/// Probe the Core privacy decision for an HWND (existing verb, no CLI change).
/// Returns (allowed, reason_name_or_None, detail_or_None).
fn probe_decision(app: &AppHandle, hwnd: isize) -> (bool, Option<String>, Option<String>) {
    // Bound concurrent probes so Alt-Tab storms serialize instead of piling up.
    if PROBE_IN_FLIGHT.fetch_add(1, Ordering::SeqCst) >= 2 {
        PROBE_IN_FLIGHT.fetch_sub(1, Ordering::SeqCst);
        return resolve_probe(true, None);
    }
    let result = (|| {
        let handle = hwnd.to_string();
        let args = vec!["probe".to_string(), "--handle".to_string(), handle];
        let arg_refs: Vec<&str> = args.iter().map(|s| s.as_str()).collect();
        let out = crate::bridge::run_sidecar(app, &arg_refs).ok()?;
        let decision = out.json.get("decision")?;
        let allowed = decision.get("allowed").and_then(|v| v.as_bool()).unwrap_or(false);
        let reason = decision
            .get("reason")
            .and_then(|v| v.as_i64())
            .map(crate::glint::reason_name)
            .map(|s| s.to_string());
        let detail = decision
            .get("detail")
            .and_then(|v| v.as_str())
            .map(|s| s.to_string());
        Some((allowed, reason, detail))
    })();
    PROBE_IN_FLIGHT.fetch_sub(1, Ordering::SeqCst);
    resolve_probe(false, result)
}

fn handle_switch(app: &AppHandle, hwnd: isize) {
    let Some(identity) = identity_of(hwnd) else {
        return;
    };
    // Same-window refocus flicker: collapse into the previous row.
    {
        let log = session_log().lock().unwrap();
        if let Some(last) = log.last() {
            let same = last.get("process").and_then(|v| v.as_str()) == Some(identity.process.as_str())
                && last.get("title").and_then(|v| v.as_str()) == Some(identity.title.as_str())
                && last.get("kind").and_then(|v| v.as_str())
                    == Some("window.focused");
            if same {
                return;
            }
        }
    }
    // Enrich with the Core gate decision (suppressed windows keep process +
    // reason + detail but never their title — mirrors Core semantics).
    let (allowed, reason, detail) = probe_decision(app, identity.hwnd);
    let mut row = base_row("window.focused");
    row["process"] = serde_json::Value::String(identity.process);
    if allowed {
        row["title"] = serde_json::Value::String(identity.title);
    } else {
        row["title"] = serde_json::Value::Null;
        row["suppressed"] = serde_json::Value::String(
            reason.unwrap_or_else(|| "Unknown".to_string()),
        );
        if let Some(detail) = detail {
            row["suppressedDetail"] = serde_json::Value::String(detail);
        }
    }
    push_row(app, row);
}

/// In-app navigation inside one OS window (Discord server hop, browser tab,
/// document switch): the foreground hook never fires because the HWND does
/// not change, so the scan loop calls this once the new title has settled.
/// Reuses the focus pipeline, so privacy probing, flicker collapsing, and
/// heartbeat title reuse all behave exactly like a real switch. Must run off
/// the async runtime (it spawns the probe sidecar synchronously).
pub fn record_retitle(app: &AppHandle, hwnd: isize) {
    handle_switch(app, hwnd);
}

/// The title a heartbeat may record: the one from the most recent
/// `window.focused` row for the same process, which already passed the
/// privacy gate. Returns None when that row is for another process or had
/// its title suppressed, so a blocked title is never revived later.
fn heartbeat_title(log: &[serde_json::Value], process: &str) -> Option<String> {
    log.iter()
        .rev()
        .find(|row| row.get("kind").and_then(|v| v.as_str()) == Some("window.focused"))
        .filter(|row| row.get("process").and_then(|v| v.as_str()) == Some(process))
        .and_then(|row| row.get("title").and_then(|v| v.as_str()))
        .map(|title| title.to_string())
}

/// 30 s dwell heartbeat (called from the scan loop's heartbeat task).
pub fn record_heartbeat(app: &AppHandle) {
    let Some(identity) = current_foreground() else {
        return;
    };
    // Never read the live title here: this window was privacy-checked on
    // focus, and re-reading would leak a title the gate suppressed.
    let (dwell_ms, title) = {
        let log = session_log().lock().unwrap();
        let dwell = log
            .last()
            .and_then(|last| last.get("ts_wall_ms").and_then(|v| v.as_i64()))
            .map(|last_ts| wall_ms() - last_ts)
            .unwrap_or(0);
        (dwell, heartbeat_title(&log, &identity.process))
    };
    let mut row = base_row("heartbeat");
    row["accuracy"] = serde_json::Value::String("heartbeat".to_string());
    row["process"] = serde_json::Value::String(identity.process);
    match title {
        Some(title) => row["title"] = serde_json::Value::String(title),
        None => {
            row["title"] = serde_json::Value::Null;
            row["suppressed"] = serde_json::Value::String("TitleNotChecked".to_string());
            row["suppressedDetail"] = serde_json::Value::String(
                "no privacy-checked title for this window".to_string(),
            );
        }
    }
    row["dwell_ms"] = serde_json::Value::from(dwell_ms);
    push_row(app, row);
}

/// Start once from setup: OS hook thread + async pump. The pump drops
/// everything while scanning is off — logging only runs inside an
/// initiated recording.
pub fn start_hook(app: AppHandle) {
    let (tx, rx) = mpsc::sync_channel::<isize>(HOOK_CHANNEL_CAP);
    let _ = HOOK_TX.set(tx);
    std::thread::Builder::new()
        .name("glint-foreground-hook".to_string())
        .spawn(hook_thread)
        .expect("foreground hook thread");
    let pump = app.clone();
    // A dedicated thread, not an async task: rx.recv() blocks, so on the async
    // runtime it parked a worker thread for the life of the process. The loop
    // is serial anyway, which keeps rapid switches in timestamp order.
    std::thread::Builder::new()
        .name("glint-timeline-pump".to_string())
        .spawn(move || {
            while let Ok(hwnd) = rx.recv() {
                if !crate::glint::scanning_now(&pump) {
                    continue;
                }
                handle_switch(&pump, hwnd);
            }
        })
        .expect("timeline pump thread");
}

/// Dropped-hook counter for Diagnostics honesty.
pub fn dropped_count() -> u64 {
    DROPPED_HOOKS.load(Ordering::Relaxed)
}

#[cfg(test)]
mod tests {
    use super::{heartbeat_title, resolve_probe};
    use serde_json::json;

    #[test]
    fn busy_probe_suppresses_the_title() {
        let (allowed, reason, detail) = resolve_probe(true, None);
        assert!(!allowed);
        assert_eq!(reason.as_deref(), Some("ProbeUnavailable"));
        assert!(detail.is_some());
    }

    #[test]
    fn failed_probe_suppresses_the_title() {
        let (allowed, reason, _) = resolve_probe(false, None);
        assert!(!allowed);
        assert_eq!(reason.as_deref(), Some("ProbeUnavailable"));
    }

    #[test]
    fn probe_decision_passes_through_unchanged() {
        let probed = Some((true, None, None));
        assert_eq!(resolve_probe(false, probed), (true, None, None));

        let denied = Some((
            false,
            Some("ElevatedProcess".to_string()),
            Some("elevated".to_string()),
        ));
        let (allowed, reason, detail) = resolve_probe(false, denied);
        assert!(!allowed);
        assert_eq!(reason.as_deref(), Some("ElevatedProcess"));
        assert_eq!(detail.as_deref(), Some("elevated"));
    }

    #[test]
    fn heartbeat_reuses_the_last_checked_title() {
        let log = vec![json!({
            "kind": "window.focused",
            "process": "Code",
            "title": "roadmap.md - Code",
        })];
        assert_eq!(
            heartbeat_title(&log, "Code").as_deref(),
            Some("roadmap.md - Code")
        );
    }

    #[test]
    fn heartbeat_keeps_a_suppressed_title_suppressed() {
        let log = vec![json!({
            "kind": "window.focused",
            "process": "keepass",
            "title": serde_json::Value::Null,
            "suppressed": "BlockedProcess",
        })];
        assert_eq!(heartbeat_title(&log, "keepass"), None);
    }

    #[test]
    fn heartbeat_ignores_a_title_from_another_process() {
        let log = vec![json!({
            "kind": "window.focused",
            "process": "Code",
            "title": "roadmap.md - Code",
        })];
        assert_eq!(heartbeat_title(&log, "keepass"), None);
    }

    #[test]
    fn heartbeat_looks_past_earlier_heartbeats() {
        let log = vec![
            json!({"kind": "window.focused", "process": "Code", "title": "roadmap.md - Code"}),
            json!({"kind": "heartbeat", "process": "Code", "title": "roadmap.md - Code"}),
        ];
        assert_eq!(
            heartbeat_title(&log, "Code").as_deref(),
            Some("roadmap.md - Code")
        );
    }

    #[test]
    fn heartbeat_without_any_focus_row_has_no_title() {
        assert_eq!(heartbeat_title(&[], "Code"), None);
    }
}
