//! Liquid-glass backdrop blur for the frameless windows.
//!
//! CSS `backdrop-filter` cannot blur the desktop behind a transparent
//! webview — it only blurs in-page content. Real backdrop blur needs the OS
//! compositor, so both windows get Windows acrylic (smooth blur + theme
//! tint) via `window_vibrancy`. The webview keeps painting its translucent
//! theme color on top, which flavors the frosted glass. If the compositor
//! refuses (old Windows build), the app silently keeps plain translucency.

use tauri::{AppHandle, Manager};

/// Glass tuning — the Rust half of the blur strength. (The CSS half lives
/// in `src/glass.css`: `--window-radius`, `--glass-titlebar-bg`,
/// `--glass-sheen-*`, `--glass-scroll-*`, `--page-padding`,
/// `--page-max-width`. Tweak both files together.)
pub mod tuning {
    /// Windows to frost. Must match the labels in `tauri.conf.json`.
    pub const WINDOWS: [&str; 2] = ["main", "command-bar"];
    /// Startup tint = default theme background (Ocean Blue #1e3c72 @ 0.55).
    /// The frontend re-tints on every theme change anyway.
    pub const DEFAULT_TINT: (u8, u8, u8, f32) = (30, 60, 114, 0.55);
    /// Acrylic tint-alpha band (0–255). The blur radius itself is
    /// system-defined; this only flavors the glass so it stays frosted —
    /// never muddy, never invisible — at any theme opacity:
    /// `BASE + themeOpacity * RANGE`.
    pub const TINT_BASE_ALPHA: f32 = 90.0;
    pub const TINT_ALPHA_RANGE: f32 = 80.0;
}

/// Acrylic tint curve: the blur itself is system-defined; the tint alpha
/// stays in a modest band so the glass reads frosted at any theme opacity.
fn tint_alpha(alpha01: f32) -> u8 {
    (tuning::TINT_BASE_ALPHA + alpha01.clamp(0.0, 1.0) * tuning::TINT_ALPHA_RANGE).round()
        as u8
}

/// (Re)apply the frosted-glass tint to the dashboard and command bar.
pub fn apply_tint(app: &AppHandle, r: u8, g: u8, b: u8, alpha01: f32) {
    let color = Some((r, g, b, tint_alpha(alpha01)));
    for label in tuning::WINDOWS {
        if let Some(window) = app.get_webview_window(label) {
            if let Err(error) = window_vibrancy::apply_acrylic(&window, color) {
                eprintln!("Glint glass unavailable on {label}: {error}");
            }
        }
    }
}
