import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { glintUsage } from "../glint";
import {
  CATEGORY_NAMES,
  RANGES,
  axisDuration,
  drillTarget,
  groupsFor,
  longDuration,
  niceScale,
  periodLabel,
  periodStart,
  previousName,
  shiftPeriod,
  shortDuration,
  summarize,
  totalBetween,
} from "../usage";

const GROUPINGS = [
  { id: "category", label: "Activity" },
  { id: "mode", label: "Mode" },
];

const MODE_NAMES = { Read: "Read", Make: "Make", Play: "Play", Watch: "Watch", Private: "Private" };

function loadPrefs() {
  try {
    const saved = JSON.parse(localStorage.getItem("glint-dashboard") ?? "{}");
    return {
      range: RANGES.some((r) => r.id === saved.range) ? saved.range : "day",
      by: GROUPINGS.some((g) => g.id === saved.by) ? saved.by : "category",
    };
  } catch {
    return { range: "day", by: "category" };
  }
}

function Segmented({ options, value, onChange, label }) {
  return (
    <div className="dash-seg" role="radiogroup" aria-label={label}>
      {options.map((option) => (
        <button
          key={option.id}
          role="radio"
          aria-checked={value === option.id}
          className={value === option.id ? "active" : ""}
          onClick={() => onChange(option.id)}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}

/** Stacked bars, one per bucket, with a hover/focus tooltip. */
function UsageChart({ data, range, by, onDrill }) {
  const [hover, setHover] = useState(null);
  const groups = groupsFor(by);
  const scale = niceScale(Math.max(0, ...data.buckets.map((b) => b.total)), range);
  const canDrill = Boolean(drillTarget(range));
  const now = Date.now();

  return (
    <div className="dash-chart" onMouseLeave={() => setHover(null)}>
      <div className="dash-plot">
        <div className="dash-grid" aria-hidden="true">
          {scale.ticks.map((tick) => (
            <div key={tick} className="dash-gridline" style={{ bottom: `${(tick / scale.top) * 100}%` }}>
              <span>{axisDuration(tick)}</span>
            </div>
          ))}
        </div>
        <div className="dash-bars" style={{ gridTemplateColumns: `repeat(${data.buckets.length}, minmax(0, 1fr))` }}>
          {data.buckets.map((bucket, index) => {
            const future = bucket.from > now;
            const stack = groups.filter((g) => (bucket.groups[g.id] ?? 0) > 0);
            const height = scale.top > 0 ? (bucket.total / scale.top) * 100 : 0;
            return (
              <button
                key={bucket.from}
                className={`dash-col ${future ? "future" : ""} ${hover === index ? "hover" : ""}`}
                onMouseEnter={() => setHover(index)}
                onFocus={() => setHover(index)}
                onBlur={() => setHover(null)}
                onClick={() => canDrill && bucket.total > 0 && onDrill(bucket)}
                disabled={future}
                aria-label={`${bucket.title}: ${bucket.total > 0 ? shortDuration(bucket.total) : "nothing recorded"}`}
                style={{ cursor: canDrill && bucket.total > 0 ? "pointer" : "default" }}
              >
                <span className="dash-stack" style={{ height: `${Math.min(height, 100)}%` }}>
                  {stack.map((g) => (
                    <span
                      key={g.id}
                      className="dash-seg-fill"
                      style={{ flexGrow: bucket.groups[g.id], background: g.color }}
                    />
                  ))}
                </span>
              </button>
            );
          })}
        </div>
        {hover != null && data.buckets[hover] && (
          <ChartTooltip bucket={data.buckets[hover]} index={hover} count={data.buckets.length} groups={groups} by={by} canDrill={canDrill} />
        )}
      </div>
      <div className="dash-axis" style={{ gridTemplateColumns: `repeat(${data.buckets.length}, minmax(0, 1fr))` }} aria-hidden="true">
        {data.buckets.map((bucket) => (
          <span key={bucket.from}>{bucket.tick ? bucket.label : ""}</span>
        ))}
      </div>
    </div>
  );
}

function ChartTooltip({ bucket, index, count, groups, by, canDrill }) {
  const left = ((index + 0.5) / count) * 100;
  const rows = groups.filter((g) => (bucket.groups[g.id] ?? 0) > 0).sort((a, b) => bucket.groups[b.id] - bucket.groups[a.id]);
  const align = left < 22 ? "start" : left > 78 ? "end" : "center";
  return (
    <div className={`dash-tip ${align}`} style={{ left: `${left}%` }} role="status">
      <div className="dash-tip-title">{bucket.title}</div>
      <div className="dash-tip-total">{bucket.total > 0 ? shortDuration(bucket.total) : "Nothing recorded"}</div>
      {rows.map((g) => (
        <div key={g.id} className="dash-tip-row">
          <span className="dash-swatch" style={{ background: g.color }} />
          <span className="dash-tip-name">{g.label}</span>
          <span className="dash-tip-value">{shortDuration(bucket.groups[g.id])}</span>
        </div>
      ))}
      {by === "category" && rows.length > 0 && (
        <div className="dash-tip-detail">
          {Object.entries(bucket.categories)
            .sort((a, b) => b[1] - a[1])
            .slice(0, 4)
            .map(([category, ms]) => `${CATEGORY_NAMES[category] ?? category} ${shortDuration(ms)}`)
            .join(" · ")}
        </div>
      )}
      {canDrill && bucket.total > 0 && <div className="dash-tip-hint">Click to open</div>}
    </div>
  );
}

function DashboardPage() {
  const [prefs, setPrefs] = useState(loadPrefs);
  const [start, setStart] = useState(() => periodStart(loadPrefs().range, Date.now()));
  const [rows, setRows] = useState([]);
  const [loaded, setLoaded] = useState(false);
  const [error, setError] = useState("");
  const request = useRef(0);

  const { range, by } = prefs;
  const end = shiftPeriod(range, start, 1);
  const previousStart = shiftPeriod(range, start, -1);
  const isCurrent = periodStart(range, Date.now()) === start;

  useEffect(() => {
    try {
      localStorage.setItem("glint-dashboard", JSON.stringify(prefs));
    } catch {
      // Not remembered; still works.
    }
  }, [prefs]);

  const load = useCallback(async () => {
    const id = ++request.current;
    try {
      // One read covers this period and the one before, for the comparison.
      const response = await glintUsage(previousStart, end);
      if (id !== request.current) return;
      setRows(response?.activities ?? []);
      setError("");
    } catch (failure) {
      if (id !== request.current) return;
      const message = String(failure?.message ?? failure);
      // An older CLI prints its help for a verb it doesn't know.
      setError(
        /no JSON output/i.test(message)
          ? "Glint's background tool is older than this screen. Restart Glint and it rebuilds itself (or run `dotnet build src/Glint.Phase0.Cli -c Release` from the repo)."
          : message,
      );
    } finally {
      if (id === request.current) setLoaded(true);
    }
  }, [previousStart, end]);

  useEffect(() => {
    load();
  }, [load]);

  // New captures are grouped while scanning; refresh when they land.
  useEffect(() => {
    let unlisten;
    listen("sessions-updated", () => load()).then((fn) => (unlisten = fn)).catch(() => {});
    return () => unlisten?.();
  }, [load]);

  const data = useMemo(() => summarize(rows, range, start, by), [rows, range, start, by]);
  const previousTotal = useMemo(() => totalBetween(rows, previousStart, start), [rows, previousStart, start]);

  function setRange(next) {
    setPrefs((p) => ({ ...p, range: next }));
    // Keep looking at the same moment: the period that contains this one's start,
    // or now when this is the current period.
    setStart(periodStart(next, isCurrent ? Date.now() : start));
  }

  function drill(bucket) {
    const target = drillTarget(range);
    if (!target) return;
    setPrefs((p) => ({ ...p, range: target }));
    setStart(periodStart(target, bucket.from));
  }

  const diff = data.total - previousTotal;
  const comparison =
    previousTotal === 0 && data.total === 0
      ? ""
      : previousTotal === 0
        ? `Nothing recorded ${previousName(range, start)}`
        : Math.abs(diff) < 60_000
          ? `About the same as ${previousName(range, start)}`
          : `${shortDuration(Math.abs(diff))} ${diff > 0 ? "more" : "less"} than ${previousName(range, start)}`;

  const topGroup = [...data.groups].sort((a, b) => b.ms - a.ms)[0];

  return (
    <div className="glint-page">
      <div className="glint-page-inner">
        <div className="dash-head">
          <h1 className="glint-title">Dashboard</h1>
          <div className="dash-controls">
            <Segmented options={RANGES} value={range} onChange={setRange} label="Period length" />
          </div>
        </div>

        <section className="glint-card dash-hero">
          <div className="dash-hero-top">
            <div className="dash-period">
              <button className="dash-nav" onClick={() => setStart(previousStart)} aria-label="Previous period" title="Previous">
                ‹
              </button>
              <span className="dash-period-label">{periodLabel(range, start)}</span>
              <button className="dash-nav" onClick={() => setStart(end)} disabled={end > Date.now()} aria-label="Next period" title="Next">
                ›
              </button>
              {!isCurrent && (
                <button className="dash-now" onClick={() => setStart(periodStart(range, Date.now()))}>
                  Now
                </button>
              )}
            </div>
            <Segmented options={GROUPINGS} value={by} onChange={(next) => setPrefs((p) => ({ ...p, by: next }))} label="Divide by" />
          </div>

          <div className="dash-total">{!loaded ? "Loading…" : error ? "—" : longDuration(data.total)}</div>
          {comparison && !error && <div className="dash-compare">{comparison}</div>}
          {error && <div className="dash-error">Couldn't load usage: {error}</div>}

          <UsageChart data={data} range={range} by={by} onDrill={drill} />

          {data.groups.length > 0 && (
            <div className="dash-legend">
              {data.groups.map((g) => (
                <span key={g.id} className="dash-legend-item">
                  <span className="dash-swatch" style={{ background: g.color }} />
                  {g.label}
                  <span className="dash-legend-value">{shortDuration(g.ms)}</span>
                </span>
              ))}
            </div>
          )}
        </section>

        {data.total > 0 && (
          <>
            <div className="dash-stats">
              <div className="dash-stat">
                <span className="dash-stat-label">Activities</span>
                <span className="dash-stat-value">{data.count}</span>
              </div>
              <div className="dash-stat">
                <span className="dash-stat-label">Most time on</span>
                <span className="dash-stat-value">{topGroup?.label ?? "—"}</span>
              </div>
              <div className="dash-stat">
                <span className="dash-stat-label">Busiest</span>
                <span className="dash-stat-value">{data.busiest ? data.busiest.title : "—"}</span>
              </div>
              <div className="dash-stat">
                <span className="dash-stat-label">Longest stretch</span>
                <span className="dash-stat-value">{shortDuration(data.longest)}</span>
              </div>
            </div>

            <div className="dash-columns">
              <section className="glint-card dash-list-card">
                <h3>Where the time went</h3>
                <ul className="dash-list">
                  {[...data.groups].sort((a, b) => b.ms - a.ms).map((g) => (
                    <li key={g.id}>
                      <div className="dash-list-row">
                        <span className="dash-swatch" style={{ background: g.color }} />
                        <span className="dash-list-name">{g.label}</span>
                        <span className="dash-list-value">
                          {shortDuration(g.ms)}
                          <span className="dash-list-pct">{Math.round((g.ms / data.total) * 100)}%</span>
                        </span>
                      </div>
                      <div className="dash-meter">
                        <span style={{ width: `${(g.ms / data.total) * 100}%`, background: g.color }} />
                      </div>
                      {g.categories?.length > 1 && (
                        <div className="dash-list-sub">
                          {g.categories.map((c) => `${c.label} ${shortDuration(c.ms)}`).join(" · ")}
                        </div>
                      )}
                    </li>
                  ))}
                </ul>
              </section>

              <section className="glint-card dash-list-card">
                <h3>Top apps &amp; sites</h3>
                <ul className="dash-list">
                  {data.apps.slice(0, 8).map((app) => (
                    <li key={app.name}>
                      <div className="dash-list-row">
                        <span className="dash-list-name" title={app.via ? `${app.name} in ${app.via}` : app.name}>
                          {app.name}
                          {app.via && <span className="dash-list-via"> · {app.via}</span>}
                        </span>
                        <span className="dash-list-value">{shortDuration(app.ms)}</span>
                      </div>
                      <div className="dash-meter">
                        <span style={{ width: `${(app.ms / data.apps[0].ms) * 100}%` }} />
                      </div>
                    </li>
                  ))}
                </ul>
              </section>
            </div>

            <section className="glint-card dash-list-card">
              <h3>Longest activities</h3>
              <ul className="dash-activities">
                {data.activities.slice(0, 8).map((activity) => {
                  const group = groupsFor(by).find((g) => g.id === activity.group);
                  return (
                    <li key={activity.id}>
                      <span className="dash-swatch" style={{ background: group?.color }} />
                      <span className="dash-activity-label" title={activity.label}>
                        {activity.label}
                      </span>
                      <span className="dash-activity-meta">
                        {MODE_NAMES[activity.mode] ?? activity.mode} · {activity.source}
                      </span>
                      <span className="dash-list-value">{shortDuration(activity.ms)}</span>
                    </li>
                  );
                })}
              </ul>
            </section>
          </>
        )}

        {loaded && data.total === 0 && !error && (
          <p className="glint-hint dash-empty">
            Nothing was recorded {range === "day" && isCurrent ? "today" : "in this period"} yet. Start scanning on the Activity page and it will show up here.
          </p>
        )}
      </div>
    </div>
  );
}

export default DashboardPage;
