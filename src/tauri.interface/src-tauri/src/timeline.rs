//! The live window-switch lane. A Win32 `EVENT_SYSTEM_FOREGROUND` hook sees
//! every switch the millisecond it happens; each one goes to the backend,
//! which checks it against the privacy gate and writes it to the focus log in
//! the encrypted store. The new row is shown live. Only while recording.

use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{mpsc, OnceLock};

use tauri::{AppHandle, Emitter};
use windows::Win32::Foundation::HWND;
use windows::Win32::UI::Accessibility::{SetWinEventHook, HWINEVENTHOOK};
use windows::Win32::UI::WindowsAndMessaging::{
    DispatchMessageW, GetAncestor, GetMessageW, GetWindowThreadProcessId, EVENT_SYSTEM_FOREGROUND,
    GA_ROOTOWNER, MSG, OBJID_WINDOW, WINEVENT_OUTOFCONTEXT, WINEVENT_SKIPOWNPROCESS,
};

const HOOK_CHANNEL_CAP: usize = 64;

/// A switch and the moment it happened.
static HOOK_TX: OnceLock<mpsc::SyncSender<(isize, i64)>> = OnceLock::new();
static DROPPED_HOOKS: AtomicU64 = AtomicU64::new(0);

pub fn local_day() -> String {
    chrono::Local::now().format("%Y-%m-%d").to_string()
}

fn now_ms() -> i64 {
    chrono::Utc::now().timestamp_millis()
}

/// The window that owns `hwnd` when both belong to the same process: a Save
/// As dialog, an export window or a pop-up menu is part of its app's main
/// window, not a switch to something else.
fn root_owner(hwnd: HWND) -> HWND {
    unsafe {
        let owner = GetAncestor(hwnd, GA_ROOTOWNER);
        if owner.0.is_null() || owner == hwnd {
            return hwnd;
        }
        let mut dialog_pid: u32 = 0;
        let mut owner_pid: u32 = 0;
        GetWindowThreadProcessId(hwnd, Some(&mut dialog_pid as *mut u32));
        GetWindowThreadProcessId(owner, Some(&mut owner_pid as *mut u32));
        if dialog_pid != 0 && dialog_pid == owner_pid {
            owner
        } else {
            hwnd
        }
    }
}

/// One switch to the backend; a new focus row comes back unless the gate,
/// a refocus of the same window or Glint itself made it nothing to log.
fn handle_switch(app: &AppHandle, hwnd: isize, at: i64) {
    if hwnd == 0 {
        return;
    }
    let root = root_owner(HWND(hwnd as *mut core::ffi::c_void)).0 as isize;
    let reply = crate::backend::request_blocking(
        app,
        serde_json::json!({ "op": "focus", "handle": root, "at": at }),
        crate::backend::QUICK_TIMEOUT,
    );
    let Ok(row) = reply else {
        return;
    };
    if !row.is_object() {
        return;
    }
    // Watched for closing: closing the app ends its activity right away.
    if let Some(process) = row.get("process").and_then(|v| v.as_str()) {
        crate::system_events::track_foreground(root, process);
    }
    let _ = app.emit("timeline-event", &row);
}

/// In-app navigation inside one OS window (Discord server hop, browser tab,
/// document switch): the foreground hook never fires because the HWND does
/// not change, so the scan loop calls this once the new title has settled.
/// Must run off the async runtime (the request blocks).
pub fn record_retitle(app: &AppHandle, hwnd: isize) {
    handle_switch(app, hwnd, now_ms());
}

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
        if tx.try_send((hwnd.0 as isize, now_ms())).is_err() {
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

/// Start once from setup: OS hook thread + pump. The pump drops everything
/// while scanning is off — logging only runs inside an initiated recording.
pub fn start_hook(app: AppHandle) {
    let (tx, rx) = mpsc::sync_channel::<(isize, i64)>(HOOK_CHANNEL_CAP);
    let _ = HOOK_TX.set(tx);
    std::thread::Builder::new()
        .name("glint-foreground-hook".to_string())
        .spawn(hook_thread)
        .expect("foreground hook thread");
    // A dedicated thread, not an async task: rx.recv() blocks. The loop is
    // serial, which keeps rapid switches in timestamp order.
    std::thread::Builder::new()
        .name("glint-timeline-pump".to_string())
        .spawn(move || {
            while let Ok((hwnd, at)) = rx.recv() {
                if !crate::glint::scanning_now(&app) {
                    continue;
                }
                handle_switch(&app, hwnd, at);
            }
        })
        .expect("timeline pump thread");
}

/// Dropped-hook counter for Diagnostics honesty.
pub fn dropped_count() -> u64 {
    DROPPED_HOOKS.load(Ordering::Relaxed)
}
