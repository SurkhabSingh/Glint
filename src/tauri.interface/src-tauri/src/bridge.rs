//! Bridge between the Tauri frontend and the Phase 0 .NET backend.
//!
//! The old WinUI frontend (`Glint.Phase0.App/MainViewModel.cs`) called
//! `Glint.Phase0.Core` directly. The Tauri backend cannot link managed code,
//! so every Core operation goes through the `Glint.Phase0.Cli` sidecar: one
//! short-lived process per action, a single camelCase JSON document on stdout,
//! diagnostics/errors on stderr. Exit codes: 0 = ok, 1 = failure,
//! 2 = `compatibility --require-ready` not ready.

use std::path::PathBuf;
use tauri::{AppHandle, Manager};

/// Canonical Phase 0 data root, shared with the WinUI app and the CLI:
/// `%LOCALAPPDATA%\Glint\Phase0` (`memory.db`, `secrets/dbkey.bin`,
/// `models/active.json`). Must match `MainViewModel.GetDataRoot()`.
pub fn data_root() -> Result<PathBuf, String> {
    if let Ok(dir) = std::env::var("GLINT_DATA_DIR") {
        if !dir.trim().is_empty() {
            return Ok(PathBuf::from(dir));
        }
    }
    let local = std::env::var("LOCALAPPDATA")
        .map_err(|_| "LOCALAPPDATA is not set; cannot locate the Phase 0 data root.".to_string())?;
    Ok(PathBuf::from(local).join("Glint").join("Phase0"))
}

fn dev_cli_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("Glint.Phase0.Cli")
        .join("bin")
}

fn candidate_cli_paths(app: &AppHandle) -> Vec<PathBuf> {
    let mut paths = Vec::new();
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            paths.push(dir.join("Glint.Phase0.Cli.exe"));
            paths.push(dir.join("bin").join("Glint.Phase0.Cli.exe"));
        }
    }
    if let Ok(resources) = app.path().resource_dir() {
        paths.push(resources.join("Glint.Phase0.Cli.exe"));
        paths.push(resources.join("bin").join("Glint.Phase0.Cli.exe"));
    }
    if let Some(bundled) = find_in_resources(app, "Glint.Phase0.Cli.exe") {
        paths.push(bundled);
    }
    let dev = dev_cli_dir();
    for profile in ["Release", "Debug"] {
        paths.push(
            dev.join(profile)
                .join("net9.0-windows10.0.22621.0")
                .join("Glint.Phase0.Cli.exe"),
        );
    }
    paths
}

/// Locate the CLI sidecar, probing ship locations first and the developer
/// build output (`src/Glint.Phase0.Cli/bin/...`) as a fallback.
pub fn sidecar_path(app: &AppHandle) -> Result<PathBuf, String> {
    if let Ok(dir) = std::env::var("GLINT_CLI_PATH") {
        let custom = PathBuf::from(dir.trim());
        if custom.is_file() {
            return Ok(custom);
        }
    }
    for path in candidate_cli_paths(app) {
        if path.is_file() {
            return Ok(path);
        }
    }
    Err("Glint.Phase0.Cli.exe was not found next to the app or in the developer build output. Build it with: dotnet build src/Glint.Phase0.Cli -c Release (or set GLINT_CLI_PATH).".to_string())
}

/// Recursive resource lookup (depth ≤ 3) so bundled files are found
/// regardless of how the packager nests the resources directory.
pub fn find_in_resources(app: &AppHandle, file_name: &str) -> Option<PathBuf> {
    let resources = app.path().resource_dir().ok()?;
    let direct = resources.join(file_name);
    if direct.is_file() {
        return Some(direct);
    }
    fn walk(dir: &std::path::Path, file_name: &str, depth: u8) -> Option<PathBuf> {
        if depth == 0 {
            return None;
        }
        let entries = std::fs::read_dir(dir).ok()?;
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_file() && path.file_name().map(|n| n == file_name).unwrap_or(false) {
                return Some(path);
            }
            if path.is_dir() {
                // Skip the heavyweight .NET artifacts when hunting for data files.
                if let Some(name) = path.file_name().and_then(|n| n.to_str()) {
                    if name.eq_ignore_ascii_case("runtimes") && file_name != "vec0.dll" {
                        continue;
                    }
                }
                if let Some(found) = walk(&path, file_name, depth - 1) {
                    return Some(found);
                }
            }
        }
        None
    }
    walk(&resources, file_name, 3)
}

/// `vec0.dll` override for `--sqlite-vec`: next to the CLI first
/// (`runtimes/win-x64/native/vec0.dll`, then flat `vec0.dll`), then the
/// bundled resources.
pub fn sqlite_vec_arg(app: &AppHandle, cli: &std::path::Path) -> Option<String> {
    if let Some(dir) = cli.parent() {
        for candidate in [
            dir.join("runtimes").join("win-x64").join("native").join("vec0.dll"),
            dir.join("vec0.dll"),
        ] {
            if candidate.is_file() {
                return Some(candidate.to_string_lossy().into_owned());
            }
        }
    }
    find_in_resources(app, "vec0.dll").map(|p| p.to_string_lossy().into_owned())
}

pub struct SidecarOutput {
    pub exit_code: i32,
    pub json: serde_json::Value,
}

/// Run the sidecar once and parse its single stdout JSON document.
/// Non-zero exit codes still return parsed JSON when stdout parses (e.g.
/// exit 2 from `compatibility --require-ready`); otherwise stderr becomes
/// the error message.
pub fn run_sidecar(
    app: &AppHandle,
    args: &[&str],
) -> Result<SidecarOutput, String> {
    let cli = sidecar_path(app)?;
    let output = std::process::Command::new(&cli)
        .args(args)
        .output()
        .map_err(|error| format!("Failed to launch {}: {error}", cli.display()))?;
    let code = output.status.code().unwrap_or(-1);
    let stdout = String::from_utf8_lossy(&output.stdout);
    let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
    match serde_json::from_str::<serde_json::Value>(&stdout) {
        Ok(json) => Ok(SidecarOutput {
            exit_code: code,
            json,
        }),
        Err(_) => Err(if stderr.is_empty() {
            format!("{} exited with code {code} and no JSON output.", cli.display())
        } else {
            stderr
        }),
    }
}

/// Base args shared by every verb that opens the database.
pub fn db_args(app: &AppHandle, root: &std::path::Path) -> Vec<String> {
    let mut args = vec![
        "--data-dir".to_string(),
        root.to_string_lossy().into_owned(),
    ];
    if let Ok(cli) = sidecar_path(app) {
        if let Some(vec) = sqlite_vec_arg(app, &cli) {
            args.push("--sqlite-vec".to_string());
            args.push(vec);
        }
    }
    args
}
