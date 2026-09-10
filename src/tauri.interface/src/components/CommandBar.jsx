import { useCallback, useEffect, useRef, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";
import {
  glintCaptureOnce,
  glintOpenSearch,
  glintPauseScanning,
  glintShowMain,
  glintStartScanning,
  parseQuickCommand,
  QuickCommandKind,
} from "../glint";
import { hexToRgba, loadTheme, sanitizeTheme } from "../ColorSwitcher";

const SUGGESTIONS = [
  { tag: "capture this", glyph: "⧉" },
  { tag: "start scanning", glyph: "▶" },
  { tag: "pause scanning", glyph: "⏸" },
  { tag: "search your context", glyph: "⌕" },
  { tag: "open Glint", glyph: "❖" },
];

/**
 * Ports CommandBarWindow: floating quick-command palette toggled by
 * Ctrl+Alt+G. Esc hides it; Enter submits (quick commands mirror
 * QuickCommandParser: capture / start / pause / search / open).
 */
function CommandBar() {
  const [text, setText] = useState("");
  const [selected, setSelected] = useState(-1);
  const [theme, setTheme] = useState(loadTheme);
  const inputRef = useRef(null);

  const hide = useCallback(async () => {
    try {
      await getCurrentWindow().hide();
    } catch {
      /* browser dev: nothing to hide */
    }
  }, []);

  const submit = useCallback(
    async (raw) => {
      const command = raw.trim();
      if (!command) {
        await hide();
        return;
      }
      const parsed = parseQuickCommand(command);
      await hide();
      // Small delay so the bar is gone before focus/scans start (ports the
      // 120 ms settle in CommandBarWindow.SubmitAsync).
      await new Promise((resolve) => setTimeout(resolve, 120));
      try {
        switch (parsed.kind) {
          case QuickCommandKind.CaptureCurrentWindow:
            await glintCaptureOnce();
            break;
          case QuickCommandKind.StartScanning:
            await glintStartScanning();
            break;
          case QuickCommandKind.PauseScanning:
            await glintPauseScanning();
            break;
          case QuickCommandKind.SearchContext:
            await glintOpenSearch(parsed.query);
            break;
          case QuickCommandKind.OpenDashboard:
          default:
            await glintShowMain();
            break;
        }
      } catch {
        /* bridge errors surface on the dashboard banner */
      }
    },
    [hide]
  );

  // Re-read the shared theme (same localStorage key as the dashboard) and
  // focus the input every time the bar is shown.
  useEffect(() => {
    let unlisten;
    listen("command-bar-shown", () => {
      setTheme(loadTheme());
      setText("");
      setSelected(-1);
      inputRef.current?.focus();
    }).then((fn) => {
      unlisten = fn;
    });
    inputRef.current?.focus();
    return () => unlisten?.();
  }, []);

  function onKeyDown(e) {
    if (e.key === "Escape") {
      e.preventDefault();
      hide();
      return;
    }
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setSelected((s) => (s + 1) % SUGGESTIONS.length);
      return;
    }
    if (e.key === "ArrowUp") {
      e.preventDefault();
      setSelected((s) => (s - 1 + SUGGESTIONS.length) % SUGGESTIONS.length);
      return;
    }
    if (e.key === "Enter") {
      e.preventDefault();
      if (text.trim()) {
        submit(text);
      } else if (selected >= 0) {
        submit(SUGGESTIONS[selected].tag);
      } else {
        hide();
      }
    }
  }

  const safeTheme = sanitizeTheme(theme);

  return (
    <div
      className="cmdbar"
      style={{
        backgroundColor: hexToRgba(
          safeTheme.bg,
          Math.min(1, safeTheme.bgAlpha + 0.25),
        ),
      }}
    >
      <div className="cmdbar-grip" data-tauri-drag-region>
        <span data-tauri-drag-region />
      </div>
      <div className="cmdbar-input-row">
        <span
          className="cmdbar-badge"
          style={{ backgroundColor: safeTheme.accent }}
        >
          G
        </span>
        <input
          ref={inputRef}
          value={text}
          onChange={(e) => {
            setText(e.target.value);
            setSelected(-1);
          }}
          onKeyDown={onKeyDown}
          placeholder="Type a command or search your context"
          aria-label="Glint command bar"
        />
        <span className="cmdbar-enter">Enter</span>
      </div>
      <div className="cmdbar-divider" />
      <div className="cmdbar-suggestions">
        {SUGGESTIONS.map((item, index) => (
          <button
            key={item.tag}
            className={`cmdbar-suggestion ${selected === index ? "selected" : ""}`}
            onClick={() => submit(item.tag)}
            onMouseEnter={() => setSelected(index)}
          >
            <span className="glyph">{item.glyph}</span>
            <span>{item.tag}</span>
          </button>
        ))}
      </div>
      <div className="cmdbar-hint">Ctrl+Alt+G toggles this command bar. Esc closes it.</div>
    </div>
  );
}

export default CommandBar;
