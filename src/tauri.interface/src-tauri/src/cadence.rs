//! When to capture.
//!
//! The scan loop used to fire on a fixed 1 s tick regardless of what the user
//! was doing, so a window left open produced the same load as active work and
//! an idle machine kept capturing. Capture is now driven by signals, adapted
//! from the macOS build's `CaptureTrigger.swift`: react quickly to a window
//! switch, keep up while the user is typing, tick slowly when they are only
//! reading, and stop entirely once they are away.
//!
//! The policy is a pure function so it can be tested without a desktop; the
//! loop only reads the signals and acts on the decision.

/// Ignore the first moments after a switch so alt-tabbing through windows
/// does not capture each one in passing.
pub const SWITCH_DEBOUNCE_MS: u64 = 1_500;

/// Input this recent counts as actively working.
pub const ACTIVE_INPUT_WINDOW_MS: u64 = 5_000;

/// Cadence while actively working.
pub const ACTIVE_INTERVAL_MS: u64 = 2_500;

/// Cadence while the window is up but nothing is being typed.
pub const PASSIVE_INTERVAL_MS: u64 = 60_000;

/// No input for this long means the user is away; capture stops.
pub const IDLE_AFTER_MS: u64 = 300_000;

/// Upper bound on a single wait, so the loop re-reads signals regularly and
/// stays responsive to a pause.
pub const POLL_INTERVAL_MS: u64 = 500;

/// On battery every interval doubles.
pub const BATTERY_MULTIPLIER: u64 = 2;

/// A look waits for this much stillness in the keyboard and mouse. Reading a
/// window goes through its accessibility interface, which Chromium-based apps
/// (Steam, Discord, VS Code) answer on the same thread that scrolls and
/// paints: a look in the middle of a scroll or a drag is felt as a hitch.
/// Typing and scrolling both pause often, so a look still lands within a
/// moment of the work.
pub const INPUT_SETTLE_MS: u64 = 1_200;

