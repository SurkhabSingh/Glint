import { useState, useEffect, useCallback } from "react";
import ColorSwitcher, {
  defaultTheme,
  hexToRgba,
  presetThemes,
  presetIndexForTheme,
  stripPresetName,
} from "./ColorSwitcher";
import TitlebarMenu from "./TitlebarMenu";
import "./App.css";

function loadTheme() {
  try {
    const raw = localStorage.getItem("glint-window-theme");
    if (raw) return { ...defaultTheme, ...JSON.parse(raw) };
  } catch {
    /* ignore corrupt storage */
  }
  return defaultTheme;
}

async function getTauriWindow() {
  const { getCurrentWindow } = await import("@tauri-apps/api/window");
  return getCurrentWindow();
}

function App() {
  const [theme, setTheme] = useState(loadTheme);
  const [menuPos, setMenuPos] = useState(null);
  const [panelOpen, setPanelOpen] = useState(false);

  const windowBg = hexToRgba(theme.bg, theme.bgAlpha);
  const windowBorder = hexToRgba(theme.border, theme.borderAlpha);

  useEffect(() => {
    document.documentElement.style.setProperty("--window-bg", windowBg);
    document.documentElement.style.setProperty("--window-border", windowBorder);
    document.documentElement.style.setProperty("--accent-color", theme.accent);
    localStorage.setItem("glint-window-theme", JSON.stringify(theme));
  }, [windowBg, windowBorder, theme]);

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

  function applyMenuPreset(index) {
    setTheme(stripPresetName(presetThemes[index]));
  }

  return (
    <div
      className="window-frame"
      style={{
        backgroundColor: windowBg,
        borderColor: windowBorder,
        borderWidth: theme.borderWidth,
      }}
    >
      <div
        className="titlebar"
        data-tauri-drag-region
        onContextMenu={openTitlebarMenu}
      >
        <span className="titlebar-title" data-tauri-drag-region>
          Glint
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

      <main className="container">
        {panelOpen && (
          <ColorSwitcher
            value={theme}
            onChange={setTheme}
            onClose={() => setPanelOpen(false)}
          />
        )}
      </main>
    </div>
  );
}

export default App;
