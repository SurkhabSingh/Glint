import { useState, useEffect, useCallback, useRef } from "react";
import { listen } from "@tauri-apps/api/event";
import { open } from "@tauri-apps/plugin-dialog";
import ColorSwitcher, {
  hexToRgb,
  hexToRgba,
  loadTheme,
  presetThemes,
  presetIndexForTheme,
  sanitizeTheme,
  stripPresetName,
} from "./ColorSwitcher";
import TitlebarMenu from "./TitlebarMenu";
import NavRail from "./components/NavRail";
import CommandBar from "./components/CommandBar";
import ActivityPage from "./pages/ActivityPage";
import SearchPage from "./pages/SearchPage";
import DiagnosticsPage from "./pages/DiagnosticsPage";
import SettingsPage from "./pages/SettingsPage";
import {
  glintInitialize,
  glintSetGlassTint,
  glintProbe,
  glintVerifyStorage,
  glintCheckCompatibility,
  glintRequestBorderless,
  glintSearch,
  glintImportModel,
  glintStartScanning,
  glintPauseScanning,
  glintSetupRuntime,
  historySummaryText,
} from "./glint";
import "./App.css";
import "./Glint.css";
import "./glass.css";

function resolveWindowLabel() {
  try {
    const internals = window.__TAURI_INTERNALS__;
    const label = internals?.metadata?.currentWindow?.label;
    if (typeof label === "string" && label) return label;
  } catch {
    /* fall through to the default below */
  }
  return "main";
}

async function getTauriWindow() {
  const { getCurrentWindow } = await import("@tauri-apps/api/window");
  return getCurrentWindow();
}

const DEFAULT_SEARCH_SUMMARY =
  "Search summaries and redacted captured text stored on this machine.";

