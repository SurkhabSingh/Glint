/**
 * Dashboard arithmetic: periods, buckets and groups. Pure functions over
 * the rows `glint_usage` returns, so the chart and the totals always agree.
 * Time is counted from each activity's active segments, clipped to every
 * bucket they touch, so an activity that crosses an hour or midnight is
 * split exactly where it crossed.
 */

export const RANGES = [
  { id: "hour", label: "Hour" },
  { id: "day", label: "Day" },
  { id: "week", label: "Week" },
  { id: "month", label: "Month" },
  { id: "year", label: "Year" },
];

const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
const DAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

/**
 * Activity division. Fifteen categories are too many hues to tell apart,
 * so they fold into eight fixed kinds; each kind always has the same color,
 * whatever else is on screen. The exact categories stay in the tooltip.
 */
export const CATEGORY_GROUPS = [
  { id: "work", label: "Coding & docs", color: "var(--c-blue)", categories: ["Coding", "Docs", "Files", "Assistant"] },
  { id: "browsing", label: "Browsing", color: "var(--c-teal)", categories: ["Browsing"] },
  { id: "talk", label: "Email & chat", color: "var(--c-pink)", categories: ["Email", "Chat"] },
  { id: "study", label: "Study", color: "var(--c-green)", categories: ["Learning"] },
  { id: "media", label: "Video & music", color: "var(--c-orange)", categories: ["Video", "Music"] },
  { id: "games", label: "Gaming", color: "var(--c-purple)", categories: ["Game"] },
  { id: "creative", label: "Design & photo", color: "var(--c-yellow)", categories: ["Design", "Photo"] },
  { id: "finance", label: "Finance", color: "var(--c-red)", categories: ["Finance"] },
  { id: "other", label: "Other", color: "rgb(var(--fg-rgb) / 0.38)", categories: ["Other"] },
];

export const MODE_GROUPS = [
  { id: "Read", label: "Read", color: "var(--c-blue)" },
  { id: "Make", label: "Make", color: "var(--c-purple)" },
  { id: "Play", label: "Play", color: "var(--c-green)" },
  { id: "Watch", label: "Watch", color: "var(--c-orange)" },
  { id: "Private", label: "Private", color: "var(--c-yellow)" },
];

export const CATEGORY_NAMES = {
  Other: "Other", Browsing: "Browsing", Email: "Email", Chat: "Chat",
  Learning: "Study", Video: "Video", Music: "Music", Game: "Gaming",
  Finance: "Finance", Coding: "Coding", Docs: "Documents", Design: "Design",
  Photo: "Photo editing", Files: "Files", Assistant: "AI assistant",
};

export function groupsFor(by) {
  return by === "mode" ? MODE_GROUPS : CATEGORY_GROUPS;
}

export function groupOf(activity, by) {
  if (by === "mode") {
    return MODE_GROUPS.some((g) => g.id === activity.mode) ? activity.mode : "Read";
  }
  const found = CATEGORY_GROUPS.find((g) => g.categories.includes(activity.category));
  return found ? found.id : "other";
}

/** The start of the period containing `anchor`. */
export function periodStart(range, anchor) {
  const d = new Date(anchor);
  switch (range) {
    case "hour":
      d.setMinutes(0, 0, 0);
      break;
    case "day":
      d.setHours(0, 0, 0, 0);
      break;
    case "week": {
      d.setHours(0, 0, 0, 0);
      const back = (d.getDay() + 6) % 7; // weeks start on Monday
      d.setDate(d.getDate() - back);
      break;
    }
    case "month":
      d.setHours(0, 0, 0, 0);
      d.setDate(1);
      break;
    default:
      d.setHours(0, 0, 0, 0);
      d.setMonth(0, 1);
  }
  return d.getTime();
}

/** Move a period start by `steps` whole periods (calendar-aware). */
export function shiftPeriod(range, start, steps) {
  const d = new Date(start);
  switch (range) {
    case "hour":
      d.setHours(d.getHours() + steps);
      break;
    case "day":
      d.setDate(d.getDate() + steps);
      break;
    case "week":
      d.setDate(d.getDate() + 7 * steps);
      break;
    case "month":
      d.setMonth(d.getMonth() + steps, 1);
      break;
    default:
      d.setFullYear(d.getFullYear() + steps, 0, 1);
  }
  return d.getTime();
}

const pad = (n) => String(n).padStart(2, "0");

function hourLabel(hours, minutes = 0) {
  const suffix = hours >= 12 ? "PM" : "AM";
  const h = hours % 12 === 0 ? 12 : hours % 12;
  return minutes ? `${h}:${pad(minutes)} ${suffix}` : `${h} ${suffix}`;
}

