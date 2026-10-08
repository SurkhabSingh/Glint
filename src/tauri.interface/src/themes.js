/**
 * Themes and appearance. Each theme is a published terminal/editor palette
 * (the same families OpenCode ships): a background, two surface steps,
 * text, muted text, an accent and eight named hues. Everything on screen
 * reads these through CSS variables, so light themes work as well as dark.
 *
 * Appearance (theme, accent, transparency, blur) is stored in one
 * localStorage key shared by the dashboard and the command bar.
 */

const HUES = ["red", "orange", "yellow", "green", "teal", "blue", "purple", "pink"];

export const THEMES = [
  {
    id: "catppuccin-mocha",
    name: "Catppuccin Mocha",
    dark: true,
    bg: "#1e1e2e", surface: "#313244", surface2: "#45475a",
    text: "#cdd6f4", muted: "#a6adc8", accent: "mauve",
    red: "#f38ba8", orange: "#fab387", yellow: "#f9e2af", green: "#a6e3a1",
    teal: "#94e2d5", blue: "#89b4fa", purple: "#cba6f7", pink: "#f5c2e7",
  },
  {
    id: "catppuccin-macchiato",
    name: "Catppuccin Macchiato",
    dark: true,
    bg: "#24273a", surface: "#363a4f", surface2: "#494d64",
    text: "#cad3f5", muted: "#a5adcb", accent: "mauve",
    red: "#ed8796", orange: "#f5a97f", yellow: "#eed49f", green: "#a6da95",
    teal: "#8bd5ca", blue: "#8aadf4", purple: "#c6a0f6", pink: "#f5bde6",
  },
  {
    id: "catppuccin-latte",
    name: "Catppuccin Latte",
    dark: false,
    bg: "#eff1f5", surface: "#e6e9ef", surface2: "#ccd0da",
    text: "#4c4f69", muted: "#6c6f85", accent: "mauve",
    red: "#d20f39", orange: "#fe640b", yellow: "#df8e1d", green: "#40a02b",
    teal: "#179299", blue: "#1e66f5", purple: "#8839ef", pink: "#ea76cb",
  },
  {
    id: "everforest-dark",
    name: "Everforest Dark",
    dark: true,
    bg: "#2d353b", surface: "#343f44", surface2: "#475258",
    text: "#d3c6aa", muted: "#9da9a0", accent: "green",
    red: "#e67e80", orange: "#e69875", yellow: "#dbbc7f", green: "#a7c080",
    teal: "#83c092", blue: "#7fbbb3", purple: "#d699b6", pink: "#e8a2c5",
  },
  {
    id: "everforest-light",
    name: "Everforest Light",
    dark: false,
    bg: "#fdf6e3", surface: "#f4f0d9", surface2: "#e6e2cc",
    text: "#5c6a72", muted: "#829181", accent: "green",
    red: "#f85552", orange: "#f57d26", yellow: "#dfa000", green: "#8da101",
    teal: "#35a77c", blue: "#3a94c5", purple: "#df69ba", pink: "#e66868",
  },
  {
    id: "vercel-dark",
    name: "Vercel Dark",
    dark: true,
    bg: "#000000", surface: "#0a0a0a", surface2: "#1f1f1f",
    text: "#ededed", muted: "#a1a1a1", accent: "text",
    red: "#ff6166", orange: "#ff990a", yellow: "#ffcd38", green: "#62c073",
    teal: "#0ac7b4", blue: "#52a8ff", purple: "#bf7af0", pink: "#f75f8f",
  },
  {
    id: "vercel-light",
    name: "Vercel Light",
    dark: false,
    bg: "#ffffff", surface: "#fafafa", surface2: "#eaeaea",
    text: "#171717", muted: "#666666", accent: "text",
    red: "#e5484d", orange: "#f76b15", yellow: "#d4a106", green: "#29a383",
    teal: "#0d9b8a", blue: "#0070f3", purple: "#8e4ec6", pink: "#d6409f",
  },
  {
    id: "opencode",
    name: "OpenCode",
    dark: true,
    bg: "#0a0a0a", surface: "#141414", surface2: "#262626",
    text: "#eeeeee", muted: "#808080", accent: "orange",
    red: "#e06c75", orange: "#fab283", yellow: "#e5c07b", green: "#7fd88f",
    teal: "#56b6c2", blue: "#5c9cf5", purple: "#9d7cd8", pink: "#f5a7c4",
  },
  {
    id: "tokyonight",
    name: "Tokyo Night",
    dark: true,
    bg: "#1a1b26", surface: "#24283b", surface2: "#292e42",
    text: "#c0caf5", muted: "#a9b1d6", accent: "blue",
    red: "#f7768e", orange: "#ff9e64", yellow: "#e0af68", green: "#9ece6a",
    teal: "#73daca", blue: "#7aa2f7", purple: "#bb9af7", pink: "#ff007c",
  },
  {
    id: "gruvbox-dark",
    name: "Gruvbox Dark",
    dark: true,
    bg: "#282828", surface: "#3c3836", surface2: "#504945",
    text: "#ebdbb2", muted: "#a89984", accent: "yellow",
    red: "#fb4934", orange: "#fe8019", yellow: "#fabd2f", green: "#b8bb26",
    teal: "#8ec07c", blue: "#83a598", purple: "#d3869b", pink: "#e396a8",
  },
  {
    id: "gruvbox-light",
    name: "Gruvbox Light",
    dark: false,
    bg: "#fbf1c7", surface: "#f2e5bc", surface2: "#ebdbb2",
    text: "#3c3836", muted: "#7c6f64", accent: "orange",
    red: "#9d0006", orange: "#af3a03", yellow: "#b57614", green: "#79740e",
    teal: "#427b58", blue: "#076678", purple: "#8f3f71", pink: "#b16286",
  },
  {
    id: "nord",
    name: "Nord",
    dark: true,
    bg: "#2e3440", surface: "#3b4252", surface2: "#434c5e",
    text: "#eceff4", muted: "#d8dee9", accent: "teal",
    red: "#bf616a", orange: "#d08770", yellow: "#ebcb8b", green: "#a3be8c",
    teal: "#88c0d0", blue: "#81a1c1", purple: "#b48ead", pink: "#d3a4c9",
  },
  {
    id: "kanagawa",
    name: "Kanagawa",
    dark: true,
    bg: "#1f1f28", surface: "#2a2a37", surface2: "#363646",
    text: "#dcd7ba", muted: "#a6a69c", accent: "blue",
    red: "#e46876", orange: "#ffa066", yellow: "#e6c384", green: "#98bb6c",
    teal: "#7aa89f", blue: "#7e9cd8", purple: "#957fb8", pink: "#d27e99",
  },
  {
    id: "rose-pine",
    name: "Rosé Pine",
    dark: true,
    bg: "#191724", surface: "#1f1d2e", surface2: "#26233a",
    text: "#e0def4", muted: "#908caa", accent: "purple",
    red: "#eb6f92", orange: "#ebbcba", yellow: "#f6c177", green: "#31748f",
    teal: "#9ccfd8", blue: "#3e8fb0", purple: "#c4a7e7", pink: "#ea9a97",
  },
  {
    id: "rose-pine-dawn",
    name: "Rosé Pine Dawn",
    dark: false,
    bg: "#faf4ed", surface: "#fffaf3", surface2: "#f2e9e1",
    text: "#575279", muted: "#797593", accent: "purple",
    red: "#b4637a", orange: "#d7827e", yellow: "#ea9d34", green: "#286983",
    teal: "#56949f", blue: "#3e8fb0", purple: "#907aa9", pink: "#d7827e",
  },
  {
    id: "one-dark",
    name: "One Dark",
    dark: true,
    bg: "#282c34", surface: "#21252b", surface2: "#2c313a",
    text: "#abb2bf", muted: "#7f848e", accent: "blue",
    red: "#e06c75", orange: "#d19a66", yellow: "#e5c07b", green: "#98c379",
    teal: "#56b6c2", blue: "#61afef", purple: "#c678dd", pink: "#e599c4",
  },
  {
    id: "dracula",
    name: "Dracula",
    dark: true,
    bg: "#282a36", surface: "#343746", surface2: "#44475a",
    text: "#f8f8f2", muted: "#9aa3c9", accent: "purple",
    red: "#ff5555", orange: "#ffb86c", yellow: "#f1fa8c", green: "#50fa7b",
    teal: "#8be9fd", blue: "#6f8cf2", purple: "#bd93f9", pink: "#ff79c6",
  },
  {
    id: "ayu-dark",
    name: "Ayu Dark",
    dark: true,
    bg: "#0d1017", surface: "#131721", surface2: "#1f2430",
    text: "#bfbdb6", muted: "#8a8f98", accent: "yellow",
    red: "#f07178", orange: "#ff8f40", yellow: "#e6b450", green: "#aad94c",
    teal: "#95e6cb", blue: "#59c2ff", purple: "#d2a6ff", pink: "#f29668",
  },
];

