//! LiteRT runtime auto-provisioning.
//!
//! `LiteRtRuntimeLocator` (Core) looks for, in order: `%GLINT_LITERT_PYTHON%`
//! then `%dataRoot%\runtime\python\python.exe` then a workspace
//! `tools/litert/.venv`; worker `…/worker.py` similarly; model from
//! `%GLINT_GEMMA_MODEL%`, the `models/active.json` pointer, then a workspace
//! `models/upstream/…` copy.
//!
//! This module makes "select a model → scanning works" true without manual
//! setup: it normalizes the saved model pointer, discovers an existing
//! Python that can already `import litert_lm` (or a base interpreter to
//! build one from), pins the findings into process env vars that the
//! sidecar locator reads first, and can create the isolated venv + pip
//! install on demand (`glint_setup_runtime`).

use std::path::{Path, PathBuf};
use tauri::{AppHandle, Emitter, Manager};

pub const MODEL_ID: &str = "gemma-4-e2b";
const VENV_DIR_NAME: &str = "python-env";
const RECEIPT_NAME: &str = ".glint-provision-receipt.json";

// ---------------------------------------------------------------------------
// Paths
// ---------------------------------------------------------------------------

/// Strip the `\\?\` verbatim prefix that `std::fs::canonicalize` produces.
/// The Core locator, the worker argv, and the UI all expect plain paths.
pub fn normalize_windows_path(path: &Path) -> PathBuf {
    let text = path.to_string_lossy();
    if let Some(rest) = text.strip_prefix(r"\\?\UNC\") {
        return PathBuf::from(format!(r"\\{}", rest.replace('/', r"\")));
    }
    if let Some(rest) = text.strip_prefix(r"\\?\") {
        return PathBuf::from(rest);
    }
    path.to_path_buf()
}

/// Absolute, cleaned path without touching the filesystem.
pub fn absolute_normalized(path: &Path) -> PathBuf {
    match std::path::absolute(path) {
        Ok(absolute) => normalize_windows_path(&absolute),
        Err(_) => normalize_windows_path(path),
    }
}

fn first_existing(candidates: &[PathBuf]) -> Option<PathBuf> {
    candidates
        .iter()
        .find(|path| path.is_file())
        .map(|path| normalize_windows_path(path))
}

fn ancestors_of(start: &Path) -> Vec<PathBuf> {
    let mut out = Vec::new();
    let mut current = if start.is_file() {
        start.parent().map(|p| p.to_path_buf())
    } else {
        Some(start.to_path_buf())
    };
    while let Some(dir) = current {
        out.push(dir.clone());
        current = dir.parent().map(|p| p.to_path_buf());
    }
    out
}

fn venv_python(venv_dir: &Path) -> PathBuf {
    venv_dir.join("Scripts").join("python.exe")
}

fn workspace_roots(app: &AppHandle) -> Vec<PathBuf> {
    let mut roots = Vec::new();
    if let Ok(exe) = std::env::current_exe() {
        roots.extend(ancestors_of(&exe));
    }
    if let Ok(resources) = app.path().resource_dir() {
        roots.extend(ancestors_of(&resources));
    }
    roots.push(PathBuf::from(env!("CARGO_MANIFEST_DIR")));
    roots
}

// ---------------------------------------------------------------------------
// Active-model pointer (models/active.json)
// ---------------------------------------------------------------------------

fn pointer_path(root: &Path) -> PathBuf {
    root.join("models").join("active.json")
}

fn read_pointer_model(root: &Path) -> Option<String> {
    let text = std::fs::read_to_string(pointer_path(root)).ok()?;
    serde_json::from_str::<serde_json::Value>(&text)
        .ok()?
        .get("path")
        .and_then(|v| v.as_str())
        .map(|s| s.to_string())
}

/// Rewrite a verbatim (`\\?\…`) pointer to a plain absolute path,
/// preserving every other field. Returns true when the file was rewritten.
fn normalize_pointer(root: &Path) -> bool {
    let path = pointer_path(root);
    let text = match std::fs::read_to_string(&path) {
        Ok(text) => text,
        Err(_) => return false,
    };
    let mut value: serde_json::Value = match serde_json::from_str(&text) {
        Ok(value) => value,
        Err(_) => return false,
    };
    let needs_fix = value
        .get("path")
        .and_then(|v| v.as_str())
        .map(|p| p.starts_with(r"\\?\"))
        .unwrap_or(false);
    if !needs_fix {
        return false;
    }
    if let Some(model) = value.get_mut("path") {
        let fixed = normalize_windows_path(Path::new(model.as_str().unwrap_or("")));
        *model = serde_json::Value::String(fixed.to_string_lossy().into_owned());
    }
    std::fs::write(&path, serde_json::to_string_pretty(&value).unwrap_or(text)).is_ok()
}

// ---------------------------------------------------------------------------
// Python discovery
// ---------------------------------------------------------------------------

fn run_quiet(program: &Path, args: &[&str]) -> Option<String> {
    let output = std::process::Command::new(program).args(args).output().ok()?;
    if !output.status.success() {
        return None;
    }
    Some(String::from_utf8_lossy(&output.stdout).trim().to_string())
}

fn can_import_litert(python: &Path) -> bool {
    run_quiet(python, &["-c", "import litert_lm"]).is_some()
}

fn path_value(path: &Option<PathBuf>) -> serde_json::Value {
    path.as_ref()
        .map(|p| serde_json::Value::String(p.to_string_lossy().into_owned()))
        .unwrap_or(serde_json::Value::Null)
}

fn str_value(text: &Option<String>) -> serde_json::Value {
    text.as_ref()
        .map(|s| serde_json::Value::String(s.clone()))
        .unwrap_or(serde_json::Value::Null)
}

fn receipt_path(root: &Path) -> PathBuf {
    root.join("runtime").join(VENV_DIR_NAME).join(RECEIPT_NAME)
}

fn receipt_trusted_python(root: &Path) -> Option<PathBuf> {
    if receipt_path(root).is_file() {
        let python = venv_python(&root.join("runtime").join(VENV_DIR_NAME));
        if python.is_file() {
            return Some(normalize_windows_path(&python));
        }
    }
    None
}

/// Base interpreters able to `python -m venv`: (program, extra args).
fn base_interpreters() -> Vec<(String, Vec<String>)> {
    vec![
        ("py".to_string(), vec!["-3".to_string()]),
        ("py".to_string(), vec![]),
        ("python3".to_string(), vec![]),
        ("python".to_string(), vec![]),
    ]
}

fn probe_base(program: &str, extra: &[String]) -> bool {
    let mut args: Vec<&str> = extra.iter().map(|s| s.as_str()).collect();
    args.push("--version");
    std::process::Command::new(program)
        .args(&args)
        .output()
        .map(|o| o.status.success())
        .unwrap_or(false)
}

/// First base interpreter that actually runs (for venv creation / messaging).
pub fn find_base_python() -> Option<(String, Vec<String>)> {
    base_interpreters()
        .into_iter()
        .find(|(program, extra)| probe_base(program, extra))
}

/// Resolve the real exe behind a launcher (`py -3` → `C:\…\python.exe`).
fn launcher_executable(program: &str, extra: &[String]) -> Option<PathBuf> {
    let mut args: Vec<&str> = extra.iter().map(|s| s.as_str()).collect();
    args.extend(["-c", "import sys; print(sys.executable)"]);
    let out = run_quiet(Path::new(program), &args)?;
    let first = out.lines().next()?.trim();
    if first.is_empty() {
        return None;
    }
    Some(normalize_windows_path(Path::new(first)))
}

/// An interpreter verified to `import litert_lm`, in locator priority order.
fn discover_verified_python(app: &AppHandle, root: &Path) -> Option<PathBuf> {
    // 1. Already pinned this session (or deliberately set by the user).
    if let Ok(pinned) = std::env::var("GLINT_LITERT_PYTHON") {
        let path = PathBuf::from(pinned.trim());
        if path.is_file() {
            return Some(normalize_windows_path(&path));
        }
    }
    // 2. Our provisioned venv (receipt-verified, no import probe needed).
    if let Some(python) = receipt_trusted_python(root) {
        return Some(python);
    }
    let mut candidates = Vec::new();
    // 3. The locator's canonical `runtime\python\python.exe` slot.
    candidates.push(root.join("runtime").join("python").join("python.exe"));
    // 4. Provisioned venv without receipt (re-verify by import).
    candidates.push(venv_python(&root.join("runtime").join(VENV_DIR_NAME)));
    // 5. Workspace venvs (dev checkouts).
    for base in workspace_roots(app) {
        for ancestor in ancestors_of(&base) {
            candidates.push(
                ancestor
                    .join("tools")
                    .join("litert")
                    .join(".venv")
                    .join("Scripts")
                    .join("python.exe"),
            );
        }
    }
    for candidate in candidates {
        if candidate.is_file() && can_import_litert(&candidate) {
            return Some(normalize_windows_path(&candidate));
        }
    }
    // 6. System interpreters that already have litert_lm installed.
    for (program, extra) in base_interpreters() {
        if let Some(exe) = launcher_executable(&program, &extra) {
            if exe.is_file() && can_import_litert(&exe) {
                return Some(exe);
            }
        }
    }
    None
}

// ---------------------------------------------------------------------------
// Worker + requirements discovery
// ---------------------------------------------------------------------------

fn discover_file(app: &AppHandle, name: &str) -> Option<PathBuf> {
    if let Ok(pinned) = std::env::var("GLINT_LITERT_WORKER") {
        if name == "worker.py" {
            let path = PathBuf::from(pinned.trim());
            if path.is_file() {
                return Some(normalize_windows_path(&path));
            }
        }
    }
    let mut candidates = Vec::new();
    if let Some(bundled) = crate::bridge::find_in_resources(app, name) {
        candidates.push(bundled);
    }
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            candidates.push(dir.join(name));
        }
    }
    for base in workspace_roots(app) {
        for ancestor in ancestors_of(&base) {
            candidates.push(ancestor.join("tools").join("litert").join(name));
        }
    }
    // The sidecar's own ancestors walk covers the dev checkout even when
    // this host process lives elsewhere.
    if let Ok(cli) = crate::bridge::sidecar_path(app) {
        for ancestor in ancestors_of(&cli) {
            candidates.push(ancestor.join("tools").join("litert").join(name));
        }
    }
    first_existing(&candidates)
}

// ---------------------------------------------------------------------------
// ensure: normalize + discover + pin env + report
// ---------------------------------------------------------------------------

/// Make the runtime ready if possible and report its state. Fast and
/// side-effect-light (writes only the pointer normalization and process
/// env); never touches the network.
pub fn ensure_runtime(app: &AppHandle) -> serde_json::Value {
    let mut actions: Vec<String> = Vec::new();
    let root = match crate::bridge::data_root() {
        Ok(root) => root,
        Err(error) => {
            return serde_json::json!({
                "ready": false,
                "missing": ["LiteRT Python runtime", "LiteRT worker", "Gemma 4 E2B model"],
                "python": serde_json::Value::Null,
                "worker": serde_json::Value::Null,
                "modelPath": serde_json::Value::Null,
                "modelId": MODEL_ID,
                "setupRequired": false,
                "setupReason": error,
                "basePython": serde_json::Value::Null,
                "actionsTaken": actions,
            });
        }
    };

    if normalize_pointer(&root) {
        actions.push("Normalized the saved model path.".to_string());
    }
    let model_path = read_pointer_model(&root)
        .map(|p| normalize_windows_path(Path::new(&p)).to_string_lossy().into_owned())
        .filter(|p| Path::new(p).is_file());

    let python = discover_verified_python(app, &root);
    if let Some(python) = &python {
        std::env::set_var("GLINT_LITERT_PYTHON", python);
        actions.push(format!("Using LiteRT Python at {}.", python.display()));
    }

    let worker = discover_file(app, "worker.py");
    if let Some(worker) = &worker {
        std::env::set_var("GLINT_LITERT_WORKER", worker);
        actions.push(format!("Using LiteRT worker at {}.", worker.display()));
    }

    let mut missing = Vec::new();
    if python.is_none() {
        missing.push("LiteRT Python runtime");
    }
    if worker.is_none() {
        missing.push("LiteRT worker");
    }
    if model_path.is_none() {
        missing.push("Gemma 4 E2B model");
    }
    let ready = missing.is_empty();

    let base = find_base_python().map(|(program, extra)| {
        let mut full = program;
        for arg in extra {
            full.push(' ');
            full.push_str(&arg);
        }
        full
    });

    let (setup_required, setup_reason) = if ready {
        (false, String::new())
    } else if model_path.is_none() {
        (
            false,
            "Select a Gemma model with “Import Gemma model…” first.".to_string(),
        )
    } else if python.is_none() && worker.is_none() {
        (
            base.is_some(),
            "No LiteRT Python runtime or worker script was found on this machine.".to_string(),
        )
    } else if python.is_none() {
        (
            base.is_some(),
            "No Python interpreter with the litert-lm package was found; one can be provisioned automatically.".to_string(),
        )
    } else {
        (
            false,
            "The LiteRT worker script is missing and could not be staged.".to_string(),
        )
    };

    serde_json::json!({
        "ready": ready,
        "missing": missing,
        "python": path_value(&python),
        "worker": path_value(&worker),
        "modelPath": str_value(&model_path),
        "modelId": MODEL_ID,
        "setupRequired": setup_required,
        "setupReason": setup_reason,
        "basePython": str_value(&base),
        "actionsTaken": actions,
    })
}

// ---------------------------------------------------------------------------
// setup: create the isolated venv + pip install (explicit user action)
// ---------------------------------------------------------------------------

fn emit_progress(app: &AppHandle, stage: &str, detail: &str) {
    let _ = app.emit(
        "runtime-setup-progress",
        serde_json::json!({ "stage": stage, "detail": detail }),
    );
}

fn tail_lines(output: &str, count: usize) -> String {
    output
        .lines()
        .rev()
        .take(count)
        .collect::<Vec<_>>()
        .into_iter()
        .rev()
        .collect::<Vec<_>>()
        .join("\n")
}

/// Provision `%dataRoot%\runtime\python-env` (venv + requirements) and
/// verify `import litert_lm`. Emits `runtime-setup-progress` events.
pub async fn setup_runtime(app: AppHandle) -> Result<serde_json::Value, String> {
    let root = crate::bridge::data_root()?;
    let venv_dir = root.join("runtime").join(VENV_DIR_NAME);
    let venv_python = venv_python(&venv_dir);

    emit_progress(&app, "python", "Looking for a Python interpreter...");
    let (program, extra) = find_base_python().ok_or_else(|| {
        "No Python 3 interpreter was found. Install Python 3.11 or later (python.org or `winget install Python.Python.3.12`), then choose “Set up local AI runtime” again.".to_string()
    })?;
    let mut base: Vec<String> = vec![program.clone()];
    base.extend(extra.clone());
    emit_progress(
        &app,
        "python",
        &format!("Using base interpreter: {}.", base.join(" ")),
    );

    if !venv_python.is_file() {
        emit_progress(
            &app,
            "venv",
            &format!("Creating isolated environment at {}...", venv_dir.display()),
        );
        let mut args: Vec<String> = extra.clone();
        args.extend(["-m".to_string(), "venv".to_string(), venv_dir.to_string_lossy().into_owned()]);
        let prog = program.clone();
        let run = tokio::task::spawn_blocking(move || {
            let arg_refs: Vec<&str> = args.iter().map(|s| s.as_str()).collect();
            std::process::Command::new(&prog).args(&arg_refs).output()
        })
        .await
        .map_err(|error| format!("Environment task failed: {error}"))?
        .map_err(|error| format!("Failed to launch {program}: {error}"))?;
        if !run.status.success() || !venv_python.is_file() {
            let detail = tail_lines(&String::from_utf8_lossy(&run.stderr), 8);
            return Err(format!("Could not create the Python environment.\n{detail}"));
        }
        emit_progress(&app, "venv", "Isolated environment created.");
    } else {
        emit_progress(&app, "venv", "Reusing the existing isolated environment.");
    }

    let requirements = discover_file(&app, "requirements.txt").ok_or_else(|| {
        "requirements.txt was not found next to the app or in the workspace; cannot install litert-lm.".to_string()
    })?;
    emit_progress(
        &app,
        "install",
        "Installing litert-lm (this downloads several hundred megabytes and takes several minutes)...",
    );
    let req = requirements.to_string_lossy().into_owned();
    let venv_py = venv_python.clone();
    let run = tokio::task::spawn_blocking(move || {
        std::process::Command::new(&venv_py)
            .args([
                "-m",
                "pip",
                "install",
                "--disable-pip-version-check",
                "-r",
                req.as_str(),
            ])
            .output()
    })
    .await
    .map_err(|error| format!("Install task failed: {error}"))?
    .map_err(|error| format!("Failed to run pip: {error}"))?;
    if !run.status.success() {
        let detail = tail_lines(
            &format!(
                "{}\n{}",
                String::from_utf8_lossy(&run.stdout),
                String::from_utf8_lossy(&run.stderr)
            ),
            12,
        );
        return Err(format!("pip could not install litert-lm.\n{detail}"));
    }
    emit_progress(&app, "install", "Packages installed. Verifying the worker import...");

    let venv_check = venv_python.clone();
    let verified = tokio::task::spawn_blocking(move || can_import_litert(&venv_check))
        .await
        .unwrap_or(false);
    if !verified {
        return Err(
            "The environment was created but `import litert_lm` still fails. Try repairing with a newer Python, then run setup again.".to_string(),
        );
    }
    let receipt = serde_json::json!({
        "python": venv_python.to_string_lossy(),
        "litertLm": true,
        "installedAt": chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Nanos, true),
    });
    let _ = std::fs::write(
        receipt_path(&root),
        serde_json::to_string_pretty(&receipt).unwrap_or_default(),
    );
    emit_progress(&app, "verify", "LiteRT worker import verified.");

    let status = ensure_runtime(&app);
    emit_progress(
        &app,
        "done",
        if status.get("ready").and_then(|v| v.as_bool()).unwrap_or(false) {
            "Local AI runtime is ready."
        } else {
            "Setup finished but the runtime is still incomplete."
        },
    );
    Ok(serde_json::json!({ "ok": status.get("ready"), "runtime": status }))
}
