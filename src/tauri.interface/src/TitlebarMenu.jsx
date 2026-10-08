import { useEffect } from "react";
import "./TitlebarMenu.css";

const MENU_WIDTH = 250;
const MENU_MAX_HEIGHT = 420;

function TitlebarMenu({
  position,
  onAppearance,
  onMinimize,
  onToggleMaximize,
  onCloseWindow,
  onDismiss,
}) {
  useEffect(() => {
    function onKeyDown(e) {
      if (e.key === "Escape") onDismiss();
    }
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [onDismiss]);

  const x = Math.min(position.x, window.innerWidth - MENU_WIDTH - 8);
  const y = Math.min(position.y, window.innerHeight - MENU_MAX_HEIGHT - 8);

  function wrap(fn) {
    return () => {
      onDismiss();
      fn();
    };
  }

  return (
    <div
      className="tbmenu-overlay"
      onClick={onDismiss}
      onContextMenu={(e) => {
        e.preventDefault();
        onDismiss();
      }}
    >
      <div
        className="tbmenu"
        style={{ left: Math.max(x, 8), top: Math.max(y, 8) }}
        onClick={(e) => e.stopPropagation()}
        onContextMenu={(e) => e.preventDefault()}
      >
        <button className="tbmenu-item" onClick={wrap(onMinimize)}>
          Minimize
        </button>
        <button className="tbmenu-item" onClick={wrap(onToggleMaximize)}>
          Maximize / Restore
        </button>
        <button className="tbmenu-item" onClick={wrap(onCloseWindow)}>
          Close
        </button>

        <div className="tbmenu-separator" />
        <button
          className="tbmenu-item tbmenu-customize"
          onClick={wrap(onAppearance)}
        >
          Theme &amp; transparency&hellip;
        </button>
      </div>
    </div>
  );
}

export default TitlebarMenu;
