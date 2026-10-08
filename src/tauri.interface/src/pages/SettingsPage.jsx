import { useEffect, useRef, useState } from "react";
import { glintAdminStatus, glintRestartAsAdmin } from "../glint";
import { ACCENTS, DEFAULT_APPEARANCE, THEMES, ZOOM_STEPS, stepZoom, themeById } from "../themes";

function Toggle({ checked, onToggle, label, disabled = false }) {
  return (
    <button
      className={`setting-toggle ${checked ? "on" : ""}`}
      onClick={() => onToggle(!checked)}
      role="switch"
      aria-checked={checked}
      aria-label={label}
      disabled={disabled}
    >
      <span className="setting-knob" />
    </button>
  );
}

function Slider({ value, min, max, step, onChange, format, label, disabled = false }) {
  return (
    <div className="setting-slider">
      <input
        type="range"
        min={min}
        max={max}
        step={step}
        value={value}
        disabled={disabled}
        aria-label={label}
        onChange={(e) => onChange(Number(e.target.value))}
      />
      <output>{format(value)}</output>
    </div>
  );
}

/** A theme's colors in a line: its background with the palette on it. */
function ThemeChip({ theme }) {
  return (
    <span className="theme-chip" style={{ background: theme.bg, borderColor: theme.surface2 }} aria-hidden="true">
      {["red", "yellow", "green", "blue", "purple"].map((hue) => (
        <span key={hue} style={{ background: theme[hue] }} />
      ))}
    </span>
  );
}

const THEME_GROUPS = [
  { label: "Dark", list: THEMES.filter((t) => t.dark) },
  { label: "Light", list: THEMES.filter((t) => !t.dark) },
];

/**
 * Theme dropdown. A native <select> can't show colors, so this is a small
 * listbox: arrow keys move, Enter picks, Escape or a click outside closes.
 */
