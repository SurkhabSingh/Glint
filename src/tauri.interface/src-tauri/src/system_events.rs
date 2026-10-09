//! Signals that something ended, straight from Windows:
//!
//! - the PC was locked or unlocked (session notifications),
//! - it is going to sleep or woke up (power broadcasts),
//! - it is shutting down or the user is signing out (end-session messages),
//! - an app the user was in was closed (its last window went away).
//!
//! Each becomes a marker the session and activity builders read, so a
//! sitting ends where the PC actually stopped being used, and an app's
//! activity ends the moment it is closed. Recorded only while scanning.
//!
//! Locking, sleep and shutdown arrive as window messages, so this module
//! owns a hidden top-level window on its own thread. (A message-only window
//! would not receive the broadcasts.) App closes come from a WinEvent hook
//! on window destruction, filtered to windows the user actually had in front.

use std::collections::HashMap;
use std::sync::{mpsc, Mutex, OnceLock};

use tauri::AppHandle;
use windows::core::{w, BOOL};
use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::RemoteDesktop::{
    WTSRegisterSessionNotification, NOTIFY_FOR_THIS_SESSION,
};
use windows::Win32::UI::Accessibility::{SetWinEventHook, HWINEVENTHOOK};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DispatchMessageW, EnumWindows, GetMessageW,
    GetWindowThreadProcessId, IsWindowVisible, RegisterClassW, TranslateMessage, MSG,
    WINDOW_EX_STYLE, WINDOW_STYLE, WINEVENT_OUTOFCONTEXT, WINEVENT_SKIPOWNPROCESS, WNDCLASSW,
};

const WM_QUERYENDSESSION: u32 = 0x0011;
const WM_ENDSESSION: u32 = 0x0016;
const WM_POWERBROADCAST: u32 = 0x0218;
const WM_WTSSESSION_CHANGE: u32 = 0x02B1;
const WTS_SESSION_LOCK: usize = 0x7;
const WTS_SESSION_UNLOCK: usize = 0x8;
const PBT_APMSUSPEND: usize = 0x4;
const PBT_APMRESUMESUSPEND: usize = 0x7;
const PBT_APMRESUMEAUTOMATIC: usize = 0x12;
const EVENT_OBJECT_DESTROY: u32 = 0x8001;
const OBJID_WINDOW: i32 = 0;
const CHILDID_SELF: i32 = 0;

/// How long after a window closes to check whether its app is really gone
/// (a browser closing one of two windows, an app swapping its splash
/// screen for its main window).
const CLOSE_SETTLE_MS: u64 = 1_500;

static APP: OnceLock<AppHandle> = OnceLock::new();
static CLOSE_TX: OnceLock<mpsc::SyncSender<(u32, String)>> = OnceLock::new();

/// Windows the user had in front, by handle: the app's process id and name.
/// Only these are watched for closing; everything else Windows destroys
/// (tooltips, menus, hidden helpers) is ignored in the hook.
fn tracked() -> &'static Mutex<HashMap<isize, (u32, String)>> {
    static TRACKED: OnceLock<Mutex<HashMap<isize, (u32, String)>>> = OnceLock::new();
    TRACKED.get_or_init(|| Mutex::new(HashMap::new()))
}

/// Remember a window the user brought to the front (called on every switch).
pub fn track_foreground(hwnd: isize, process: &str) {
    let mut pid: u32 = 0;
    unsafe {
        GetWindowThreadProcessId(HWND(hwnd as *mut core::ffi::c_void), Some(&mut pid as *mut u32));
    }
    if pid == 0 || process.starts_with("pid:") {
        return;
    }
    let mut map = tracked().lock().unwrap();
    // Bounded: a long day of switching must not grow this forever.
    if map.len() > 512 {
        map.clear();
    }
    map.insert(hwnd, (pid, app_key(process)));
}

/// The app key the activity model uses: lowercase process name, no ".exe".
fn app_key(process: &str) -> String {
    let lower = process.trim().to_ascii_lowercase();
    lower.strip_suffix(".exe").unwrap_or(&lower).to_string()
}

pub fn start(app: AppHandle) {
    let _ = APP.set(app);
    let (tx, rx) = mpsc::sync_channel::<(u32, String)>(64);
    let _ = CLOSE_TX.set(tx);

    std::thread::Builder::new()
        .name("glint-system-events".to_string())
        .spawn(message_thread)
        .expect("system events thread");

    std::thread::Builder::new()
        .name("glint-app-close".to_string())
        .spawn(move || {
            while let Ok((pid, process)) = rx.recv() {
                std::thread::sleep(std::time::Duration::from_millis(CLOSE_SETTLE_MS));
                if has_visible_window(pid) {
                    continue;
                }
                if let Some(app) = APP.get() {
                    crate::glint::on_system_event(app, "app.closed", Some(&process));
                }
            }
        })
        .expect("app close thread");
}