/// A gesture that never pauses (a long scroll, a game held down) still gets
/// its look this long after it became due.
pub const LONGEST_DEFER_MS: u64 = 15_000;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct CaptureSignals {
    /// Milliseconds since the last keyboard or mouse input.
    pub idle_ms: u64,
    /// Milliseconds since the last capture; `None` before the first one.
    pub since_last_scan_ms: Option<u64>,
    /// Milliseconds since the foreground window last changed, or `None` if it
    /// has not changed since the last capture.
    pub since_foreground_change_ms: Option<u64>,
    pub on_battery: bool,
    /// The foreground is a game or video whose screen was still moving at
    /// the last look: a cutscene, a controller, a lecture. No keyboard or
    /// mouse input then does not mean the user left.
    pub screen_active: bool,
    /// The foreground is a game or a video. Input there is play, not work:
    /// the slow cadence applies, so a game is not interrupted by a capture
    /// every few seconds and a video is not re-read. A switch still
    /// captures at once.
    pub visual_foreground: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CaptureReason {
    /// The user moved to a different window.
    WindowSwitch,
    /// The user is typing or clicking.
    Active,
    /// The window is up but idle-ish; a slow refresh.
    Passive,
    /// Nothing captured yet in this run.
    First,
}

impl CaptureReason {
    pub fn as_str(self) -> &'static str {
        match self {
            CaptureReason::WindowSwitch => "window-switch",
            CaptureReason::Active => "active",
            CaptureReason::Passive => "passive",
            CaptureReason::First => "first",
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CaptureDecision {
    Capture(CaptureReason),
    /// The user is away: record it once and stop capturing until they return.
    Idle,
    /// Nothing to do yet; re-read the signals after this long.
    Wait(u64),
}

pub fn decide(signals: CaptureSignals) -> CaptureDecision {
    // Away beats everything, including a pending window switch: a window that
    // changes while nobody is at the machine is not the user working.
    if signals.idle_ms >= IDLE_AFTER_MS && !signals.screen_active {
        return CaptureDecision::Idle;
    }

    let scale = if signals.on_battery { BATTERY_MULTIPLIER } else { 1 };

    // A switch wins over the interval, once it has settled.
    if let Some(since_change) = signals.since_foreground_change_ms {
        if since_change >= SWITCH_DEBOUNCE_MS {
            if let Some(wait) = settle(signals.idle_ms, since_change - SWITCH_DEBOUNCE_MS) {
                return CaptureDecision::Wait(wait);
            }
            return CaptureDecision::Capture(CaptureReason::WindowSwitch);
        }

        return CaptureDecision::Wait(
            (SWITCH_DEBOUNCE_MS - since_change).min(POLL_INTERVAL_MS),
        );
    }

    let Some(since_last_scan) = signals.since_last_scan_ms else {
        return CaptureDecision::Capture(CaptureReason::First);
    };

    let working = signals.idle_ms < ACTIVE_INPUT_WINDOW_MS && !signals.visual_foreground;
    let (interval, reason) = if working {
        (ACTIVE_INTERVAL_MS * scale, CaptureReason::Active)
    } else {
        (PASSIVE_INTERVAL_MS * scale, CaptureReason::Passive)
    };

    if since_last_scan >= interval {
        if let Some(wait) = settle(signals.idle_ms, since_last_scan - interval) {
            return CaptureDecision::Wait(wait);
        }
        return CaptureDecision::Capture(reason);
    }

    CaptureDecision::Wait((interval - since_last_scan).min(POLL_INTERVAL_MS))
}

/// How long a due look should still wait for the user's input to pause, or
/// `None` to look now: input is still, or the look is overdue.
fn settle(idle_ms: u64, overdue_ms: u64) -> Option<u64> {
    if idle_ms >= INPUT_SETTLE_MS || overdue_ms >= LONGEST_DEFER_MS {
        return None;
    }
    Some((INPUT_SETTLE_MS - idle_ms).clamp(50, POLL_INTERVAL_MS))
}

// ---------------------------------------------------------------------------
// Signal sources
// ---------------------------------------------------------------------------

use windows::Win32::System::Power::{GetSystemPowerStatus, SYSTEM_POWER_STATUS};
use windows::Win32::System::SystemInformation::GetTickCount;
use windows::Win32::UI::Input::KeyboardAndMouse::{GetLastInputInfo, LASTINPUTINFO};
use windows::Win32::UI::WindowsAndMessaging::{GetForegroundWindow, GetWindowTextW};

/// Milliseconds since the last keyboard or mouse input, system-wide.
/// Unknown reads as active, so a failing API can never silently stop capture.
pub fn input_idle_ms() -> u64 {
    unsafe {
        let mut info = LASTINPUTINFO {
            cbSize: std::mem::size_of::<LASTINPUTINFO>() as u32,
            dwTime: 0,
        };
        if GetLastInputInfo(&mut info).as_bool() {
            // GetTickCount wraps about every 49 days; wrapping_sub keeps the
            // difference right across the wrap.
            GetTickCount().wrapping_sub(info.dwTime) as u64
        } else {
            0
        }
    }
}

/// True only when the machine is definitely running on battery. Unknown reads
/// as mains power, so a desktop without a battery keeps the normal cadence.
pub fn on_battery() -> bool {
    unsafe {
        let mut status = SYSTEM_POWER_STATUS::default();
        GetSystemPowerStatus(&mut status).is_ok() && status.ACLineStatus == 0
    }
}

/// The foreground window handle, used only to notice that it changed.
pub fn foreground_handle() -> isize {
    unsafe { GetForegroundWindow().0 as isize }
}

/// The foreground window title, used only to notice in-app navigation inside
/// one unchanged window (Discord server hop, browser tab). Empty when there
/// is no foreground window or the title cannot be read; callers treat empty
/// as "no information" rather than a change, so a transient failed read can
/// never fake a navigation event.
pub fn foreground_title() -> String {
    unsafe {
        let hwnd = GetForegroundWindow();
        let mut buf = [0u16; 512];
        let read = GetWindowTextW(hwnd, &mut buf).max(0) as usize;
        String::from_utf16_lossy(&buf[..read.min(buf.len())])
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn signals() -> CaptureSignals {
        CaptureSignals {
            idle_ms: 0,
            since_last_scan_ms: Some(0),
            since_foreground_change_ms: None,
            on_battery: false,
            screen_active: false,
            visual_foreground: false,
        }
    }

    #[test]
    fn games_and_video_keep_the_slow_cadence_while_input_flows() {
        let playing = CaptureSignals {
            idle_ms: 50,
            since_last_scan_ms: Some(ACTIVE_INTERVAL_MS * 4),
            visual_foreground: true,
            ..signals()
        };
        assert!(matches!(decide(playing), CaptureDecision::Wait(_)));

        let switched = CaptureSignals {
            since_foreground_change_ms: Some(SWITCH_DEBOUNCE_MS),
            idle_ms: INPUT_SETTLE_MS,
            ..playing
        };
        assert_eq!(
            decide(switched),
            CaptureDecision::Capture(CaptureReason::WindowSwitch)
        );
    }

    #[test]
    fn a_moving_game_or_video_is_not_away() {
        // Hands off for a cutscene: still there, checked at the slow cadence.
        let watching = CaptureSignals {
            idle_ms: IDLE_AFTER_MS + 60_000,
            since_last_scan_ms: Some(PASSIVE_INTERVAL_MS),
            screen_active: true,
            ..signals()
        };
        assert_eq!(
            decide(watching),
            CaptureDecision::Capture(CaptureReason::Passive)
        );

        let still = CaptureSignals {
            screen_active: false,
            ..watching
        };
        assert_eq!(decide(still), CaptureDecision::Idle);
    }

    #[test]
    fn captures_immediately_when_nothing_has_been_captured_yet() {
        let decision = decide(CaptureSignals {
            since_last_scan_ms: None,
            ..signals()
        });

        assert_eq!(decision, CaptureDecision::Capture(CaptureReason::First));
    }

    #[test]
    fn an_away_user_stops_capture_entirely() {
        let decision = decide(CaptureSignals {
            idle_ms: IDLE_AFTER_MS,
            since_last_scan_ms: Some(u64::MAX / 2),
            ..signals()
        });

        assert_eq!(decision, CaptureDecision::Idle);
    }

    #[test]
    fn away_beats_a_pending_window_switch() {
        // A window changing while nobody is there is not the user working.
        let decision = decide(CaptureSignals {
            idle_ms: IDLE_AFTER_MS + 1,
            since_foreground_change_ms: Some(SWITCH_DEBOUNCE_MS + 1),
            ..signals()
        });

        assert_eq!(decision, CaptureDecision::Idle);
    }

    #[test]
    fn a_settled_window_switch_captures() {
        let decision = decide(CaptureSignals {
            since_foreground_change_ms: Some(SWITCH_DEBOUNCE_MS),
            idle_ms: INPUT_SETTLE_MS,
            ..signals()
        });

        assert_eq!(
            decision,
            CaptureDecision::Capture(CaptureReason::WindowSwitch)
        );
    }

    #[test]
    fn alt_tabbing_through_windows_waits_out_the_debounce() {
        let decision = decide(CaptureSignals {
            since_foreground_change_ms: Some(200),
            ..signals()
        });

        match decision {
            CaptureDecision::Wait(ms) => assert!(ms <= POLL_INTERVAL_MS && ms > 0),
            other => panic!("expected a wait, got {other:?}"),
        }
    }

    #[test]
    fn typing_keeps_the_fast_cadence_at_its_pauses() {
        let decision = decide(CaptureSignals {
            idle_ms: INPUT_SETTLE_MS,
            since_last_scan_ms: Some(ACTIVE_INTERVAL_MS),
            ..signals()
        });

        assert_eq!(decision, CaptureDecision::Capture(CaptureReason::Active));
    }

    #[test]
    fn a_look_never_lands_in_the_middle_of_a_scroll() {
        // Input 100 ms ago: wait for the pause, a little at a time.
        let scrolling = CaptureSignals {
            idle_ms: 100,
            since_last_scan_ms: Some(ACTIVE_INTERVAL_MS),
            ..signals()
        };
        match decide(scrolling) {
            CaptureDecision::Wait(ms) => assert!(ms > 0 && ms <= POLL_INTERVAL_MS),
            other => panic!("expected a wait, got {other:?}"),
        }

        // The same for a fresh window switch.
        let switched = CaptureSignals {
            since_foreground_change_ms: Some(SWITCH_DEBOUNCE_MS),
            ..scrolling
        };
        assert!(matches!(decide(switched), CaptureDecision::Wait(_)));

        // A scroll that never pauses still gets its look, late.
        let endless = CaptureSignals {
            since_last_scan_ms: Some(ACTIVE_INTERVAL_MS + LONGEST_DEFER_MS),
            ..scrolling
        };
        assert_eq!(decide(endless), CaptureDecision::Capture(CaptureReason::Active));
    }

    #[test]
    fn reading_uses_the_slow_cadence() {
        // Past the active interval but still reading: no capture yet.
        let reading = CaptureSignals {
            idle_ms: ACTIVE_INPUT_WINDOW_MS + 1,
            since_last_scan_ms: Some(ACTIVE_INTERVAL_MS * 4),
            ..signals()
        };
        assert!(matches!(decide(reading), CaptureDecision::Wait(_)));

        let overdue = CaptureSignals {
            since_last_scan_ms: Some(PASSIVE_INTERVAL_MS),
            ..reading
        };
        assert_eq!(
            decide(overdue),
            CaptureDecision::Capture(CaptureReason::Passive)
        );
    }

    #[test]
    fn battery_doubles_every_interval() {
        let typing_on_battery = CaptureSignals {
            idle_ms: INPUT_SETTLE_MS,
            since_last_scan_ms: Some(ACTIVE_INTERVAL_MS),
            on_battery: true,
            ..signals()
        };
        assert!(matches!(decide(typing_on_battery), CaptureDecision::Wait(_)));

        let doubled = CaptureSignals {
            since_last_scan_ms: Some(ACTIVE_INTERVAL_MS * BATTERY_MULTIPLIER),
            ..typing_on_battery
        };
        assert_eq!(
            decide(doubled),
            CaptureDecision::Capture(CaptureReason::Active)
        );
    }

    #[test]
    fn the_win32_signal_readers_work() {
        // Values depend on the machine, so only sanity-check them; the point
        // is that the FFI signatures are right and nothing panics.
        let idle = input_idle_ms();
        assert!(idle < 7 * 24 * 60 * 60 * 1000, "implausible idle_ms: {idle}");
        let _ = on_battery();
        let _ = foreground_handle();
        // Must never panic, even with no foreground window; empty means "no
        // information" and the scan loop ignores it.
        let _ = foreground_title();
    }

    #[test]
    fn waits_are_bounded_so_a_pause_is_noticed_quickly() {
        let decision = decide(CaptureSignals {
            idle_ms: ACTIVE_INPUT_WINDOW_MS + 1,
            since_last_scan_ms: Some(0),
            ..signals()
        });

        assert_eq!(decision, CaptureDecision::Wait(POLL_INTERVAL_MS));
    }
}
