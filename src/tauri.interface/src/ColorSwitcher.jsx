import "./ColorSwitcher.css";

const FALLBACK_RGB = { r: 30, g: 60, b: 114 };

export function isHexColor(value) {
  return (
    typeof value === "string" &&
    /^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/.test(value)
  );
}

export function clampAlpha(value, fallback) {
  const n = typeof value === "string" && value.trim() !== "" ? Number(value) : value;
  if (typeof n !== "number" || !Number.isFinite(n)) return fallback;
  return Math.min(1, Math.max(0, n));
}

export function hexToRgb(hex) {
  if (!isHexColor(hex)) return FALLBACK_RGB;
  let h = hex.slice(1);
  if (h.length === 3)
    h = h
      .split("")
      .map((c) => c + c)
      .join("");
  const n = parseInt(h, 16);
  if (Number.isNaN(n)) return FALLBACK_RGB;
  return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255 };
}

export function hexToRgba(hex, alpha) {
  const { r, g, b } = hexToRgb(hex);
  const a = clampAlpha(alpha, 1);
  return `rgba(${r}, ${g}, ${b}, ${a})`;
}

export const defaultTheme = {
  bg: "#1e3c72",
  bgAlpha: 0.55,
  accent: "#42a5f5",
};

export const presetThemes = [
  {
    name: "Ocean Blue",
    bg: "#1e3c72",
    bgAlpha: 0.55,
    accent: "#42a5f5",
  },
  {
    name: "Sunset Orange",
    bg: "#b74a3e",
    bgAlpha: 0.55,
    accent: "#ff7043",
  },
  {
    name: "Forest Green",
    bg: "#2e6232",
    bgAlpha: 0.55,
    accent: "#66bb6a",
  },
  {
    name: "Purple Violet",
    bg: "#5d368e",
    bgAlpha: 0.55,
    accent: "#ab47bc",
  },
  {
    name: "Rose Pink",
    bg: "#a7446a",
    bgAlpha: 0.55,
    accent: "#f06292",
  },
  {
    name: "Slate Gray",
    bg: "#475569",
    bgAlpha: 0.55,
    accent: "#90a4ae",
  },
  {
    name: "Golden",
    bg: "#a27a31",
    bgAlpha: 0.55,
    accent: "#ffd54f",
  },
  {
    name: "Midnight",
    bg: "#0f172a",
    bgAlpha: 0.65,
    accent: "#64748b",
  },
];

/**
 * Coerce any stored/partial value into a valid theme. Corrupt localStorage
 * (or a shape from an older build) must never crash rendering.
 */
export function sanitizeTheme(value) {
  const v = value && typeof value === "object" ? value : {};
  return {
    bg: isHexColor(v.bg) ? v.bg : defaultTheme.bg,
    bgAlpha: clampAlpha(v.bgAlpha, defaultTheme.bgAlpha),
    accent: isHexColor(v.accent) ? v.accent : defaultTheme.accent,
  };
}

export function loadTheme() {
  try {
    return sanitizeTheme(
      JSON.parse(localStorage.getItem("glint-window-theme")),
    );
  } catch {
    return { ...defaultTheme };
  }
}

export function presetIndexForTheme(theme) {
  if (!theme || typeof theme !== "object") return -1;
  return presetThemes.findIndex(
    (p) =>
      p.bg === theme.bg &&
      p.bgAlpha === theme.bgAlpha &&
      p.accent.toLowerCase() === String(theme.accent).toLowerCase(),
  );
}

export function stripPresetName(preset) {
  const { name, ...theme } = preset;
  return theme;
}

function ColorSwitcher({ value, onChange, onClose }) {
  // Active preset is derived from the current values so a preset picked
  // from the titlebar menu stays in sync with this panel.
  const selectedPreset = presetIndexForTheme(value);

  function update(patch) {
    onChange({ ...value, ...patch });
  }

  function applyPreset(index) {
    onChange(stripPresetName(presetThemes[index]));
  }

  const safe = sanitizeTheme(value);
  const windowBg = hexToRgba(safe.bg, safe.bgAlpha);

  return (
    <div className="color-switcher-panel">
      <div className="panel-header">
        <h3>Window Theme</h3>
        {onClose && (
          <button className="panel-close" onClick={onClose} title="Close panel">
            &#10005;
          </button>
        )}
      </div>

      <div className="panel-section">
        <label>Presets</label>
        <div className="preset-grid">
          {presetThemes.map((preset, index) => (
            <button
              key={preset.name}
              className={`preset-btn ${selectedPreset === index ? "active" : ""}`}
              onClick={() => applyPreset(index)}
              style={{ background: hexToRgba(preset.bg, preset.bgAlpha) }}
              title={preset.name}
            >
              <span
                className="preset-dot"
                style={{ backgroundColor: preset.accent }}
              ></span>
            </button>
          ))}
        </div>
      </div>

      <div className="panel-section">
        <label>Window background</label>
        <div className="color-input-row">
          <input
            type="color"
            value={safe.bg}
            onChange={(e) => update({ bg: e.target.value })}
            title="Window background color"
          />
          <input
            type="range"
            min="0.05"
            max="1"
            step="0.01"
            value={safe.bgAlpha}
            onChange={(e) => update({ bgAlpha: parseFloat(e.target.value) })}
            title={`Opacity ${safe.bgAlpha}`}
            className="alpha-slider"
          />
          <span className="alpha-value">
            {Math.round(safe.bgAlpha * 100)}%
          </span>
        </div>
      </div>

      <div className="panel-section">
        <label>Accent color</label>
        <div className="color-input-row">
          <input
            type="color"
            value={safe.accent}
            onChange={(e) => update({ accent: e.target.value })}
            title="Accent color"
          />
          <input
            type="text"
            value={value.accent}
            onChange={(e) => update({ accent: e.target.value })}
            className="color-text-input"
          />
        </div>
      </div>

      <div className="panel-preview">
        <label>Window preview</label>
        <div
          className="preview-box"
          style={{ background: windowBg}}
        >
          <div className="preview-titlebar">
            <div className="preview-dots">
              <span className="preview-dot close"></span>
              <span className="preview-dot minimize"></span>
              <span className="preview-dot maximize"></span>
            </div>
            <span className="preview-title">glint</span>
          </div>
          <div className="preview-content" style={{ color: safe.accent }}>
            desktop shows through here
          </div>
        </div>
      </div>
    </div>
  );
}

export default ColorSwitcher;