fn message_thread() {
    unsafe {
        let Ok(module) = GetModuleHandleW(None) else {
            eprintln!("Glint system events unavailable: no module handle");
            return;
        };
        let class = WNDCLASSW {
            lpfnWndProc: Some(window_proc),
            hInstance: module.into(),
            lpszClassName: w!("GlintSystemEvents"),
            ..Default::default()
        };
        RegisterClassW(&class);
        // A real top-level window, never shown: only those receive the
        // end-session and power broadcasts.
        let hwnd = match CreateWindowExW(
            WINDOW_EX_STYLE(0),
            w!("GlintSystemEvents"),
            w!("Glint system events"),
            WINDOW_STYLE(0),
            0,
            0,
            0,
            0,
            None,
            None,
            Some(module.into()),
            None,
        ) {
            Ok(hwnd) => hwnd,
            Err(error) => {
                eprintln!("Glint system events unavailable: {error}");
                return;
            }
        };
        if let Err(error) = WTSRegisterSessionNotification(hwnd, NOTIFY_FOR_THIS_SESSION) {
            eprintln!("Glint lock/unlock events unavailable: {error}");
        }
        let _destroy_hook = SetWinEventHook(
            EVENT_OBJECT_DESTROY,
            EVENT_OBJECT_DESTROY,
            None,
            Some(destroy_hook),
            0,
            0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS,
        );

        let mut msg = MSG::default();
        while GetMessageW(&mut msg, None, 0, 0).as_bool() {
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
}

unsafe extern "system" fn window_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if let Some(app) = APP.get() {
        match msg {
            WM_WTSSESSION_CHANGE => match wparam.0 {
                WTS_SESSION_LOCK => crate::glint::on_system_event(app, "user.locked", None),
                WTS_SESSION_UNLOCK => crate::glint::on_system_event(app, "user.unlocked", None),
                _ => {}
            },
            WM_POWERBROADCAST => match wparam.0 {
                PBT_APMSUSPEND => crate::glint::on_system_event(app, "system.sleep", None),
                PBT_APMRESUMESUSPEND | PBT_APMRESUMEAUTOMATIC => {
                    crate::glint::on_system_event(app, "system.resumed", None)
                }
                _ => {}
            },
            // Windows asks first, then confirms. Write on the question: the
            // process may not get another chance. Never block the shutdown.
            WM_QUERYENDSESSION => {
                crate::glint::on_system_event(app, "system.shutdown", None);
                return LRESULT(1);
            }
            WM_ENDSESSION => return LRESULT(0),
            _ => {}
        }
    }
    DefWindowProcW(hwnd, msg, wparam, lparam)
}

unsafe extern "system" fn destroy_hook(
    _hook: HWINEVENTHOOK,
    _event: u32,
    hwnd: HWND,
    id_object: i32,
    id_child: i32,
    _thread: u32,
    _time: u32,
) {
    if id_object != OBJID_WINDOW || id_child != CHILDID_SELF {
        return;
    }
    // Fast path: most destroyed windows were never in front.
    let entry = tracked().lock().ok().and_then(|mut map| map.remove(&(hwnd.0 as isize)));
    if let (Some((pid, process)), Some(tx)) = (entry, CLOSE_TX.get()) {
        let _ = tx.try_send((pid, process));
    }
}

/// Whether the process still shows any window: closing one of two browser
/// windows, or a launcher swapping windows, is not closing the app.
fn has_visible_window(pid: u32) -> bool {
    struct Search {
        pid: u32,
        found: bool,
    }
    unsafe extern "system" fn visit(hwnd: HWND, lparam: LPARAM) -> BOOL {
        let search = &mut *(lparam.0 as *mut Search);
        let mut owner: u32 = 0;
        GetWindowThreadProcessId(hwnd, Some(&mut owner as *mut u32));
        if owner == search.pid && IsWindowVisible(hwnd).as_bool() {
            search.found = true;
            return BOOL(0);
        }
        BOOL(1)
    }
    let mut search = Search { pid, found: false };
    unsafe {
        let _ = EnumWindows(Some(visit), LPARAM(&mut search as *mut Search as isize));
    }
    search.found
}