function App() {
  const [windowLabel, setWindowLabel] = useState(null);

  // The dashboard and the command bar share one bundle; the label picks
  // which shell to render. Resolved async because the Tauri JS API is ESM.
  useEffect(() => {
    let cancelled = false;
    import("@tauri-apps/api/window")
      .then(({ getCurrentWindow }) => {
        if (!cancelled) setWindowLabel(getCurrentWindow().label ?? "main");
      })
      .catch(() => {
        if (!cancelled) setWindowLabel(resolveWindowLabel());
      });
    return () => {
      cancelled = true;
    };
  }, []);
  const [theme, setTheme] = useState(loadTheme);
  const [menuPos, setMenuPos] = useState(null);
  const [panelOpen, setPanelOpen] = useState(false);
  const [page, setPage] = useState("activity");

  // Dashboard state (ports MainViewModel properties).
  const [initializing, setInitializing] = useState(true);
  const [overall, setOverall] = useState(null);
  const [compatibilitySummary, setCompatibilitySummary] =
    useState("Not checked.");
  const [storageSummary, setStorageSummary] = useState("Not checked.");
  const [modelSummary, setModelSummary] = useState(
    "Checking the local Gemma runtime...",
  );
  const [foregroundSummary, setForegroundSummary] = useState("Not checked.");
  const [captureSummary, setCaptureSummary] = useState("No capture has run.");
  const [history, setHistory] = useState([]);
  const [scanning, setScanning] = useState(false);
  const [query, setQuery] = useState("");
  const [results, setResults] = useState([]);
  const [searchSummary, setSearchSummary] = useState(DEFAULT_SEARCH_SUMMARY);
  const [runtime, setRuntime] = useState(null);
  const [settingUp, setSettingUp] = useState(false);
  const [setupLog, setSetupLog] = useState([]);

  const safeTheme = sanitizeTheme(theme);
  const windowBg = hexToRgba(safeTheme.bg, safeTheme.bgAlpha);
  const tintTimer = useRef(null);

  useEffect(() => {
    document.documentElement.style.setProperty("--window-bg", windowBg);
    document.documentElement.style.setProperty(
      "--accent-color",
      safeTheme.accent,
    );
    localStorage.setItem("glint-window-theme", JSON.stringify(safeTheme));
    // Re-tint the OS frosted-glass backdrop to match (debounced; acrylic
    // re-application is cheap but slider drags fire many renders).
    if (tintTimer.current) clearTimeout(tintTimer.current);
    tintTimer.current = setTimeout(() => {
      const { r, g, b } = hexToRgb(safeTheme.bg);
      glintSetGlassTint({ r, g, b, alpha: safeTheme.bgAlpha }).catch(() => {
        /* browser dev or glass unsupported: CSS translucency remains */
      });
    }, 120);
    return () => {
      if (tintTimer.current) clearTimeout(tintTimer.current);
    };
  }, [windowBg, safeTheme]);

  const applyOutcome = useCallback((payload) => {
    if (!payload || payload.kind === -1) {
      if (payload?.captureSummary) setCaptureSummary(payload.captureSummary);
      if (payload?.overall) setOverall(payload.overall);
      return;
    }
    setCaptureSummary(payload.captureSummary);
    setOverall(payload.overall);
    if (payload.record) {
      setHistory((prev) => [payload.record, ...prev].slice(0, 50));
    }
  }, []);

  const applyScanState = useCallback((state) => {
    if (!state) return;
    if (typeof state.scanning === "boolean") setScanning(state.scanning);
    if (state.captureSummary) setCaptureSummary(state.captureSummary);
    if (state.overall) setOverall(state.overall);
  }, []);

  const runSearch = useCallback(async (text) => {
    try {
      const response = await glintSearch(text);
      setResults(response.results ?? []);
      setSearchSummary(response.searchSummary);
    } catch (error) {
      setResults([]);
      setSearchSummary(`Search failed: ${String(error)}`);
    }
  }, []);

  // Bootstrap (ports InitializeAsync + LoadScanHistory).
  useEffect(() => {
    if (windowLabel !== "main") return;
    let cancelled = false;
    glintInitialize()
      .then((init) => {
        if (cancelled) return;
        setOverall(init.overall ?? null);
        setCompatibilitySummary(init.compatibilitySummary ?? "Not checked.");
        setStorageSummary(init.storageSummary ?? "Not checked.");
        setModelSummary(init.modelSummary ?? "");
        setForegroundSummary(init.foregroundSummary ?? "");
        setHistory(init.history ?? []);
        setRuntime(init.runtime ?? null);
        setInitializing(false);
      })
      .catch((error) => {
        if (cancelled) return;
        setOverall({
          title: "Check failed",
          message: `Glint bridge unavailable: ${String(error)}`,
          severity: "error",
        });
        setInitializing(false);
      });
    return () => {
      cancelled = true;
    };
  }, [windowLabel]);

  // Bridge + shell events.
  useEffect(() => {
    if (windowLabel !== "main") return;
    let unlistenOutcome;
    let unlistenState;
    let unlistenSearch;
    listen("scan-outcome", (event) => applyOutcome(event.payload)).then(
      (fn) => (unlistenOutcome = fn),
    );
    listen("scan-state", (event) => applyScanState(event.payload)).then(
      (fn) => (unlistenState = fn),
    );
    listen("open-search", (event) => {
      const next = event.payload?.query ?? null;
      setPage("search");
      if (next && next.trim()) {
        setQuery(next);
        runSearch(next);
      } else {
        setQuery("");
        setResults([]);
        setSearchSummary("Enter a person, application, topic, or phrase.");
      }
    }).then((fn) => (unlistenSearch = fn));
    return () => {
      unlistenOutcome?.();
      unlistenState?.();
      unlistenSearch?.();
    };
  }, [windowLabel, applyOutcome, applyScanState, runSearch]);

  const minimize = useCallback(async () => {
    try {
      (await getTauriWindow()).minimize();
    } catch {
      /* not running in Tauri (browser dev) */
    }
  }, []);

  const toggleMaximize = useCallback(async () => {
    try {
      (await getTauriWindow()).toggleMaximize();
    } catch {
      /* not running in Tauri (browser dev) */
    }
  }, []);

  const close = useCallback(async () => {
    try {
      (await getTauriWindow()).close();
    } catch {
      /* not running in Tauri (browser dev) */
    }
  }, []);

  function openTitlebarMenu(e) {
    e.preventDefault();
    setMenuPos({ x: e.clientX, y: e.clientY });
  }

  // Capture-card actions (ports Start/Pause/Borderless/Import).
  async function handleStart() {
    try {
      applyScanState(await glintStartScanning());
    } catch (error) {
      setOverall({
        title: "Check failed",
        message: String(error),
        severity: "error",
      });
    }
  }

  async function handlePause() {
    try {
      applyScanState(await glintPauseScanning());
    } catch (error) {
      setOverall({
        title: "Check failed",
        message: String(error),
        severity: "error",
      });
    }
  }

  async function handleSetupRuntime() {
    if (settingUp) return;
    setSettingUp(true);
    setSetupLog([]);
    let unlisten;
    try {
      unlisten = await listen("runtime-setup-progress", (event) => {
        const stage = event.payload?.stage ?? "setup";
        const detail = event.payload?.detail ?? "";
        setSetupLog((prev) => [...prev.slice(-29), `${stage}: ${detail}`]);
      });
      const response = await glintSetupRuntime();
      const next = response.runtime ?? null;
      setRuntime(next);
      if (next?.ready && next?.modelPath) {
        setModelSummary(
          `Gemma 4 E2B is ready through the local LiteRT worker. Model: ${next.modelPath}`,
        );
        setOverall({
          title: "Check completed",
          message:
            "Local AI runtime is ready. Start scanning to capture context.",
          severity: "success",
        });
      } else if (next && !next.ready) {
        setOverall({
          title: "Check failed",
          message: `Setup finished but the runtime is still incomplete: ${(next.missing ?? []).join(", ")}.`,
          severity: "error",
        });
      }
    } catch (error) {
      const message = String(error?.message ?? error);
      setSetupLog((prev) => [...prev, `error: ${message}`]);
      setOverall({ title: "Check failed", message, severity: "error" });
    } finally {
      if (unlisten) unlisten();
      setSettingUp(false);
    }
  }

  async function handleBorderless() {
    try {
      const response = await glintRequestBorderless();
      setCaptureSummary(response.captureSummary);
      if (response.overall) setOverall(response.overall);
    } catch (error) {
      const summary = `Borderless permission request failed: ${String(error)}`;
      setCaptureSummary(summary);
      setOverall({
        title: "Check failed",
        message: summary,
        severity: "error",
      });
    }
  }

  async function handleImport() {
    try {
      const picked = await open({
        multiple: false,
        filters: [{ name: "LiteRT model", extensions: ["litertlm"] }],
      });
      if (!picked) return;
      const response = await glintImportModel(picked);
      setModelSummary(response.modelSummary);
      setOverall(response.overall);
      setRuntime(response.runtime ?? null);
    } catch (error) {
      const summary = `Import failed: ${String(error)}`;
      setModelSummary(summary);
      setOverall({
        title: "Check failed",
        message: summary,
        severity: "error",
      });
    }
  }

  async function handleProbe() {
    try {
      const response = await glintProbe();
      setForegroundSummary(response.foregroundSummary);
      setOverall(response.overall);
    } catch (error) {
      const summary = `Probe failed: ${String(error)}`;
      setForegroundSummary(summary);
      setOverall({
        title: "Check failed",
        message: summary,
        severity: "error",
      });
    }
  }

  async function handleVerifyStorage() {
    try {
      const response = await glintVerifyStorage();
      setStorageSummary(response.storageSummary);
      setOverall(response.overall);
    } catch (error) {
      const summary = `Storage failed: ${String(error)}`;
      setStorageSummary(summary);
      setOverall({
        title: "Check failed",
        message: summary,
        severity: "error",
      });
    }
  }

  async function handleCompatibility() {
    try {
      const response = await glintCheckCompatibility();
      setCompatibilitySummary(response.compatibilitySummary);
      setOverall(response.overall);
    } catch (error) {
      const summary = `Compatibility check failed: ${String(error)}`;
      setCompatibilitySummary(summary);
      setOverall({
        title: "Check failed",
        message: summary,
        severity: "error",
      });
    }
  }

  function applyMenuPreset(index) {
    setTheme(stripPresetName(presetThemes[index]));
  }

  if (windowLabel === null) {
    return null;
  }

  if (windowLabel === "command-bar") {
    return <CommandBar />;
  }

  const busy = initializing || scanning;

  return (
    <div
      className="window-frame"
      style={{
        backgroundColor: windowBg,
      }}
    >
      <div
        className="titlebar"
        data-tauri-drag-region
        onContextMenu={openTitlebarMenu}
      >
        <span className="titlebar-title" data-tauri-drag-region>
          <svg
            viewBox="0 0 32 32"
            width="20.5"
            height="20.5"
            fill="rgb(83,234,213,0.8)"
            aria-hidden="true"
          >
            <path d="M16 1.4C16.62 10.8 17.9 14.1 30.6 16C17.9 17.9 16.62 21.2 16 30.6C15.38 21.2 14.1 17.9 1.4 16C14.1 14.1 15.38 10.8 16 1.4Z"></path>
            <circle cx="21.7" cy="9.3" r="1.5"></circle>
            <circle cx="10.5" cy="22.9" r="1.2"></circle>
          </svg>
          <span>Glint</span>
        </span>
        <div className="titlebar-controls">
          <button className="tb-btn" onClick={minimize} title="Minimize">
            &#8211;
          </button>
          <button className="tb-btn" onClick={toggleMaximize} title="Maximize">
            &#9744;
          </button>
          <button className="tb-btn tb-close" onClick={close} title="Close">
            &#10005;
          </button>
        </div>
      </div>

      {menuPos && (
        <TitlebarMenu
          position={menuPos}
          activePreset={presetIndexForTheme(theme)}
          onPreset={applyMenuPreset}
          onCustomize={() => setPanelOpen(true)}
          onMinimize={minimize}
          onToggleMaximize={toggleMaximize}
          onCloseWindow={close}
          onDismiss={() => setMenuPos(null)}
        />
      )}

      <div className="glint-shell">
        <NavRail page={page} onNavigate={setPage} />
        {page === "activity" && (
          <ActivityPage
            overall={overall}
            modelSummary={modelSummary}
            captureSummary={captureSummary}
            historySummary={historySummaryText(history.length)}
            history={history}
            scanning={scanning}
            busy={busy}
            onStart={handleStart}
            onPause={handlePause}
            onBorderless={handleBorderless}
            onImport={handleImport}
            runtimeSetup={
              runtime?.setupRequired ? { reason: runtime.setupReason } : null
            }
            settingUp={settingUp}
            setupLog={setupLog}
            onSetupRuntime={handleSetupRuntime}
          />
        )}
        {page === "search" && (
          <SearchPage
            query={query}
            onQueryChange={setQuery}
            onSearch={() => runSearch(query)}
            searchSummary={searchSummary}
            results={results}
          />
        )}
        {page === "settings" && <SettingsPage />}
        {page === "diagnostics" && (
          <DiagnosticsPage
            foregroundSummary={foregroundSummary}
            storageSummary={storageSummary}
            compatibilitySummary={compatibilitySummary}
            onProbe={handleProbe}
            onVerifyStorage={handleVerifyStorage}
            onCompatibility={handleCompatibility}
          />
        )}
      </div>

      {panelOpen && (
        <ColorSwitcher
          value={theme}
          onChange={setTheme}
          onClose={() => setPanelOpen(false)}
        />
      )}
    </div>
  );
}

export default App;
