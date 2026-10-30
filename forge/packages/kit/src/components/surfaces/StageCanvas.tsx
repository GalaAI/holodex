// Copied from GalaAI/ark packages/ui/src/surfaces/StageCanvas.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
// 4GE patch: the key lookup is read once so it compiles under noUncheckedIndexedAccess; behavior unchanged.
"use client";

import {
  type KeyboardEvent,
  type PointerEvent,
  type ReactNode,
  useCallback,
  useEffect,
  useRef,
  useState,
  type WheelEvent,
} from "react";

/**
 * StageCanvas (CP-24): the infinite preview board. Pan, zoom and inspect a live
 * preview the way an artboard does. It hosts previews only, never a page layout.
 *
 * Wheel pans; Shift+wheel zooms at the cursor; right or middle drag, or a left
 * drag on the ground, pans; double-click on the ground fits. An element inside
 * the board marked `data-stage-interactive` keeps its own pointer input, which
 * is how a live desktop takes clicks while the board stays pannable.
 */

export interface StageView {
  x: number;
  y: number;
  zoom: number;
}

export interface StageCanvasProps {
  /** Artboard size in content pixels; fit and the frame use it. */
  contentWidth: number;
  contentHeight: number;
  /** Frame-edge label, for example the session or preview name. */
  label?: string;
  /** Short status read at the frame edge, for example "Live" or "Recorded". */
  readout?: ReactNode;
  children: ReactNode;
  className?: string;
  minZoom?: number;
  maxZoom?: number;
  onViewChange?: (view: StageView) => void;
}

const clamp = (value: number, min: number, max: number) =>
  Math.min(max, Math.max(min, value));

export function fitStageView(
  viewport: { width: number; height: number },
  content: { width: number; height: number },
  margin = 48,
): StageView {
  const zoom = Math.max(
    0.05,
    Math.min(
      (viewport.width - margin * 2) / content.width,
      (viewport.height - margin * 2) / content.height,
      1,
    ),
  );
  return {
    zoom,
    x: (viewport.width - content.width * zoom) / 2,
    y: (viewport.height - content.height * zoom) / 2,
  };
}

/** Zoom about a point so the content under the cursor stays put. */
export function zoomStageView(
  view: StageView,
  point: { x: number; y: number },
  nextZoom: number,
): StageView {
  const ratio = nextZoom / view.zoom;
  return {
    zoom: nextZoom,
    x: point.x - (point.x - view.x) * ratio,
    y: point.y - (point.y - view.y) * ratio,
  };
}

