import "./ColorSwitcher.css";

export function hexToRgb(hex) {
  let h = hex.replace("#", "");
  if (h.length === 3) h = h.split("").map((c) => c + c).join("");
  const n = parseInt(h, 16);
  if (Number.isNaN(n) || h.length !== 6) return { r: 30, g: 60, b: 114 };
  return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255 };
}

export function hexToRgba(hex, alpha) {
  const { r, g, b } = hexToRgb(hex);
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

export const defaultTheme = {
  bg: "#1e3c72",
  bgAlpha: 0.55,
  border: "#2a5298",
  borderAlpha: 0.8,
  borderWidth: 2,
  accent: "#42a5f5",
};

export const presetThemes = [
  { name: "Ocean Blue", bg: "#1e3c72", bgAlpha: 0.55, border: "#2a5298", borderAlpha: 0.8, borderWidth: 2, accent: "#42a5f5" },
  { name: "Sunset Orange", bg: "#b74a3e", bgAlpha: 0.55, border: "#e16450", borderAlpha: 0.8, borderWidth: 2, accent: "#ff7043" },
  { name: "Forest Green", bg: "#2e6232", bgAlpha: 0.55, border: "#438144", borderAlpha: 0.8, borderWidth: 2, accent: "#66bb6a" },
  { name: "Purple Violet", bg: "#5d368e", bgAlpha: 0.55, border: "#7f4eb7", borderAlpha: 0.8, borderWidth: 2, accent: "#ab47bc" },
  { name: "Rose Pink", bg: "#a7446a", bgAlpha: 0.55, border: "#c85882", borderAlpha: 0.8, borderWidth: 2, accent: "#f06292" },
  { name: "Slate Gray", bg: "#475569", bgAlpha: 0.55, border: "#64748b", borderAlpha: 0.8, borderWidth: 2, accent: "#90a4ae" },
  { name: "Golden", bg: "#a27a31", bgAlpha: 0.55, border: "#c59646", borderAlpha: 0.8, borderWidth: 2, accent: "#ffd54f" },
  { name: "Midnight", bg: "#0f172a", bgAlpha: 0.65, border: "#1e293b", borderAlpha: 0.85, borderWidth: 2, accent: "#64748b" },
];

export function presetIndexForTheme(theme) {
  return presetThemes.findIndex(
    (p) =>
      p.bg === theme.bg &&
      p.bgAlpha === theme.bgAlpha &&
      p.border === theme.border &&
      p.borderAlpha === theme.borderAlpha &&
      p.borderWidth === theme.borderWidth &&
      p.accent.toLowerCase() === String(theme.accent).toLowerCase()
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

  const windowBg = hexToRgba(value.bg, value.bgAlpha);
  const windowBorder = hexToRgba(value.border, value.borderAlpha);

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
              <span className="preset-dot" style={{ backgroundColor: preset.accent }}></span>
            </button>
          ))}
        </div>
      </div>

      <div className="panel-section">
        <label>Window background</label>
        <div className="color-input-row">
          <input
            type="color"
            value={value.bg}
            onChange={(e) => update({ bg: e.target.value })}
            title="Window background color"
          />
          <input
            type="range"
            min="0.05"
            max="1"
            step="0.01"
            value={value.bgAlpha}
            onChange={(e) => update({ bgAlpha: parseFloat(e.target.value) })}
            title={`Opacity ${value.bgAlpha}`}
            className="alpha-slider"
          />
          <span className="alpha-value">{Math.round(value.bgAlpha * 100)}%</span>
        </div>
      </div>

      <div className="panel-section">
        <label>Window border</label>
        <div className="color-input-row">
          <input
            type="color"
            value={value.border}
            onChange={(e) => update({ border: e.target.value })}
            title="Window border color"
          />
          <input
            type="range"
            min="0.05"
            max="1"
            step="0.01"
            value={value.borderAlpha}
            onChange={(e) => update({ borderAlpha: parseFloat(e.target.value) })}
            title={`Opacity ${value.borderAlpha}`}
            className="alpha-slider"
          />
          <span className="alpha-value">{Math.round(value.borderAlpha * 100)}%</span>
        </div>
        <div className="color-input-row border-width-row">
          <span className="range-label">Width</span>
          <input
            type="range"
            min="0"
            max="8"
            step="1"
            value={value.borderWidth}
            onChange={(e) => update({ borderWidth: parseInt(e.target.value, 10) })}
            className="alpha-slider"
          />
          <span className="alpha-value">{value.borderWidth}px</span>
        </div>
      </div>

      <div className="panel-section">
        <label>Accent color</label>
        <div className="color-input-row">
          <input
            type="color"
            value={value.accent}
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
          style={{ background: windowBg, borderColor: windowBorder }}
        >
          <div className="preview-titlebar">
            <div className="preview-dots">
              <span className="preview-dot close"></span>
              <span className="preview-dot minimize"></span>
              <span className="preview-dot maximize"></span>
            </div>
            <span className="preview-title">glint</span>
          </div>
          <div className="preview-content" style={{ color: value.accent }}>
            desktop shows through here
          </div>
        </div>
      </div>
    </div>
  );
}

export default ColorSwitcher;
