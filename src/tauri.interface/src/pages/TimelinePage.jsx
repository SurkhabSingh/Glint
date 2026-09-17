import { useEffect, useRef, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import {
  formatClock,
  formatTimestamp,
  glintHistory,
  glintTimeline,
} from "../glint";
import ScanCard from "../components/ScanCard";

function pad(n) {
  return String(n).padStart(2, "0");
}

/** Local YYYY-MM-DD (matches the Rust local_day device-tz bucketing). */
function dayKey(ms) {
  const d = new Date(Number(ms));
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

function todayKey() {
  return dayKey(Date.now());
}

function addDaysStr(dayStr, n) {
  const [y, m, d] = dayStr.split("-").map(Number);
  const date = new Date(y, m - 1, d);
  date.setDate(date.getDate() + n);
  return dayKey(date.getTime());
}

function prettyDay(dayStr) {
  const [y, m, d] = dayStr.split("-").map(Number);
  return new Date(y, m - 1, d).toLocaleDateString(undefined, {
    weekday: "long",
    month: "long",
    day: "numeric",
  });
}

function weekDays(dayStr) {
  const [y, m, d] = dayStr.split("-").map(Number);
  const date = new Date(y, m - 1, d);
  const mondayOffset = (date.getDay() + 6) % 7;
  const monday = new Date(date);
  monday.setDate(date.getDate() - mondayOffset);
  return Array.from({ length: 7 }, (_, i) => {
    const day = new Date(monday);
    day.setDate(monday.getDate() + i);
    return dayKey(day.getTime());
  });
}

function monthCells(year, month) {
  // Monday-first grid; null pads days outside the month.
  const first = new Date(year, month, 1);
  const lead = (first.getDay() + 6) % 7;
  const days = new Date(year, month + 1, 0).getDate();
  const cells = Array(lead).fill(null);
  for (let d = 1; d <= days; d += 1) {
    cells.push(`${year}-${pad(month + 1)}-${pad(d)}`);
  }
  return cells;
}

function dwellText(ms) {
  if (ms == null || ms < 0) return "";
  const s = ms / 1000;
  if (s < 60) return ` · ${s.toFixed(1)}s`;
  return ` · ${Math.floor(s / 60)}m ${Math.round(s % 60)}s`;
}

function hourMinute(ms) {
  const d = new Date(Number(ms));
  let h = d.getHours();
  const ampm = h >= 12 ? "PM" : "AM";
  h = h % 12;
  if (h === 0) h = 12;
  return `${h}:${String(d.getMinutes()).padStart(2, "0")} ${ampm}`;
}

/**
 * Clusters consecutive scans sharing a sessionId (sessions are
 * time-contiguous, so same-session scans are always adjacent).
 * Singletons and ungrouped (null) scans render flat, as before.
 */
function clusterSessions(scans) {
  const clusters = [];
  for (const scan of scans) {
    const sid = scan.sessionId ?? null;
    const last = clusters[clusters.length - 1];
    if (sid && last && last.sessionId === sid) {
      last.scans.push(scan);
    } else {
      clusters.push({ sessionId: sid, scans: [scan] });
    }
  }
  return clusters;
}

function SessionHeader({ scans }) {
  const times = scans
    .map((s) => Number(s.capturedAtMilliseconds))
    .sort((a, b) => a - b);
  const process = scans[0]?.processName ?? "unknown";
  const range =
    times.length > 1
      ? `${hourMinute(times[0])}–${hourMinute(times[times.length - 1])}`
      : hourMinute(times[0]);
  return (
    <div className="session-header">
      <span className="session-tick" aria-hidden="true" />
      <span>
        Session · {process} · {range} · {scans.length} scan
        {scans.length === 1 ? "" : "s"}
      </span>
    </div>
  );
}

function renderScanList(scans, emptyText) {
  if (scans.length === 0) {
    return (
      <div className="scan-list">
        <div className="scan-card">
          <div className="scan-summary">{emptyText}</div>
        </div>
      </div>
    );
  }
  return (
    <div className="scan-list">
      {clusterSessions(scans).map((cluster) =>
        cluster.sessionId && cluster.scans.length > 1 ? (
          <div key={cluster.sessionId}>
            <SessionHeader scans={cluster.scans} />
            {cluster.scans.map((scan) => (
              <ScanCard key={scan.id} scan={scan} />
            ))}
          </div>
        ) : (
          cluster.scans.map((scan) => <ScanCard key={scan.id} scan={scan} />)
        )
      )}
    </div>
  );
}

/**
 * Grouped history: Hour / Day / Week / Month over scans + timeline events.
 * Day is the default view. The Hour view toggles Summarized (scan cards)
 * and Timeline (event rows). Stopping a recording keeps everything shown.
 */
function TimelinePage() {
  const [granularity, setGranularity] = useState("day");
  const [dayStr, setDayStr] = useState(todayKey());
  const [hour, setHour] = useState(new Date().getHours());
  const [hourTab, setHourTab] = useState("summarized");
  const [history, setHistory] = useState([]);
  const [eventsCache, setEventsCache] = useState({});
  const now = new Date();
  const [monthCursor, setMonthCursor] = useState({
    year: now.getFullYear(),
    month: now.getMonth(),
  });

  useEffect(() => {
    let cancelled = false;
    glintHistory(500)
      .then((response) => {
        if (!cancelled) setHistory(response.history ?? []);
      })
      .catch(() => {
        if (!cancelled) setHistory([]);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const loadedDays = useRef({});

  useEffect(() => {
    if (loadedDays.current[dayStr]) return;
    loadedDays.current[dayStr] = true;
    glintTimeline(dayStr)
      .then((response) => {
        setEventsCache((prev) => ({
          ...prev,
          [dayStr]: response.events ?? [],
        }));
      })
      .catch(() => {
        setEventsCache((prev) => ({ ...prev, [dayStr]: [] }));
      });
  }, [dayStr]);

  // Live event stream merges into the cached day (stops on pause: the
  // backend simply stops emitting, nothing is cleared).
  useEffect(() => {
    let unlisten;
    listen("timeline-event", (event) => {
      const row = event.payload;
      if (!row || row.ts_wall_ms == null) return;
      const day = dayKey(row.ts_wall_ms);
      setEventsCache((prev) => {
        const list = prev[day] ?? [];
        if (list.some((r) => r.id != null && r.id === row.id)) return prev;
        return { ...prev, [day]: [...list, row] };
      });
    }).then((fn) => (unlisten = fn));
    return () => unlisten?.();
  }, []);

  // Live scan completions refresh the summarized side.
  useEffect(() => {
    let unlisten;
    listen("scan-outcome", (event) => {
      const record = event.payload?.record;
      if (!record) return;
      setHistory((prev) => {
        if (prev.some((s) => s.id === record.id)) return prev;
        return [record, ...prev].slice(0, 500);
      });
    }).then((fn) => (unlisten = fn));
    return () => unlisten?.();
  }, []);

  const scansByDay = {};
  for (const scan of history) {
    const day = dayKey(scan.capturedAtMilliseconds);
    (scansByDay[day] = scansByDay[day] || []).push(scan);
  }

  function scansInHour(day, hourValue) {
    return (scansByDay[day] ?? []).filter(
      (s) => new Date(Number(s.capturedAtMilliseconds)).getHours() === hourValue
    );
  }

  function eventsInHour(day, hourValue) {
    const rows = [...(eventsCache[day] ?? [])];
    const inHour = rows.filter(
      (r) => new Date(Number(r.ts_wall_ms)).getHours() === hourValue
    );
    for (const scan of scansInHour(day, hourValue)) {
      inHour.push({
        id: `scan-${scan.id}`,
        kind: "scan.completed",
        ts_wall_ms: scan.capturedAtMilliseconds,
        process: scan.processName,
        title: scan.windowTitle,
        label: scan.label ?? "Gemma summary failed",
      });
    }
    return inHour.sort((a, b) => b.ts_wall_ms - a.ts_wall_ms);
  }

  function renderEventRow(row, index, list) {
    const key = row.id ?? `${row.ts_wall_ms}-${index}`;
    if (row.kind === "eon.started") {
      return (
        <div className="tl-row eon" key={key}>
          <span className="tl-time" title={formatTimestamp(row.ts_wall_ms)}>
            {formatClock(row.ts_wall_ms)}
          </span>
          <span className="tl-dot" />
          <span className="tl-text">
            <strong>EON began — recording started</strong>
          </span>
        </div>
      );
    }
    if (row.kind === "eon.ended") {
      const started = list.find(
        (r) => r.kind === "eon.started" && r.eon_id === row.eon_id
      );
      const span =
        started?.ts_wall_ms != null
          ? ` · ran ${dwellText(row.ts_wall_ms - started.ts_wall_ms).slice(3)}`
          : "";
      return (
        <div className="tl-row eon ended" key={key}>
          <span className="tl-time" title={formatTimestamp(row.ts_wall_ms)}>
            {formatClock(row.ts_wall_ms)}
          </span>
          <span className="tl-dot idle" />
          <span className="tl-text">
            <strong>EON ended{span}</strong>
          </span>
        </div>
      );
    }
    if (row.kind === "scan.started") {
      return (
        <div className="tl-row" key={key}>
          <span className="tl-time" title={formatTimestamp(row.ts_wall_ms)}>
            {formatClock(row.ts_wall_ms)}
          </span>
          <span className="tl-dot rec" />
          <span className="tl-text">
            <strong>Recording started</strong>
          </span>
        </div>
      );
    }
    if (row.kind === "scan.stopped" || row.kind === "scan.paused") {
      return (
        <div className="tl-row dim" key={key}>
          <span className="tl-time" title={formatTimestamp(row.ts_wall_ms)}>
            {formatClock(row.ts_wall_ms)}
          </span>
          <span className="tl-dot idle" />
          <span className="tl-text">
            <strong>
              {row.kind === "scan.stopped"
                ? "Recording stopped"
                : "Recording paused"}
            </strong>
          </span>
        </div>
      );
    }
    if (row.kind === "heartbeat") {
      return (
        <div className="tl-row dim" key={key}>
          <span className="tl-time">{formatClock(row.ts_wall_ms)}</span>
          <span className="tl-dot idle" />
          <span className="tl-text">Still in {row.process ?? "unknown"}</span>
        </div>
      );
    }
    if (row.kind === "scan.completed") {
      return (
        <div className="tl-row scan" key={key}>
          <span className="tl-time">{formatClock(row.ts_wall_ms)}</span>
          <span className="tl-dot done" />
          <span className="tl-text">
            <strong>{row.label}</strong>
            <span className="tl-sub">
              {row.process}
              {row.title ? ` | ${row.title}` : ""}
            </span>
          </span>
        </div>
      );
    }
    const newer = index > 0 ? list[index - 1] : null;
    const dwell =
      newer?.ts_wall_ms != null ? newer.ts_wall_ms - row.ts_wall_ms : null;
    return (
      <div className="tl-row" key={key}>
        <span className="tl-time" title={formatTimestamp(row.ts_wall_ms)}>
          {formatClock(row.ts_wall_ms)}
        </span>
        <span className="tl-dot" />
        <span className="tl-text">
          <strong>{row.process ?? "unknown"}</strong>
          {row.title ? ` — ${row.title}` : ""}
          {row.suppressed ? (
            <span className="tl-badge">suppressed ({row.suppressed})</span>
          ) : null}
          {row.suppressedDetail ? (
            <span className="tl-sub">{row.suppressedDetail}</span>
          ) : null}
          {dwell != null && dwell >= 0 ? (
            <span className="tl-sub">{dwellText(dwell).slice(3)} here</span>
          ) : null}
        </span>
      </div>
    );
  }

  const dayScans = scansByDay[dayStr] ?? [];
  const daySwitches = (eventsCache[dayStr] ?? []).filter(
    (r) => r.kind === "window.focused"
  ).length;

  const MARKER_KINDS = [
    "eon.started",
    "eon.ended",
    "scan.started",
    "scan.paused",
    "scan.stopped",
  ];

  // Summarized day view with EON/scan lifecycle markers interleaved at
  // their timestamps, so a run's begin/end reads as structure, not text.
  function renderDayList() {
    const markers = (eventsCache[dayStr] ?? [])
      .filter((r) => MARKER_KINDS.includes(r.kind))
      .map((r) => ({ marker: r, ts: Number(r.ts_wall_ms) }));
    if (dayScans.length === 0 && markers.length === 0) {
      return renderScanList(
        [],
        "No scans recorded this day. Start scanning to capture context."
      );
    }
    const items = [
      ...clusterSessions(dayScans).map((cluster) => ({
        cluster,
        ts: Math.max(
          ...cluster.scans.map((s) => Number(s.capturedAtMilliseconds))
        ),
      })),
      ...markers,
    ].sort((a, b) => b.ts - a.ts);
    return (
      <div className="scan-list">
        {items.map((item, i) => {
          if (item.marker) {
            return renderEventRow(
              item.marker,
              i,
              items.map((x) => x.marker ?? {})
            );
          }
          const cluster = item.cluster;
          return cluster.sessionId && cluster.scans.length > 1 ? (
            <div key={cluster.sessionId}>
              <SessionHeader scans={cluster.scans} />
              {cluster.scans.map((scan) => (
                <ScanCard key={scan.id} scan={scan} />
              ))}
            </div>
          ) : (
            cluster.scans.map((scan) => <ScanCard key={scan.id} scan={scan} />)
          );
        })}
      </div>
    );
  }

  return (
    <div className="glint-page">
      <div className="glint-page-inner narrow">
        <h1 className="glint-title">History</h1>
        <p className="glint-hint">
          Summarized notes grouped by hour, day, week, or month.
        </p>

        <div className="glint-btn-row seg-tabs" role="tablist" aria-label="Grouping">
          {["hour", "day", "week", "month"].map((g) => (
            <button
              key={g}
              role="tab"
              aria-selected={granularity === g}
              className={`glint-btn seg-btn ${granularity === g ? "primary" : ""}`}
              onClick={() => setGranularity(g)}
            >
              {g[0].toUpperCase() + g.slice(1)}
            </button>
          ))}
        </div>

        {granularity === "day" && (
          <>
            <div className="day-nav">
              <button className="glint-btn" onClick={() => setDayStr(addDaysStr(dayStr, -1))}>
                ‹ Prev
              </button>
              <strong>{prettyDay(dayStr)}</strong>
              <button className="glint-btn" onClick={() => setDayStr(addDaysStr(dayStr, 1))}>
                Next ›
              </button>
              <button
                className="glint-btn push-right"
                onClick={() => setDayStr(todayKey())}
              >
                Today
              </button>
            </div>
            <p className="glint-section-sub">
              {dayScans.length} scans · {daySwitches} window switches
            </p>
            {renderDayList()}
          </>
        )}

        {granularity === "hour" && (
          <>
            <div className="day-nav">
              <button className="glint-btn" onClick={() => setDayStr(addDaysStr(dayStr, -1))}>
                ‹ Prev
              </button>
              <strong>{prettyDay(dayStr)}</strong>
              <button className="glint-btn" onClick={() => setDayStr(addDaysStr(dayStr, 1))}>
                Next ›
              </button>
            </div>
            <div className="day-nav">
              <button
                className="glint-btn"
                onClick={() => setHour((h) => (h + 23) % 24)}
              >
                ‹ {pad(hour)}:00
              </button>
              <button
                className="glint-btn"
                onClick={() => setHour((h) => (h + 1) % 24)}
              >
                {pad((hour + 1) % 24)}:00 ›
              </button>
              <div
                className="view-switch push-right"
                role="group"
                aria-label="Hour view"
              >
                <button
                  className={`view-seg ${hourTab === "summarized" ? "active" : ""}`}
                  aria-pressed={hourTab === "summarized"}
                  title="Summarized"
                  onClick={() => setHourTab("summarized")}
                >
                  <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true">
                    <path d="M2 4h12M2 8h12M2 12h7" strokeLinecap="round" />
                  </svg>
                </button>
                <button
                  className={`view-seg ${hourTab === "timeline" ? "active" : ""}`}
                  aria-pressed={hourTab === "timeline"}
                  title="Timeline"
                  onClick={() => setHourTab("timeline")}
                >
                  <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true">
                    <path
                      d="M1.5 8h3l1.5-4 3 8 1.5-4h4"
                      strokeLinecap="round"
                      strokeLinejoin="round"
                    />
                  </svg>
                </button>
              </div>
            </div>
            {hourTab === "summarized" ? (
              renderScanList(
                scansInHour(dayStr, hour),
                "No summarized notes this hour. Switch to Timeline to see every switch that happened."
              )
            ) : (
              <div className="scan-list">
                {eventsInHour(dayStr, hour).length === 0 && (
                  <div className="scan-card">
                    <div className="scan-summary">
                      No switches logged this hour.
                    </div>
                  </div>
                )}
                {eventsInHour(dayStr, hour).map((row, index, list) =>
                  renderEventRow(row, index, list)
                )}
              </div>
            )}
          </>
        )}

        {granularity === "week" && (
          <>
            <div className="day-nav">
              <button className="glint-btn" onClick={() => setDayStr(addDaysStr(dayStr, -7))}>
                ‹ Prev week
              </button>
              <strong>
                Week of {prettyDay(weekDays(dayStr)[0])}
              </strong>
              <button className="glint-btn" onClick={() => setDayStr(addDaysStr(dayStr, 7))}>
                Next week ›
              </button>
            </div>
            <div className="scan-list">
              {weekDays(dayStr).map((day) => {
                const scans = scansByDay[day] ?? [];
                return (
                  <div className="scan-card" key={day}>
                    <div className="scan-label" style={{ fontSize: 16 }}>
                      {prettyDay(day)}
                    </div>
                    <div className="scan-source">
                      {scans.length} scan{scans.length === 1 ? "" : "s"}
                    </div>
                    <div className="glint-btn-row">
                      <button
                        className="glint-btn"
                        onClick={() => {
                          setDayStr(day);
                          setGranularity("day");
                        }}
                      >
                        Open day
                      </button>
                    </div>
                  </div>
                );
              })}
            </div>
          </>
        )}

        {granularity === "month" && (
          <>
            <div className="day-nav">
              <button
                className="glint-btn"
                onClick={() =>
                  setMonthCursor((c) => ({
                    year: c.month === 0 ? c.year - 1 : c.year,
                    month: (c.month + 11) % 12,
                  }))
                }
              >
                ‹ Prev
              </button>
              <strong>
                {new Date(monthCursor.year, monthCursor.month, 1).toLocaleDateString(
                  undefined,
                  { month: "long", year: "numeric" }
                )}
              </strong>
              <button
                className="glint-btn"
                onClick={() =>
                  setMonthCursor((c) => ({
                    year: c.month === 11 ? c.year + 1 : c.year,
                    month: (c.month + 1) % 12,
                  }))
                }
              >
                Next ›
              </button>
            </div>
            <div className="month-grid">
              {["M", "T", "W", "T", "F", "S", "S"].map((d, i) => (
                <span key={i} className="month-dow">
                  {d}
                </span>
              ))}
              {monthCells(monthCursor.year, monthCursor.month).map((day, i) => {
                if (!day) return <span key={`pad-${i}`} />;
                const count = (scansByDay[day] ?? []).length;
                return (
                  <button
                    key={day}
                    className={`month-cell ${day === dayStr ? "selected" : ""} ${
                      count > 0 ? "has-activity" : ""
                    }`}
                    onClick={() => {
                      setDayStr(day);
                      setGranularity("day");
                    }}
                    title={`${day} — ${count} scans`}
                  >
                    <span>{Number(day.split("-")[2])}</span>
                    {count > 0 && <span className="month-dot">{count}</span>}
                  </button>
                );
              })}
            </div>
          </>
        )}
      </div>
    </div>
  );
}

export default TimelinePage;
