import { activityView, durationText, shortClock } from "../glint";

/**
 * A day at a glance: one block per activity, placed at its real times and
 * coloured by how Glint watched it. Gaps are time nothing was recorded.
 */
function ActivityStrip({ activities, caption }) {
  const items = (activities ?? []).filter(
    (activity) => activity.endedAtMilliseconds > activity.startedAtMilliseconds,
  );
  if (items.length === 0) return null;

  const start = Math.min(...items.map((a) => a.startedAtMilliseconds));
  const end = Math.max(...items.map((a) => a.endedAtMilliseconds));
  const span = Math.max(end - start, 60_000);
  const left = (ms) => `${((ms - start) / span) * 100}%`;
  const width = (from, to) => `${Math.max(((to - from) / span) * 100, 0.6)}%`;

  return (
    <div className="activity-strip" aria-label={caption ?? "Activities over time"}>
      <div className="activity-strip-track">
        {items.flatMap((activity) => {
          const view = activityView(activity);
          return (activity.segments?.length
            ? activity.segments
            : [
                {
                  startMilliseconds: activity.startedAtMilliseconds,
                  endMilliseconds: activity.endedAtMilliseconds,
                },
              ]
          ).map((segment, index) => (
            <span
              key={`${activity.id}-${index}`}
              className={`activity-block mode-${activity.mode}`}
              style={{
                left: left(segment.startMilliseconds),
                width: width(segment.startMilliseconds, segment.endMilliseconds),
              }}
              title={`${view.label} · ${view.modeLabel} · ${shortClock(segment.startMilliseconds)}–${shortClock(segment.endMilliseconds)} (${durationText(segment.endMilliseconds - segment.startMilliseconds)})`}
            />
          ));
        })}
      </div>
      <div className="activity-strip-axis">
        <span>{shortClock(start)}</span>
        <span>{shortClock(end)}</span>
      </div>
      <div className="activity-legend">
        {["Read", "Make", "Play", "Watch", "Private"]
          .filter((mode) => items.some((a) => a.mode === mode))
          .map((mode) => (
            <span key={mode} className={`activity-legend-item mode-${mode}`}>
              {mode}
            </span>
          ))}
      </div>
    </div>
  );
}

export default ActivityStrip;