/** The accent choices: "theme" keeps the theme's own, else a named hue. */
export const ACCENTS = ["theme", ...HUES];

export const DEFAULT_APPEARANCE = {
  theme: "catppuccin-mocha",
  accent: "theme",
  transparency: true,
  opacity: 0.78,
  blur: true,
  cardOpacity: 0.6,
  cardBlur: 22,
  zoom: 1,
};

/** Zoom steps, the same ladder browsers use for Ctrl + / Ctrl -. */
export const ZOOM_STEPS = [0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2];

/** The next zoom step up (+1) or down (-1) from any value. */
export function stepZoom(current, direction) {
  if (direction > 0) return ZOOM_STEPS.find((z) => z > current + 0.001) ?? ZOOM_STEPS[ZOOM_STEPS.length - 1];
  return [...ZOOM_STEPS].reverse().find((z) => z < current - 0.001) ?? ZOOM_STEPS[0];
}

const STORAGE_KEY = "glint-appearance";
const LEGACY_KEY = "glint-window-theme";

export function themeById(id) {
  return THEMES.find((theme) => theme.id === id) ?? THEMES[0];
}

export function isHexColor(value) {
  return typeof value === "string" && /^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/.test(value);
}

export function hexToRgb(hex) {
  if (!isHexColor(hex)) return { r: 0, g: 0, b: 0 };
  let h = hex.slice(1);
  if (h.length === 3) h = h.split("").map((c) => c + c).join("");
  const n = parseInt(h, 16);
  return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255 };
}