/**
 * The bars of a period: their boundaries (computed with calendar math so a
 * DST day has 23 or 25 hours), axis labels and which labels to print.
 */
export function buildBuckets(range, start) {
  const end = shiftPeriod(range, start, 1);
  const buckets = [];
  const d = new Date(start);
  while (d.getTime() < end) {
    const from = d.getTime();
    let label;
    let tick;
    let title;
    switch (range) {
      case "hour": {
        label = hourLabel(d.getHours(), d.getMinutes());
        tick = d.getMinutes() % 15 === 0;
        title = label;
        d.setMinutes(d.getMinutes() + 5);
        break;
      }
      case "day": {
        label = hourLabel(d.getHours());
        tick = d.getHours() % 6 === 0;
        title = `${hourLabel(d.getHours())} – ${hourLabel((d.getHours() + 1) % 24)}`;
        d.setHours(d.getHours() + 1);
        break;
      }
      case "week":
        label = DAYS[d.getDay()];
        tick = true;
        title = `${DAYS[d.getDay()]}, ${MONTHS[d.getMonth()]} ${d.getDate()}`;
        d.setDate(d.getDate() + 1);
        break;
      case "month":
        label = String(d.getDate());
        tick = d.getDate() === 1 || d.getDate() % 7 === 1;
        title = `${DAYS[d.getDay()]}, ${MONTHS[d.getMonth()]} ${d.getDate()}`;
        d.setDate(d.getDate() + 1);
        break;
      default:
        label = MONTHS[d.getMonth()];
        tick = true;
        title = `${MONTHS[d.getMonth()]} ${d.getFullYear()}`;
        d.setMonth(d.getMonth() + 1, 1);
    }
    buckets.push({ from, to: Math.min(d.getTime(), end), label, tick, title });
  }
  return buckets;
}

/** What a bar opens when clicked: the next finer view of that bar. */
export function drillTarget(range) {
  return { day: "hour", week: "day", month: "day", year: "month" }[range] ?? null;
}

/** "Today", "Wed, Oct 8", "Oct 6 – 12", "October 2026", "2 PM – 3 PM". */
export function periodLabel(range, start, now = Date.now()) {
  const d = new Date(start);
  const current = periodStart(range, now) === start;
  switch (range) {
    case "hour":
      return `${current ? "This hour · " : ""}${hourLabel(d.getHours())} – ${hourLabel((d.getHours() + 1) % 24)}, ${MONTHS[d.getMonth()]} ${d.getDate()}`;
    case "day": {
      if (current) return "Today";
      if (periodStart("day", shiftPeriod("day", now, -1)) === start) return "Yesterday";
      return `${DAYS[d.getDay()]}, ${MONTHS[d.getMonth()]} ${d.getDate()}${d.getFullYear() !== new Date(now).getFullYear() ? `, ${d.getFullYear()}` : ""}`;
    }
    case "week": {
      const last = new Date(shiftPeriod("week", start, 1) - 1);
      const span = last.getMonth() === d.getMonth()
        ? `${MONTHS[d.getMonth()]} ${d.getDate()} – ${last.getDate()}`
        : `${MONTHS[d.getMonth()]} ${d.getDate()} – ${MONTHS[last.getMonth()]} ${last.getDate()}`;
      return current ? `This week · ${span}` : span;
    }
    case "month":
      return `${new Date(start).toLocaleString([], { month: "long" })} ${d.getFullYear()}`;
    default:
      return String(d.getFullYear());
  }
}

/** The previous period, in words, for the comparison line. */
export function previousName(range, start, now = Date.now()) {
  const current = periodStart(range, now) === start;
  if (!current) return { hour: "the hour before", day: "the day before", week: "the week before", month: "the month before", year: "the year before" }[range];
  return { hour: "the previous hour", day: "yesterday", week: "last week", month: "last month", year: "last year" }[range];
}

/** Clip [a, b) to [from, to); 0 when they don't meet. */
const overlap = (a, b, from, to) => Math.max(0, Math.min(b, to) - Math.max(a, from));

/**
 * Everything the page shows for one period, from the raw rows:
 * per-bar totals by group, group totals, top apps and top activities.
 */
