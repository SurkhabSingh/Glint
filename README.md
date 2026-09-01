# Glint Windows Phase 0

Glint is a local-first desktop memory assistant. This workspace is the isolated
Windows feasibility implementation for focused-window capture, privacy gating,
encrypted storage, local Gemma 4 inference, and 768-dimensional embeddings.

This directory is intentionally separate from the macOS repository and does not
contain a `.git` directory. It can become a new branch or repository only after
the Phase 0 results are accepted.

## Implemented

- Native C#/.NET 9 WinUI 3 diagnostic shell using Windows App SDK 1.8.6.
- Self-contained x64 MSIX and portable ZIP; neither requires a separate .NET
  or Windows App SDK installation.
- Foreground HWND, process, title, bounds, elevation, desktop, minimized, and
  display-affinity inspection.
- Fail-closed privacy gate before UI Automation text or pixel capture.
- Bounded Microsoft UI Automation traversal and password-field detection.
- One-frame Windows Graphics Capture through `CreateForWindow(HWND)`.
- Hardware D3D11 capture with automatic WARP software fallback.
- In-memory Windows OCR with no image encoding or image files.
- Built-in machine compatibility report in WinUI and the CLI.
- User-controlled continuous OCR scanning from the WinUI app.
- Local Gemma 4 E2B label and summary generation after privacy filtering and
  deterministic redaction.
- Conversation-aware 16,000-character context selection that retains metadata
  and prioritizes the latest visible messages.
- Important signals and potential reminder candidates for explicit
  commitments, meetings, deadlines, requests, blockers, and unresolved issues.
- Collapsible per-scan diagnostics showing redacted combined input plus
  redacted OCR-only and UI Automation-only text for prompt tuning.
- Start/Pause lifecycle with cancellation of in-flight capture and inference.
- Redacted-content deduplication before Gemma to avoid repeated summaries.
- Timestamped scan history persisted in SQLCipher and restored after relaunch.
- Whole-frame secret rejection and deterministic text redaction.
- Current-user DPAPI wrapping for the random SQLCipher key.
- SQLCipher SQLite, FTS5, and pinned sqlite-vec 0.1.9.
- Resumable and verified model install, activation, rollback, repair, and removal.
- Gemma 4 E2B generation through LiteRT-LM 0.13.1.
- Anonymous-pipe C# to Python worker spike with prompts sent through stdin.
- Nomic Embed Text v1.5 through Windows ML with normalized 768-float output.
- Automatic borderless-capture consent on supported packaged Windows builds,
  with the system indicator retained when Windows denies access.
- CLI diagnostics and 36 deterministic tests.

## Projects

| Project | Responsibility |
|---|---|
| `Glint.Phase0.Core` | Capture, privacy, redaction, encrypted storage, model lifecycle, LiteRT worker client |
| `Glint.Phase0.Cli` | Repeatable host diagnostics and smoke tests |
| `Glint.Phase0.App` | Minimal native WinUI 3 diagnostic UI and MSIX definition |
| `Glint.Phase0.EmbeddingProbe` | Windows ML and Nomic embedding feasibility proof |
| `Glint.Phase0.Tests` | Privacy, pipeline, redaction, storage, and model lifecycle tests |

## Quick Verification

```powershell
.\scripts\verify-phase0.ps1
```

To skip the multi-gigabyte inference checks:

```powershell
.\scripts\verify-phase0.ps1 -SkipInference
```

## Run Without Installing

Build the portable package, then extract and run it:

```powershell
.\scripts\build-portable.ps1
```

```text
artifacts\portable\Glint.Phase0.App-win-x64.zip
```

The portable build needs no certificate trust, .NET runtime, Windows App SDK,
Python, or model files for compatibility and storage diagnostics. The continuous
Gemma scanner currently auto-discovers the verified Phase 0 Python
runtime and model from this workspace. Production distribution still requires
the owned C++ LiteRT worker.

## Manual Scan

1. Run `Glint.Phase0.App.exe`.
2. Click **Start scanning**.
3. Switch to the target window. Glint scans changed visible content
   sequentially and keeps running.
4. Timestamped labels and summaries appear under **Scanned activity**.
5. Return to Glint and click **Pause scanning** to stop. Any in-flight capture
   or Gemma request is cancelled.

The history is encrypted under `%LOCALAPPDATA%\Glint\Phase0`.

Machine and live-target reports:

```powershell
.\scripts\export-compatibility-report.ps1
.\scripts\run-live-compatibility.ps1
```

## Privacy Invariants

1. Secure or indeterminate state suppresses capture.
2. Password state is checked before text extraction or pixel capture.
3. Raw frames remain in memory and are never encoded or persisted.
4. Whole-frame secret detection runs before persistence.
5. Deterministic redaction runs before hashing and storage.
6. Deduplication hashes redacted text only.
7. SQLCipher failure is fatal; plaintext fallback is not allowed.
8. Model inference is local and no localhost service is opened.

## Current Status

The vertical slice works on this Windows machine. Phase 0 is not exited because
two more physical machines, the actual minimum Windows build, elevated/UAC/lock
scenarios, and intermittent Calculator/UWP WGC behavior remain outstanding.