export function StageCanvas({
  contentWidth,
  contentHeight,
  label,
  readout,
  children,
  className,
  minZoom = 0.1,
  maxZoom = 4,
  onViewChange,
}: StageCanvasProps) {
  const ref = useRef<HTMLElement>(null);
  const [view, setView] = useState<StageView>({ x: 0, y: 0, zoom: 1 });
  const drag = useRef<{ id: number; x: number; y: number } | null>(null);
  const update = useCallback(
    (next: StageView) => {
      setView(next);
      onViewChange?.(next);
    },
    [onViewChange],
  );
  const fit = useCallback(() => {
    const box = ref.current?.getBoundingClientRect();
    if (box)
      update(
        fitStageView(
          { width: box.width, height: box.height },
          { width: contentWidth, height: contentHeight },
        ),
      );
  }, [contentWidth, contentHeight, update]);
  useEffect(() => {
    fit();
  }, [fit]);

  const fromInteractive = (target: EventTarget | null) =>
    target instanceof Element &&
    target.closest("[data-stage-interactive]") !== null;

  const onWheel = (event: WheelEvent<HTMLElement>) => {
    if (fromInteractive(event.target) && !event.shiftKey) return;
    const box = event.currentTarget.getBoundingClientRect();
    if (event.shiftKey || event.ctrlKey) {
      const factor = Math.exp(-(event.deltaY || event.deltaX) * 0.0015);
      update(
        zoomStageView(
          view,
          { x: event.clientX - box.left, y: event.clientY - box.top },
          clamp(view.zoom * factor, minZoom, maxZoom),
        ),
      );
    } else
      update({ ...view, x: view.x - event.deltaX, y: view.y - event.deltaY });
  };
  const onPointerDown = (event: PointerEvent<HTMLElement>) => {
    const ground = !fromInteractive(event.target);
    if (
      event.button === 1 ||
      event.button === 2 ||
      (event.button === 0 && ground)
    ) {
      drag.current = {
        id: event.pointerId,
        x: event.clientX,
        y: event.clientY,
      };
      event.currentTarget.setPointerCapture(event.pointerId);
      event.preventDefault();
    }
  };
  const onPointerMove = (event: PointerEvent<HTMLElement>) => {
    const active = drag.current;
    if (!active || active.id !== event.pointerId) return;
    drag.current = { ...active, x: event.clientX, y: event.clientY };
    update({
      ...view,
      x: view.x + event.clientX - active.x,
      y: view.y + event.clientY - active.y,
    });
  };
  const endDrag = (event: PointerEvent<HTMLElement>) => {
    if (drag.current?.id === event.pointerId) drag.current = null;
  };
  const onKeyDown = (event: KeyboardEvent<HTMLElement>) => {
    if (event.target !== event.currentTarget) return;
    const step = 48;
    const box = event.currentTarget.getBoundingClientRect();
    const center = { x: box.width / 2, y: box.height / 2 };
    const moves: Record<string, () => StageView> = {
      ArrowLeft: () => ({ ...view, x: view.x + step }),
      ArrowRight: () => ({ ...view, x: view.x - step }),
      ArrowUp: () => ({ ...view, y: view.y + step }),
      ArrowDown: () => ({ ...view, y: view.y - step }),
      "+": () =>
        zoomStageView(view, center, clamp(view.zoom * 1.25, minZoom, maxZoom)),
      "=": () =>
        zoomStageView(view, center, clamp(view.zoom * 1.25, minZoom, maxZoom)),
      "-": () =>
        zoomStageView(view, center, clamp(view.zoom / 1.25, minZoom, maxZoom)),
    };
    if (event.key === "0") {
      event.preventDefault();
      fit();
    } else {
      const move = moves[event.key];
      if (move) {
        event.preventDefault();
        update(move());
      }
    }
  };
  const pitch = 22 * view.zoom;
  return (
    <section
      ref={ref}
      className={`rp-stage${className ? ` ${className}` : ""}`}
      aria-label={label ? `${label} stage` : "Preview stage"}
      aria-roledescription="pannable canvas"
      // biome-ignore lint/a11y/noNoninteractiveTabindex: the board is a pannable region driven by arrow, zoom and fit keys.
      tabIndex={0}
      style={{
        backgroundSize: `${pitch}px ${pitch}px`,
        backgroundPosition: `${view.x}px ${view.y}px`,
      }}
      onWheel={onWheel}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={endDrag}
      onPointerCancel={endDrag}
      onContextMenu={(event) => {
        if (!fromInteractive(event.target)) event.preventDefault();
      }}
      onDoubleClick={(event) => {
        if (!fromInteractive(event.target)) fit();
      }}
      onKeyDown={onKeyDown}
    >
      <div
        className="rp-stage__board"
        style={{
          width: contentWidth,
          height: contentHeight,
          transform: `translate(${view.x}px, ${view.y}px) scale(${view.zoom})`,
        }}
      >
        {children}
      </div>
      <div className="rp-stage__marks" aria-hidden="true">
        <i className="rp-stage__mark rp-stage__mark--tl" />
        <i className="rp-stage__mark rp-stage__mark--tr" />
        <i className="rp-stage__mark rp-stage__mark--bl" />
        <i className="rp-stage__mark rp-stage__mark--br" />
      </div>
      {label ? <span className="rp-stage__label">{label}</span> : null}
      <span className="rp-stage__readout" aria-live="polite">
        {readout}
        <b>{Math.round(view.zoom * 100)}%</b>
      </span>
    </section>
  );
}