const triplet = (hex) => {
  const { r, g, b } = hexToRgb(hex);
  return `${r} ${g} ${b}`;
};

function clamp(value, min, max, fallback) {
  const n = typeof value === "string" && value.trim() !== "" ? Number(value) : value;
  if (typeof n !== "number" || !Number.isFinite(n)) return fallback;
  return Math.min(max, Math.max(min, n));
}

/** WCAG relative luminance, for picking readable text on the accent. */
function luminance(hex) {
  const channel = (c) => {
    const v = c / 255;
    return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  };
  const { r, g, b } = hexToRgb(hex);
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
}

/** Coerce anything stored into valid settings; bad values never crash. */
export function sanitizeAppearance(value) {
  const v = value && typeof value === "object" ? value : {};
  const d = DEFAULT_APPEARANCE;
  return {
    theme: THEMES.some((t) => t.id === v.theme) ? v.theme : d.theme,
    accent: ACCENTS.includes(v.accent) ? v.accent : d.accent,
    transparency: typeof v.transparency === "boolean" ? v.transparency : d.transparency,
    opacity: clamp(v.opacity, 0.2, 1, d.opacity),
    blur: typeof v.blur === "boolean" ? v.blur : d.blur,
    cardOpacity: clamp(v.cardOpacity, 0.2, 1, d.cardOpacity),
    cardBlur: Math.round(clamp(v.cardBlur, 0, 40, d.cardBlur)),
    zoom: Math.round(clamp(v.zoom, 0.5, 2, d.zoom) * 100) / 100,
  };
}

export function loadAppearance() {
  try {
    localStorage.removeItem(LEGACY_KEY);
    return sanitizeAppearance(JSON.parse(localStorage.getItem(STORAGE_KEY)));
  } catch {
    return { ...DEFAULT_APPEARANCE };
  }
}

export function saveAppearance(appearance) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(sanitizeAppearance(appearance)));
  } catch {
    // Storage blocked: the choice still applies for this run.
  }
}

/** The accent color a theme + choice resolves to. */
export function accentOf(theme, choice) {
  const name = choice && choice !== "theme" ? choice : theme.accent;
  if (name === "text") return theme.text;
  if (name === "mauve") return theme.purple;
  return theme[name] ?? theme.blue;
}

/** Everything the window needs: CSS variables and the OS glass request. */
export function resolveAppearance(value) {
  const appearance = sanitizeAppearance(value);
  const theme = themeById(appearance.theme);
  const accent = accentOf(theme, appearance.accent);
  const windowAlpha = appearance.transparency ? appearance.opacity : 1;
  const vars = {
    "--bg-rgb": triplet(theme.bg),
    "--surface-rgb": triplet(theme.surface),
    "--surface2-rgb": triplet(theme.surface2),
    "--fg-rgb": triplet(theme.text),
    "--text": theme.text,
    "--muted": theme.muted,
    "--accent-color": accent,
    "--on-accent": luminance(accent) > 0.4 ? theme.dark ? theme.bg : "#111111" : "#ffffff",
    "--shade-rgb": theme.dark ? "0 0 0" : triplet(theme.text),
    "--shade-k": theme.dark ? "1" : "0.3",
    "--window-bg": `rgb(${triplet(theme.bg)} / ${windowAlpha})`,
    "--card-alpha": String(appearance.transparency ? appearance.cardOpacity : 1),
    "--card-blur-px": `${appearance.transparency ? appearance.cardBlur : 0}px`,
    "--good": theme.green,
    "--warn": theme.yellow,
    "--bad": theme.red,
    "--info": theme.blue,
  };
  for (const hue of HUES) vars[`--c-${hue}`] = theme[hue];
  return {
    appearance,
    theme,
    accent,
    vars,
    colorScheme: theme.dark ? "dark" : "light",
    glass: {
      ...hexToRgb(theme.bg),
      alpha: windowAlpha,
      // OS frosted glass only matters while the window is see-through.
      blur: appearance.transparency && appearance.blur,
    },
  };
}

/** Write the CSS variables onto the document. */
export function applyAppearanceVars(resolved) {
  const root = document.documentElement;
  for (const [name, value] of Object.entries(resolved.vars)) {
    root.style.setProperty(name, value);
  }
  root.style.colorScheme = resolved.colorScheme;
  root.dataset.theme = resolved.theme.id;
}
