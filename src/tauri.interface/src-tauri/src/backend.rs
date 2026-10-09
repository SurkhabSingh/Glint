//! The backend: one `Glint.Phase0.Cli serve` process for as long as Glint
//! runs. It owns the encrypted store, the looks and the local model; every
//! screen read, look, window switch, marker and question is a request to it.
//! Nothing else opens the store.
//!
//! One JSON line each way per request. Requests carry an id and replies echo
//! it, so many can be in flight at once: a screen's read never waits behind a
//! slow look. Lines without an id are events the backend raises on its own
//! (summaries written while the user was idle), forwarded to the screens.
//! A backend that dies is started again on the next request; one that hangs
//! on a look (an app not answering accessibility calls) is replaced.

use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use tauri::{AppHandle, Emitter, Manager};
use tokio::io::{AsyncBufReadExt, AsyncWriteExt};
use tokio::sync::oneshot;

/// Long enough for a look in a slow app (accessibility walk plus OCR),
/// short enough that a hung app cannot stall recording for long.
pub const SCAN_TIMEOUT: Duration = Duration::from_secs(20);
/// Reads, markers, window switches.
pub const QUICK_TIMEOUT: Duration = Duration::from_secs(15);
/// The first request after start can wait for a one-time store upgrade.
pub const START_TIMEOUT: Duration = Duration::from_secs(120);
/// A question may load the model first.
pub const ASK_TIMEOUT: Duration = Duration::from_secs(300);

type Pending = Arc<Mutex<HashMap<u64, oneshot::Sender<serde_json::Value>>>>;

#[derive(Default)]
pub struct Backend {
    connection: tokio::sync::Mutex<Option<Connection>>,
    next_id: AtomicU64,
}

struct Connection {
    child: tokio::process::Child,
    stdin: Arc<tokio::sync::Mutex<tokio::process::ChildStdin>>,
    pending: Pending,
}

fn spawn(app: &AppHandle) -> Result<Connection, String> {
    let root = crate::bridge::data_root()?;
    let cli = crate::bridge::sidecar_path(app)?;
    let mut args = vec!["serve".to_string()];
    args.extend(crate::bridge::db_args(app, &root));
    args.extend(crate::bridge::host_args());
    // Started again in the middle of a recording (the last one hung on a
    // look): it carries on rather than closing the run as a crash would.
    if crate::glint::scanning_now(app) {
        args.push("--recording".to_string());
    }
    let mut child = tokio::process::Command::new(&cli)
        .args(&args)
        .creation_flags(crate::bridge::CREATE_NO_WINDOW)
        .stdin(std::process::Stdio::piped())
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::null())
        .kill_on_drop(true)
        .spawn()
        .map_err(|error| format!("Could not start Glint's backend: {error}"))?;
    let stdin = child.stdin.take().ok_or("Glint's backend has no input.")?;
    let stdout = child.stdout.take().ok_or("Glint's backend has no output.")?;
    let pending: Pending = Arc::default();

    // Replies go to whoever is waiting for that id; events go to the screens.
    // When the backend exits, every waiting request hears so at once.
    let reader_pending = pending.clone();
    let reader_app = app.clone();
    tauri::async_runtime::spawn(async move {
        let mut lines = tokio::io::BufReader::new(stdout).lines();
        while let Ok(Some(line)) = lines.next_line().await {
            let Ok(message) = serde_json::from_str::<serde_json::Value>(&line) else {
                continue;
            };
            if let Some(id) = message.get("id").and_then(|v| v.as_u64()) {
                let waiter = reader_pending.lock().unwrap().remove(&id);
                if let Some(waiter) = waiter {
                    let _ = waiter.send(message);
                }
            } else if let Some(event) = message.get("event").and_then(|v| v.as_str()) {
                let data = message.get("data").cloned().unwrap_or(serde_json::Value::Null);
                if event == "sessions-updated" {
                    crate::glint::note_ai_backend(&data);
                }
                let _ = reader_app.emit(event, data);
            }
        }
        reader_pending.lock().unwrap().clear();
    });

    Ok(Connection {
        child,
        stdin: Arc::new(tokio::sync::Mutex::new(stdin)),
        pending,
    })
}

/// Send one request (`op` plus its fields) and wait for its reply's `result`.
/// Starts the backend if needed, and replaces a dead one once.
pub async fn request(
    app: &AppHandle,
    mut request: serde_json::Value,
    timeout: Duration,
) -> Result<serde_json::Value, String> {
    let backend = app.state::<Backend>();
    let id = backend.next_id.fetch_add(1, Ordering::SeqCst) + 1;
    request["id"] = serde_json::Value::from(id);
    request["testMode"] = serde_json::Value::String(crate::glint::current_test_mode());
    let is_look = request.get("op").and_then(|v| v.as_str()) == Some("scan");
    let line = format!("{request}\n");

    for _ in 0..2 {
        let (stdin, pending, receiver) = {
            let mut guard = backend.connection.lock().await;
            let dead = match guard.as_mut() {
                Some(connection) => !matches!(connection.child.try_wait(), Ok(None)),
                None => true,
            };
            if dead {
                *guard = Some(spawn(app)?);
            }
            let connection = guard.as_ref().expect("just set");
            let (sender, receiver) = oneshot::channel();
            connection.pending.lock().unwrap().insert(id, sender);
            (connection.stdin.clone(), connection.pending.clone(), receiver)
        };

        let sent = async {
            let mut stdin = stdin.lock().await;
            stdin.write_all(line.as_bytes()).await?;
            stdin.flush().await
        }
        .await;
        if sent.is_err() {
            pending.lock().unwrap().remove(&id);
            stop(app).await;
            continue;
        }

        match tokio::time::timeout(timeout, receiver).await {
            Ok(Ok(reply)) => {
                return match reply.get("error").and_then(|v| v.as_str()) {
                    Some(error) => Err(error.to_string()),
                    None => Ok(reply.get("result").cloned().unwrap_or(serde_json::Value::Null)),
                };
            }
            // The backend exited while this was waiting: start it again.
            Ok(Err(_)) => {
                stop(app).await;
                continue;
            }
            Err(_) => {
                pending.lock().unwrap().remove(&id);
                if is_look {
                    // Stuck in an app that is not answering: looks are taken
                    // one at a time, so nothing else could be looked at.
                    stop(app).await;
                }
                return Err("Glint's backend did not answer in time.".to_string());
            }
        }
    }
    Err("Glint's backend stopped unexpectedly.".to_string())
}

/// For plain threads (the window-switch pump, Windows event threads): blocks
/// until the reply. Never call from the async runtime's own threads.
pub fn request_blocking(
    app: &AppHandle,
    request: serde_json::Value,
    timeout: Duration,
) -> Result<serde_json::Value, String> {
    tauri::async_runtime::block_on(self::request(app, request, timeout))
}

/// Ends the running backend; the next request starts a new one.
async fn stop(app: &AppHandle) {
    let backend = app.state::<Backend>();
    let taken = backend.connection.lock().await.take();
    if let Some(mut connection) = taken {
        let _ = connection.child.kill().await;
    }
}

/// Starts the backend with Glint, so a store upgrade, the import of older
/// history and summaries still owed all happen before the first screen asks.
pub fn start(app: &AppHandle) {
    let app = app.clone();
    tauri::async_runtime::spawn(async move {
        if let Err(error) = request(&app, serde_json::json!({ "op": "ping" }), START_TIMEOUT).await {
            eprintln!("Glint backend unavailable: {error}");
        }
    });
}
