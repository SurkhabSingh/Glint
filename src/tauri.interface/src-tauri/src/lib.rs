//! Glint Tauri shell.
//!
//! Ports the `Glint.Phase0.App` frontend (WinUI 3) to Tauri + React:
//! - dashboard window with Activity / Search / Diagnostics pages,
//! - floating command-bar window toggled by Ctrl+Alt+G,
//! - system-tray icon with the same context menu as `TrayHotkeyHost`,
//! - hide-to-tray on close/minimize.
//! All capture/privacy/storage/model work is bridged to `Glint.Phase0.Core`
//! through the `Glint.Phase0.Cli` sidecar (see `bridge.rs` / `glint.rs`).

mod bridge;
mod glass;
mod glint;
mod runtime;

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;
use tauri::{
    image::Image,
    menu::{Menu, MenuItem},
    tray::TrayIconBuilder,
    AppHandle, Emitter, Manager, WindowEvent,
};
use tauri_plugin_global_shortcut::ShortcutState;

/// Set once the user picks Exit (ports RequestExit); otherwise closing the
/// dashboard hides it to the tray (ports HideToTray).
static ALLOW_EXIT: AtomicBool = AtomicBool::new(false);

pub struct TrayMenuItems {
    start: MenuItem<tauri::Wry>,
    pause: MenuItem<tauri::Wry>,
}

/// Show and focus the dashboard (ports MainWindow.ShowDashboard).
pub fn show_main(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
}

fn toggle_command_bar(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("command-bar") {
        if window.is_visible().unwrap_or(false) {
            let _ = window.hide();
        } else {
            // Center near the top of the current monitor (ports CenterNearTop).
            if let Ok(Some(monitor)) = window.current_monitor() {
                let work = monitor.work_area();
                let scale = monitor.scale_factor();
                let width = 480.0 * scale;
                let x = work.position.x as f64 + ((work.size.width as f64 - width) / 2.0).max(0.0);
                let y = work.position.y as f64
                    + ((work.size.height as f64 / 7.0).max(32.0 * scale));
                use tauri::Position;
                let _ = window.set_position(Position::Physical(
                    tauri::PhysicalPosition { x: x as i32, y: y as i32 },
                ));
            }
            let _ = window.show();
            let _ = window.set_focus();
            let _ = window.emit("command-bar-shown", ());
        }
    }
}

/// Enable/disable the tray Start/Pause items from scan state
/// (ports the MenuGray logic in TrayHotkeyHost.ShowContextMenu).
pub fn refresh_tray(app: &AppHandle, scanning: bool) {
    if let Some(items) = app.try_state::<Mutex<TrayMenuItems>>().map(|s| s) {
        if let Ok(items) = items.lock() {
            let _ = items.start.set_enabled(!scanning);
            let _ = items.pause.set_enabled(scanning);
        }
    }
}