function ThemeSelect({ value, onChange }) {
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(() => Math.max(0, THEMES.findIndex((t) => t.id === value)));
  const root = useRef(null);
  const list = useRef(null);
  const ordered = THEME_GROUPS.flatMap((g) => g.list);
  const current = themeById(value);

  useEffect(() => {
    if (!open) return;
    setActive(Math.max(0, ordered.findIndex((t) => t.id === value)));
    function onDown(e) {
      if (root.current && !root.current.contains(e.target)) setOpen(false);
    }
    document.addEventListener("mousedown", onDown);
    return () => document.removeEventListener("mousedown", onDown);
  }, [open]);

  useEffect(() => {
    if (open) list.current?.querySelector(`[data-index="${active}"]`)?.scrollIntoView({ block: "nearest" });
  }, [open, active]);

  function pick(theme) {
    onChange(theme.id);
    setOpen(false);
  }

  function onKeyDown(e) {
    if (!open) {
      if (["ArrowDown", "ArrowUp", "Enter", " "].includes(e.key)) {
        e.preventDefault();
        setOpen(true);
      }
      return;
    }
    if (e.key === "Escape") {
      e.preventDefault();
      setOpen(false);
    } else if (e.key === "ArrowDown") {
      e.preventDefault();
      setActive((i) => Math.min(ordered.length - 1, i + 1));
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setActive((i) => Math.max(0, i - 1));
    } else if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      pick(ordered[active]);
    } else if (e.key === "Tab") {
      setOpen(false);
    }
  }

  let index = -1;
  return (
    <div className="theme-select" ref={root}>
      <button
        className={`theme-select-button ${open ? "open" : ""}`}
        onClick={() => setOpen((o) => !o)}
        onKeyDown={onKeyDown}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-label={`Theme: ${current.name}`}
      >
        <ThemeChip theme={current} />
        <span className="theme-select-name">{current.name}</span>
        <span className="theme-select-kind">{current.dark ? "Dark" : "Light"}</span>
        <span className="theme-select-chevron" aria-hidden="true">▾</span>
      </button>
      {open && (
        <div className="theme-select-menu" role="listbox" aria-label="Themes" ref={list}>
          {THEME_GROUPS.map((group) => (
            <div key={group.label} role="group" aria-label={group.label}>
              <div className="theme-select-group">{group.label}</div>
              {group.list.map((theme) => {
                index += 1;
                const i = index;
                return (
                  <div
                    key={theme.id}
                    role="option"
                    data-index={i}
                    aria-selected={theme.id === value}
                    className={`theme-option ${i === active ? "active" : ""} ${theme.id === value ? "selected" : ""}`}
                    onMouseEnter={() => setActive(i)}
                    onMouseDown={(e) => e.preventDefault()}
                    onClick={() => pick(theme)}
                  >
                    <ThemeChip theme={theme} />
                    <span className="theme-option-name">{theme.name}</span>
                    {theme.id === value && <span aria-hidden="true">✓</span>}
                  </div>
                );
              })}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

/**
 * Apps running as administrator (some games and their anti-cheat, admin
 * tools) can't be read by a Glint that isn't. Glint still records their
 * name, title and time; restarting it as administrator lets it read them.
 */
function AdminCard() {
  const [elevated, setElevated] = useState(null);
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    glintAdminStatus()
      .then((status) => setElevated(Boolean(status?.elevated)))
      .catch(() => setElevated(null));
  }, []);

  async function restart() {
    setBusy(true);
    setMessage("");
    try {
      await glintRestartAsAdmin();
    } catch (error) {
      setMessage(String(error?.message ?? error));
      setBusy(false);
    }
  }

  return (
    <div className="glint-card">
      <h3>Apps running as administrator</h3>
      <div className="setting-row">
        <div>
          <div className="setting-name">
            {elevated === null ? "Status unknown" : elevated ? "Glint is running as administrator" : "Glint is running normally"}
          </div>
          <div className="setting-desc">
            {elevated
              ? "Apps that run as administrator are read like any other app. Passwords, secrets and blocked apps are still kept out."
              : "Some games and tools run as administrator. Windows keeps their screen from Glint, so it records only their name, title and time (a game still counts as play). Restart Glint as administrator to read them fully. Windows will ask you first; this lasts until Glint is closed."}
          </div>
          {message && <div className="setting-desc admin-error">{message}</div>}
        </div>
        {elevated === false && (
          <button className="glint-btn" onClick={restart} disabled={busy}>
            {busy ? "Waiting for Windows…" : "Restart as administrator"}
          </button>
        )}
      </div>
    </div>
  );
}

const percent = (value) => `${Math.round(value * 100)}%`;

function SettingsPage({ appearance, onAppearanceChange }) {
  const [launchAtLogin, setLaunchAtLogin] = useState(false);
  const [scanNotifications, setScanNotifications] = useState(true);
  const theme = themeById(appearance.theme);
  const set = (patch) => onAppearanceChange({ ...appearance, ...patch });
  const see = appearance.transparency;

  return (
    <div className="glint-page">
      <div className="glint-page-inner narrow">
        <h1 className="glint-title">Settings</h1>

        <div className="glint-card">
          <h3>Theme</h3>
          <p className="dim">Palettes from the terminal and editor themes you know.</p>
          <div className="setting-row">
            <div>
              <div className="setting-name">Theme</div>
              <div className="setting-desc">{theme.dark ? "Dark" : "Light"} · applies to the whole window</div>
            </div>
            <ThemeSelect value={theme.id} onChange={(id) => set({ theme: id })} />
          </div>

          <div className="theme-group-label">Accent</div>
          <div className="accent-row" role="radiogroup" aria-label="Accent color">
            {ACCENTS.map((choice) =>
              choice === "theme" ? (
                <button
                  key={choice}
                  role="radio"
                  aria-checked={appearance.accent === "theme"}
                  className={`accent-swatch theme-default ${appearance.accent === "theme" ? "active" : ""}`}
                  onClick={() => set({ accent: "theme" })}
                >
                  Theme default
                </button>
              ) : (
                <button
                  key={choice}
                  role="radio"
                  aria-checked={appearance.accent === choice}
                  aria-label={choice}
                  title={choice}
                  className={`accent-swatch ${appearance.accent === choice ? "active" : ""}`}
                  style={{ background: theme[choice] }}
                  onClick={() => set({ accent: choice })}
                />
              ),
            )}
          </div>
        </div>

        <div className="glint-card">
          <h3>Zoom</h3>
          <div className="setting-row">
            <div>
              <div className="setting-name">Interface size</div>
              <div className="setting-desc">
                Makes everything in Glint smaller or bigger, text and layout together. Ctrl&nbsp;+ and Ctrl&nbsp;− work anywhere in the app; Ctrl&nbsp;0 resets.
              </div>
            </div>
            <div className="zoom-control">
              <button
                className="zoom-btn"
                onClick={() => set({ zoom: stepZoom(appearance.zoom, -1) })}
                disabled={appearance.zoom <= ZOOM_STEPS[0]}
                aria-label="Zoom out"
                title="Zoom out (Ctrl −)"
              >
                −
              </button>
              <select
                className="zoom-select"
                value={ZOOM_STEPS.includes(appearance.zoom) ? appearance.zoom : ""}
                onChange={(e) => set({ zoom: Number(e.target.value) })}
                aria-label="Zoom level"
              >
                {!ZOOM_STEPS.includes(appearance.zoom) && (
                  <option value="">{percent(appearance.zoom)}</option>
                )}
                {ZOOM_STEPS.map((z) => (
                  <option key={z} value={z}>
                    {percent(z)}
                  </option>
                ))}
              </select>
              <button
                className="zoom-btn"
                onClick={() => set({ zoom: stepZoom(appearance.zoom, 1) })}
                disabled={appearance.zoom >= ZOOM_STEPS[ZOOM_STEPS.length - 1]}
                aria-label="Zoom in"
                title="Zoom in (Ctrl +)"
              >
                +
              </button>
              <button
                className="zoom-reset"
                onClick={() => set({ zoom: 1 })}
                disabled={appearance.zoom === 1}
              >
                Reset
              </button>
            </div>
          </div>
        </div>

        <div className="glint-card">
          <h3>Transparency &amp; blur</h3>
          <div className="setting-row">
            <div>
              <div className="setting-name">Transparent window</div>
              <div className="setting-desc">Let the desktop show through Glint. Off paints the theme background solid.</div>
            </div>
            <Toggle checked={see} onToggle={(on) => set({ transparency: on })} label="Transparent window" />
          </div>
          <div className={`setting-row ${see ? "" : "disabled"}`}>
            <div>
              <div className="setting-name">Window opacity</div>
              <div className="setting-desc">How much of the theme color covers the desktop.</div>
            </div>
            <Slider
              value={appearance.opacity}
              min={0.2}
              max={1}
              step={0.01}
              disabled={!see}
              label="Window opacity"
              format={percent}
              onChange={(opacity) => set({ opacity })}
            />
          </div>
          <div className={`setting-row ${see ? "" : "disabled"}`}>
            <div>
              <div className="setting-name">Blur behind the window</div>
              <div className="setting-desc">Frosted glass from Windows. Off shows the desktop sharp through the window.</div>
            </div>
            <Toggle checked={see && appearance.blur} disabled={!see} onToggle={(on) => set({ blur: on })} label="Blur behind the window" />
          </div>
          <div className={`setting-row ${see ? "" : "disabled"}`}>
            <div>
              <div className="setting-name">Card opacity</div>
              <div className="setting-desc">How solid the cards on each page are.</div>
            </div>
            <Slider
              value={appearance.cardOpacity}
              min={0.2}
              max={1}
              step={0.01}
              disabled={!see}
              label="Card opacity"
              format={percent}
              onChange={(cardOpacity) => set({ cardOpacity })}
            />
          </div>
          <div className={`setting-row ${see ? "" : "disabled"}`}>
            <div>
              <div className="setting-name">Card blur</div>
              <div className="setting-desc">Frost on the cards themselves. 0 turns it off.</div>
            </div>
            <Slider
              value={appearance.cardBlur}
              min={0}
              max={40}
              step={1}
              disabled={!see}
              label="Card blur"
              format={(v) => `${v}px`}
              onChange={(cardBlur) => set({ cardBlur })}
            />
          </div>
          <div className="glint-btn-row">
            <button className="glint-btn" onClick={() => onAppearanceChange({ ...DEFAULT_APPEARANCE })}>
              Reset appearance
            </button>
          </div>
        </div>

        <AdminCard />

        <div className="glint-card">
          <h3>General</h3>
          <p className="dim">These two are placeholders and don't do anything yet.</p>
          <div className="setting-row">
            <div>
              <div className="setting-name">Launch at login</div>
              <div className="setting-desc">Start Glint in the system tray when Windows starts.</div>
            </div>
            <Toggle checked={launchAtLogin} onToggle={setLaunchAtLogin} label="Launch at login" />
          </div>
          <div className="setting-row">
            <div>
              <div className="setting-name">Scan notifications</div>
              <div className="setting-desc">Show a note when a new scan is summarized.</div>
            </div>
            <Toggle checked={scanNotifications} onToggle={setScanNotifications} label="Scan notifications" />
          </div>
        </div>
      </div>
    </div>
  );
}

export default SettingsPage;
