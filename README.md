# Glint Windows Phase 0

Glint is a local-first desktop memory assistant. This workspace is the isolated
Windows feasibility implementation for focused-window capture, privacy gating,
encrypted storage, local Gemma 4 inference, and 768-dimensional embeddings.

This directory is intentionally separate from the macOS repository and does not
contain a `.git` directory. It can become a new branch or repository only after
the Phase 0 results are accepted.

## Implemented

- Foreground HWND, process, title, bounds, elevation, desktop, minimized, and
  display-affinity inspection.
- Fail-closed privacy gate before UI Automation text or pixel capture.
- Bounded Microsoft UI Automation traversal and password-field detection.
- One-frame Windows Graphics Capture through `CreateForWindow(HWND)`.
- Hardware D3D11 capture with automatic WARP software fallback.
- In-memory Windows OCR with no image encoding or image files.
- Built-in machine compatibility report in the app and the CLI.
- User-controlled continuous scanning from the app.
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
| `src/tauri.interface` | The app: Tauri host (window, tray, hotkeys, when to look, Windows events) and the React screens |
| `Glint.Phase0.Cli` | `serve`, the backend the app runs; plus diagnostics that read no store |
| `Glint.Phase0.Core` | Capture, privacy, redaction, encrypted storage, activities, summaries, Ask, LiteRT worker client |
| `Glint.Phase0.EmbeddingProbe` | Windows ML and Nomic embedding feasibility proof |
| `Glint.Phase0.Tests` | Privacy, capture, grouping, storage, upgrade and model lifecycle tests |

## How it fits together (Phase 1)

- **One backend process.** The app starts `Glint.Phase0.Cli serve` once and
  sends it every request over one pipe (JSON lines, many in flight at once):
  looks, window switches, markers, every screen's reads, Ask. It is the only
  process that opens the store, through one writing connection; reads use
  their own read-only connections. The local model is loaded once and shared
  by Ask and summaries.
- **Store what happened, work out the rest.** The store keeps looks, the
  distinct lines each page showed (`content_chunks`, searchable), the focus
  log (every stretch a window spent in front), markers (start, stop, away,
  lock, app closed), and what cannot be worked out again: AI summaries, app
  modes, and the user's verdicts on tasks and sessions. Sessions and
  activities are computed when a screen asks for them (`ActivityView`), so
  nothing is sealed, rebuilt or recovered.
- **Summaries while you are away.** Finished reading activities are
  described by the local model when the user is idle (or not recording),
  never while a game or video is in front, and each description is kept.
- **Upgrading** an older store (version 15) first copies it to
  `memory.db.pre-v15.bak`, moves its text into page lines and its summaries
  and verdicts into the tables that keep them, and imports the old
  `timeline\` files into the focus log (the folder is kept as
  `timeline.imported`).

## Quick Verification

```powershell
.\scriptserify-phase0.ps1
```

To skip the multi-gigabyte inference checks:

```powershell
.\scriptserify-phase0.ps1 -SkipInference
```

## Run

```powershell
dotnet build .\src\Glint.Phase0.Cli -c Release
cd .\src\tauri.interface
npm install
npm run tauri dev
```

The continuous scanner auto-discovers the verified Phase 0 Python runtime and
model from this workspace. Production distribution still requires the owned
C++ LiteRT worker.

The history is encrypted under `%LOCALAPPDATA%\Glint\Phase0`. The backend can
also be driven by hand, e.g.
`'{"id":1,"op":"activities","limit":20}' | Glint.Phase0.Cli serve`.

Machine and live-target reports:

```powershell
.\scripts\export-compatibility-report.ps1
.\scripts
un-live-compatibility.ps1
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