fn build_tray(app: &AppHandle) -> tauri::Result<()> {
    let open_bar =
        MenuItem::with_id(app, "tray-open-bar", "Open command bar\tCtrl+Alt+G", true, None::<&str>)?;
    let open_dashboard = MenuItem::with_id(app, "tray-open", "Open Glint", true, None::<&str>)?;
    let search = MenuItem::with_id(app, "tray-search", "Search context", true, None::<&str>)?;
    let start = MenuItem::with_id(app, "tray-start", "Start scanning", true, None::<&str>)?;
    let pause = MenuItem::with_id(app, "tray-pause", "Pause scanning", false, None::<&str>)?;
    let exit = MenuItem::with_id(app, "tray-exit", "Exit Glint", true, None::<&str>)?;
    use tauri::menu::PredefinedMenuItem;
    let sep1 = PredefinedMenuItem::separator(app)?;
    let sep2 = PredefinedMenuItem::separator(app)?;
    let menu = Menu::with_items(
        app,
        &[&open_bar, &open_dashboard, &search, &sep1, &start, &pause, &sep2, &exit],
    )?;

    app.manage(Mutex::new(TrayMenuItems { start, pause }));

    let icon = Image::from_path(concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/icons/32x32.png"
    ))
    .unwrap_or_else(|_| Image::new(&[0, 0, 0, 0], 1, 1));
    TrayIconBuilder::with_id("glint-tray")
        .icon(icon)
        .tooltip("Glint - Ctrl+Alt+G")
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id.as_ref() {
            "tray-open-bar" => toggle_command_bar(app),
            "tray-open" => show_main(app),
            "tray-search" => {
                show_main(app);
                let _ = app.emit("open-search", serde_json::json!({ "query": null }));
            }
            "tray-start" => {
                let handle = app.clone();
                tauri::async_runtime::spawn(async move {
                    match glint::glint_start_scanning(handle.clone()) {
                        Ok(state) => {
                            let _ = handle.emit("scan-state", &state);
                        }
                        Err(error) => {
                            let _ = handle.emit(
                                "scan-state",
                                serde_json::json!({
                                    "scanning": false,
                                    "captureSummary": error,
                                }),
                            );
                        }
                    }
                });
            }
            "tray-pause" => {
                let handle = app.clone();
                tauri::async_runtime::spawn(async move {
                    match glint::glint_pause_scanning(handle.clone()).await {
                        Ok(state) => {
                            let _ = handle.emit("scan-state", &state);
                        }
                        Err(error) => {
                            let _ = handle.emit(
                                "scan-state",
                                serde_json::json!({
                                    "scanning": false,
                                    "captureSummary": error,
                                }),
                            );
                        }
                    }
                });
            }
            "tray-exit" => {
                ALLOW_EXIT.store(true, Ordering::SeqCst);
                app.exit(0);
            }
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            // Left-click toggles the command bar (ports WindowMessageLeftButtonUp).
            if let tauri::tray::TrayIconEvent::Click {
                button: tauri::tray::MouseButton::Left,
                button_state: tauri::tray::MouseButtonState::Up,
                ..
            } = event
            {
                toggle_command_bar(tray.app_handle());
            }
        })
        .build(app)?;
    Ok(())
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .manage(glint::ScanRuntime::default())
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(
            tauri_plugin_global_shortcut::Builder::new()
                .with_shortcut("ctrl+alt+g")
                .map(|builder| {
                    builder
                        .with_handler(|app, _shortcut, event| {
                            if event.state == ShortcutState::Pressed {
                                toggle_command_bar(app);
                            }
                        })
                        .build()
                })
                .unwrap_or_else(|_| {
                    tauri_plugin_global_shortcut::Builder::<tauri::Wry>::new().build()
                }),
        )
        .setup(|app| {
            // Pin LiteRT python/worker env for every sidecar this process
            // spawns (tray and hotkey actions included).
            let _ = glint::glint_ensure_runtime(app.handle().clone());
            // Frosted glass behind both frameless windows (default theme
            // tint; the frontend re-tints on every theme change).
            let (r, g, b, a) = glass::tuning::DEFAULT_TINT;
            glass::apply_tint(app.handle(), r, g, b, a);
            if let Err(error) = build_tray(app.handle()) {
                eprintln!("Glint tray unavailable: {error}");
            }
            // The dashboard starts hidden in the tray (ports OnLaunched).
            if let Some(window) = app.get_webview_window("main") {
                let _ = window.hide();
            }
            Ok(())
        })
        .on_window_event(|window, event| {
            // Closing or minimizing the dashboard hides it to the tray.
            if window.label() == "main" {
                match event {
                    WindowEvent::CloseRequested { api, .. } => {
                        if !ALLOW_EXIT.load(Ordering::SeqCst) {
                            api.prevent_close();
                            let _ = window.hide();
                        }
                    }
                    WindowEvent::Resized(size) => {
                        if size.width == 0 && size.height == 0 {
                            let _ = window.hide();
                        }
                    }
                    _ => {}
                }
            }
            if window.label() == "command-bar" {
                if let WindowEvent::CloseRequested { api, .. } = event {
                    api.prevent_close();
                    let _ = window.hide();
                }
            }
        })
        .invoke_handler(tauri::generate_handler![
            glint::glint_initialize,
            glint::glint_probe,
            glint::glint_verify_storage,
            glint::glint_check_compatibility,
            glint::glint_request_borderless,
            glint::glint_search,
            glint::glint_history,
            glint::glint_import_model,
            glint::glint_start_scanning,
            glint::glint_pause_scanning,
            glint::glint_scan_state,
            glint::glint_capture_once,
            glint::glint_open_search,
            glint::glint_show_main,
            glint::glint_ensure_runtime,
            glint::glint_setup_runtime,
            glint::glint_set_glass_tint,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
