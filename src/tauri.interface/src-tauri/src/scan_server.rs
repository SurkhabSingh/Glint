//! The capture worker: one `scan-serve` process that stays up while Glint
//! runs, with the encrypted database opened once. Looks, window-switch
//! privacy checks and markers are requests to it instead of a new process
//! each (which cost a process start and a database unlock every time:
//! ~650 ms a look before, ~55 ms with the worker).
//!
//! One request at a time, one JSON line each way. Every request carries an
//! id and every reply echoes it, so a reply to a request that was abandoned
//! (a look cancelled by Pause) is recognized and skipped instead of being
//! taken as the answer to the next one. A worker that dies or hangs is
//! killed and replaced on the next request.

use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

use tauri::{AppHandle, Manager};
use tokio::io::{AsyncBufReadExt, AsyncWriteExt};

/// Long enough for a look in a slow app (accessibility walk plus OCR),
/// short enough that a hung app cannot stall recording for long.
pub const SCAN_TIMEOUT: Duration = Duration::from_secs(20);
pub const QUICK_TIMEOUT: Duration = Duration::from_secs(5);

#[derive(Default)]
pub struct ScanServer {
    process: tokio::sync::Mutex<Option<ScanProcess>>,
    next_id: AtomicU64,
}

struct ScanProcess {
    child: tokio::process::Child,
    stdin: tokio::process::ChildStdin,
    stdout: tokio::io::Lines<tokio::io::BufReader<tokio::process::ChildStdout>>,
}

fn spawn(app: &AppHandle) -> Result<ScanProcess, String> {
    let root = crate::bridge::data_root()?;
    let cli = crate::bridge::sidecar_path(app)?;
    let mut args = vec!["scan-serve".to_string()];
    args.extend(crate::bridge::db_args(app, &root));
    args.extend(crate::bridge::host_args());
    let mut child = tokio::process::Command::new(&cli)
        .args(&args)
        .creation_flags(crate::bridge::CREATE_NO_WINDOW)
        .stdin(std::process::Stdio::piped())
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::null())
        .kill_on_drop(true)
        .spawn()
        .map_err(|error| format!("Could not start the capture worker: {error}"))?;
    let stdin = child.stdin.take().ok_or("The capture worker has no input.")?;
    let stdout = child.stdout.take().ok_or("The capture worker has no output.")?;
    Ok(ScanProcess {
        child,
        stdin,
        stdout: tokio::io::BufReader::new(stdout).lines(),
    })
}

/// Send one request (`op` plus its fields) and wait for its reply's
/// `result`. Starts the worker if needed, and replaces a dead one once.
pub async fn request(
    app: &AppHandle,
    mut request: serde_json::Value,
    timeout: Duration,
) -> Result<serde_json::Value, String> {
    let server = app.state::<ScanServer>();
    let mut guard = server.process.lock().await;
    let id = server.next_id.fetch_add(1, Ordering::SeqCst) + 1;
    request["id"] = serde_json::Value::from(id);
    request["testMode"] = serde_json::Value::String(crate::glint::current_test_mode());
    let line = format!("{request}\n");

    for _ in 0..2 {
        if guard.is_none() {
            *guard = Some(spawn(app)?);
        }
        let process = guard.as_mut().expect("just set");
        let sent = async {
            process.stdin.write_all(line.as_bytes()).await?;
            process.stdin.flush().await
        }
        .await;
        if sent.is_ok() {
            let reply = tokio::time::timeout(timeout, async {
                // Skip replies to abandoned requests until ours arrives.
                while let Ok(Some(text)) = process.stdout.next_line().await {
                    let Ok(value) = serde_json::from_str::<serde_json::Value>(&text) else {
                        continue;
                    };
                    if value.get("id").and_then(|v| v.as_u64()) == Some(id) {
                        return Some(value);
                    }
                }
                None
            })
            .await;
            match reply {
                Ok(Some(value)) => {
                    return match value.get("error").and_then(|v| v.as_str()) {
                        Some(error) => Err(error.to_string()),
                        None => Ok(value.get("result").cloned().unwrap_or(serde_json::Value::Null)),
                    };
                }
                Ok(None) => {} // the worker exited: replace it below
                Err(_) => {
                    // Hung (an app not answering accessibility calls): replace it.
                    if let Some(mut stuck) = guard.take() {
                        let _ = stuck.child.kill().await;
                    }
                    return Err("The capture worker did not answer in time.".to_string());
                }
            }
        }
        if let Some(mut dead) = guard.take() {
            let _ = dead.child.kill().await;
        }
    }
    Err("The capture worker stopped unexpectedly.".to_string())
}

/// For plain threads (the timeline pump, Windows event threads): blocks
/// until the reply. Never call from the async runtime's own threads.
pub fn request_blocking(
    app: &AppHandle,
    request: serde_json::Value,
    timeout: Duration,
) -> Result<serde_json::Value, String> {
    tauri::async_runtime::block_on(self::request(app, request, timeout))
}