export function summarize(rows, range, start, by) {
  const buckets = buildBuckets(range, start).map((bucket) => ({ ...bucket, total: 0, groups: {}, categories: {} }));
  const from = buckets[0].from;
  const to = buckets[buckets.length - 1].to;
  const groupTotals = {};
  const categoryTotals = {};
  const apps = new Map();
  const activities = [];
  let total = 0;
  let longest = 0;

  for (const row of rows ?? []) {
    const group = groupOf(row, by);
    let inside = 0;
    for (const [a, b] of row.segments ?? []) {
      if (!(b > a)) continue;
      const clipped = overlap(a, b, from, to);
      if (clipped <= 0) continue;
      inside += clipped;
      longest = Math.max(longest, clipped);
      // Buckets are contiguous and sorted: walk only the ones it touches.
      for (const bucket of buckets) {
        if (bucket.from >= b) break;
        const part = overlap(a, b, bucket.from, bucket.to);
        if (part <= 0) continue;
        bucket.total += part;
        bucket.groups[group] = (bucket.groups[group] ?? 0) + part;
        bucket.categories[row.category] = (bucket.categories[row.category] ?? 0) + part;
      }
    }
    if (inside <= 0) continue;
    total += inside;
    groupTotals[group] = (groupTotals[group] ?? 0) + inside;
    categoryTotals[row.category] = (categoryTotals[row.category] ?? 0) + inside;
    const appName = row.site || row.app || "Unknown";
    const app = apps.get(appName) ?? { name: appName, ms: 0, via: row.site ? row.app : null, mode: row.mode, group };
    app.ms += inside;
    apps.set(appName, app);
    activities.push({ id: row.id, label: row.label || row.subject || appName, source: appName, mode: row.mode, group, ms: inside });
  }

  const groups = groupsFor(by)
    .map((g) => ({
      ...g,
      ms: groupTotals[g.id] ?? 0,
      categories: (g.categories ?? [])
        .map((c) => ({ id: c, label: CATEGORY_NAMES[c] ?? c, ms: categoryTotals[c] ?? 0 }))
        .filter((c) => c.ms > 0)
        .sort((a, b) => b.ms - a.ms),
    }))
    .filter((g) => g.ms > 0);

  const busiest = buckets.reduce((best, bucket) => (bucket.total > (best?.total ?? 0) ? bucket : best), null);

  return {
    buckets,
    total,
    groups,
    longest,
    busiest,
    count: activities.length,
    apps: [...apps.values()].sort((a, b) => b.ms - a.ms),
    activities: activities.sort((a, b) => b.ms - a.ms),
  };
}

/** Total active time of rows inside [from, to). */
export function totalBetween(rows, from, to) {
  let sum = 0;
  for (const row of rows ?? []) {
    for (const [a, b] of row.segments ?? []) sum += overlap(a, b, from, to);
  }
  return sum;
}

/** A readable y-axis top and step for the tallest bar. */
export function niceScale(maxMs, range) {
  const minute = 60_000;
  const capacity = { hour: 5, day: 60 }[range];
  const max = Math.max(maxMs / minute, 1);
  const steps = [1, 2, 5, 10, 15, 20, 30, 60, 120, 180, 240, 360, 480, 720, 1440, 2880, 7200, 14400];
  const step = steps.find((s) => max / s <= 4) ?? steps[steps.length - 1];
  let top = Math.ceil(max / step) * step;
  // A bar can't hold more than its own length of time; keep the axis there.
  if (capacity && capacity >= max) top = Math.min(top, capacity);
  const ticks = [];
  for (let t = step; t <= top + 1e-9; t += step) ticks.push(t * minute);
  if (ticks.length === 0 || ticks[ticks.length - 1] !== top * minute) ticks.push(top * minute);
  return { top: top * minute, ticks };
}

/** "8 hours 24 minutes", for the headline. */
export function longDuration(ms) {
  const minutes = Math.round(ms / 60_000);
  if (ms <= 0) return "Nothing recorded";
  if (minutes < 1) return "Under a minute";
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  const hours = h ? `${h.toLocaleString()} ${h === 1 ? "hour" : "hours"}` : "";
  const mins = m ? `${m} ${m === 1 ? "minute" : "minutes"}` : "";
  return [hours, mins].filter(Boolean).join(" ");
}

/** "1 h 12 min", "45 min", "<1 min", for labels and tooltips. */
export function shortDuration(ms) {
  const minutes = Math.round(ms / 60_000);
  if (minutes < 1) return ms > 0 ? "<1 min" : "0 min";
  if (minutes < 60) return `${minutes} min`;
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return m ? `${h.toLocaleString()} h ${m} min` : `${h.toLocaleString()} h`;
}

/** Axis tick text: "15m", "1h", "1h 20m", "2d". */
export function axisDuration(ms) {
  const minutes = Math.round(ms / 60_000);
  if (minutes < 60) return `${minutes}m`;
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return m ? `${h}h ${m}m` : `${h}h`;
}
