import { STAGE_LABELS, STAGE_ORDER, type Annotation, type Stage } from "@4ge/contracts";
import { fitStageView, zoomStageView, type StageView as View } from "@4ge/kit";
import { Maximize, Minus, Plus } from "lucide-react";
import {
  useCallback,
  useEffect,
  useImperativeHandle,
  useRef,
  useState,
  type PointerEvent,
  type Ref,
  type WheelEvent,
} from "react";
import { StatusLed, agentName } from "../../shell/ui.js";
import { FramePreview } from "./FramePreview.js";
import {
  BOARD,
  effectiveTone,
  filterCounts,
  frameIdFromSelector,
  matchesFilter,
  stageStatus,
  type Board,
  type Frame,
  type FrameFilter,
  type Verdict,
} from "./frames.js";
import type { BuildView } from "./state.js";
import { StageView } from "./StageViews.js";

/**
 * The build's StageCanvas, run as an audit board: a pannable, zoomable
 * dotted ground (drag to pan, wheel or pinch to zoom, Fit all, 1:1) with a
 * titled row per stage and every frame at 1:1 with a status ring.
 *
 * Reuses @4ge/kit's StageCanvas math (fitStageView, zoomStageView) and its
 * rp-stage ground, marks and readout styles. The kit component keeps its
 * view private, and the audit board needs jump-to-stage and 1:1 from a
 * toolbar, so the view is held here instead.
 */
export interface CanvasControls {
  fit: () => void;
  focusFrame: (frameId: string) => void;
  focusStage: (stage: Stage) => void;
}

export interface BuildCanvasProps {
  board: Board;
  view: BuildView;
  verdicts: Record<string, Verdict>;
  filter: FrameFilter;
  onFilter: (filter: FrameFilter) => void;
  selectedId: string | undefined;
  onSelect: (frameId: string | undefined) => void;
  annotations: Annotation[];
  pinMode: boolean;
  onPin: (frameId: string, x: number, y: number) => void;
  onPinOpen: (annotationId: string) => void;
  controlRef?: Ref<CanvasControls>;
}

const MIN_ZOOM = 0.05;
const MAX_ZOOM = 3;
const clamp = (value: number) => Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, value));

const FILTERS: Array<{ id: FrameFilter; label: string }> = [
  { id: "all", label: "All" },
  { id: "unreviewed", label: "Not reviewed" },
  { id: "work", label: "Needs work" },
  { id: "broken", label: "Broken" },
];

export function BuildCanvas(props: BuildCanvasProps) {
  const { board, view, verdicts, filter, selectedId, annotations, pinMode } = props;
  const ground = useRef<HTMLElement>(null);
  const [camera, setCamera] = useState<View>({ x: 0, y: 0, zoom: 0.2 });
  const drag = useRef<{ id: number; x: number; y: number; moved: number } | null>(null);
  const lastDragMoved = useRef(0);
  const fitted = useRef(false);

  const box = () => {
    const rect = ground.current?.getBoundingClientRect();
    return rect && rect.width > 0 && rect.height > 0 ? { width: rect.width, height: rect.height } : { width: 1280, height: 800 };
  };
  const focusRect = useCallback((rect: { x: number; y: number; width: number; height: number }, margin = 40) => {
    const fitted = fitStageView(box(), { width: rect.width, height: rect.height }, margin);
    setCamera({ zoom: fitted.zoom, x: fitted.x - rect.x * fitted.zoom, y: fitted.y - rect.y * fitted.zoom });
  }, []);
  const fit = useCallback(() => focusRect({ x: 0, y: 0, width: board.width, height: board.height }, 24), [board.width, board.height, focusRect]);

  useEffect(() => {
    if (!fitted.current) {
      fitted.current = true;
      fit();
    }
  }, [fit]);

  useImperativeHandle(
    props.controlRef,
    () => ({
      fit,
      focusFrame: (frameId: string) => {
        const frame = board.frames.find((item) => item.id === frameId);
        if (frame) focusRect({ x: frame.x, y: frame.y - BOARD.frameHead, width: frame.width, height: frame.height + BOARD.frameHead }, 56);
      },
      focusStage: (stage: Stage) => {
        const row = board.rows.find((item) => item.stage === stage);
        if (!row) return;
        const rowFrames = board.frames.filter((frame) => frame.stage === stage);
        const right = Math.max(...rowFrames.map((frame) => frame.x + frame.width));
        focusRect({ x: BOARD.pad - 24, y: row.y - 24, width: right - BOARD.pad + 48, height: row.height + 48 }, 32);
      },
    }),
    [board, fit, focusRect],
  );

  const center = () => {
    const size = box();
    return { x: size.width / 2, y: size.height / 2 };
  };
  const zoomBy = (factor: number) => setCamera((current) => zoomStageView(current, center(), clamp(current.zoom * factor)));
  const oneToOne = () => setCamera((current) => zoomStageView(current, center(), 1));

  const fromInteractive = (target: EventTarget | null) =>
    target instanceof Element && target.closest("[data-stage-interactive]") !== null;

  const onWheel = (event: WheelEvent<HTMLElement>) => {
    if (fromInteractive(event.target)) return;
    const rect = event.currentTarget.getBoundingClientRect();
    const point = { x: event.clientX - rect.left, y: event.clientY - rect.top };
    if (event.shiftKey) {
      setCamera((current) => ({ ...current, x: current.x - (event.deltaY || event.deltaX), y: current.y }));
      return;
    }
    // Wheel and trackpad pinch (ctrlKey wheel) both zoom about the cursor.
    const factor = Math.exp(-(event.deltaY || event.deltaX) * (event.ctrlKey ? 0.01 : 0.0015));
    setCamera((current) => zoomStageView(current, point, clamp(current.zoom * factor)));
  };
  const onPointerDown = (event: PointerEvent<HTMLElement>) => {
    if (fromInteractive(event.target)) return;
    if (event.button !== 0 && event.button !== 1 && event.button !== 2) return;
    drag.current = { id: event.pointerId, x: event.clientX, y: event.clientY, moved: 0 };
    lastDragMoved.current = 0;
  };
  const onPointerMove = (event: PointerEvent<HTMLElement>) => {
    const active = drag.current;
    if (!active || active.id !== event.pointerId) return;
    const dx = event.clientX - active.x;
    const dy = event.clientY - active.y;
    const moved = active.moved + Math.abs(dx) + Math.abs(dy);
    drag.current = { ...active, x: event.clientX, y: event.clientY, moved };
    // Capture only once it is a real drag, so a plain click still reaches the frame.
    if (moved > 4 && !event.currentTarget.hasPointerCapture?.(event.pointerId)) {
      event.currentTarget.setPointerCapture?.(event.pointerId);
    }
    setCamera((current) => ({ ...current, x: current.x + dx, y: current.y + dy }));
  };
  const endDrag = (event: PointerEvent<HTMLElement>) => {
    if (drag.current?.id === event.pointerId) {
      // Keep the moved distance until the click handler has read it.
      const moved = drag.current.moved;
      drag.current = null;
      lastDragMoved.current = moved;
    }
  };
  const wasDrag = () => lastDragMoved.current > 4;

  const counts = filterCounts(board.frames, verdicts);
  const pinsByFrame = new Map<string, Annotation[]>();
  for (const annotation of annotations) {
    const frameId = frameIdFromSelector(annotation.target.selector);
    if (!frameId || annotation.target.x === undefined || annotation.target.y === undefined) continue;
    pinsByFrame.set(frameId, [...(pinsByFrame.get(frameId) ?? []), annotation]);
  }

  const pitch = 22 * camera.zoom;
  return (
    <div className="board">
      <div className="board__toolbar" role="toolbar" aria-label="Canvas">
        <div className="board__group" aria-label="Jump to stage">
          {STAGE_ORDER.map((stage, index) => (
            <button key={stage} type="button" className="board__chip" onClick={() => props.controlRef && focusStage(props.controlRef, stage)}>
              {String(index + 1).padStart(2, "0")} {STAGE_LABELS[stage]}
            </button>
          ))}
        </div>
        <div className="board__group" aria-label="Filter frames">
          {FILTERS.map((item) => (
            <button
              key={item.id}
              type="button"
              className={`board__chip${filter === item.id ? " is-active" : ""}`}
              aria-pressed={filter === item.id}
              onClick={() => props.onFilter(item.id)}
              data-testid={`filter-${item.id}`}
            >
              {item.label} <b>{counts[item.id]}</b>
            </button>
          ))}
        </div>
        <div className="board__group" aria-label="Zoom">
          <button type="button" className="board__icon" aria-label="Zoom out" onClick={() => zoomBy(1 / 1.25)}>
            <Minus size={14} aria-hidden="true" />
          </button>
          <span className="board__zoom" aria-live="polite">
            {Math.round(camera.zoom * 100)}%
          </span>
          <button type="button" className="board__icon" aria-label="Zoom in" onClick={() => zoomBy(1.25)}>
            <Plus size={14} aria-hidden="true" />
          </button>
          <button type="button" className="board__chip" onClick={oneToOne}>
            1:1
          </button>
          <button type="button" className="board__chip" onClick={fit}>
            <Maximize size={12} aria-hidden="true" /> Fit all
          </button>
        </div>
      </div>
      <section
        ref={ground}
        className={`rp-stage board__ground${pinMode ? " is-pinning" : ""}`}
        aria-label="Build canvas"
        aria-roledescription="pannable canvas"
        style={{ backgroundSize: `${pitch}px ${pitch}px`, backgroundPosition: `${camera.x}px ${camera.y}px` }}
        onWheel={onWheel}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={endDrag}
        onPointerCancel={endDrag}
        onContextMenu={(event) => {
          if (!fromInteractive(event.target)) event.preventDefault();
        }}
        onClick={(event) => {
          if (wasDrag()) return;
          if (event.target === event.currentTarget) props.onSelect(undefined);
        }}
        onDoubleClick={(event) => {
          if (event.target === event.currentTarget) fit();
        }}
        data-testid="build-canvas"
      >
        <div
          className="board__plane"
          style={{ width: board.width, height: board.height, transform: `translate(${camera.x}px, ${camera.y}px) scale(${camera.zoom})` }}
        >
          {board.rows.map((row) => {
            const state = view.build.stages.find((item) => item.stage === row.stage);
            return (
              <header key={row.stage} className="board__row" style={{ left: BOARD.pad, top: row.y }}>
                <span className="board__row-index">{`// ${String(row.index + 1).padStart(2, "0")}`}</span>
                <span className="board__row-title">{STAGE_LABELS[row.stage]}</span>
                {state ? <StatusLed status={stageStatus(view.build, row.stage)} /> : null}
                {state ? <span className="board__row-owner">{agentName(state.owner)} owns this stage</span> : null}
              </header>
            );
          })}
          {board.frames.map((frame) => {
            const verdict = verdicts[frame.id];
            const visible = matchesFilter(frame, verdict, filter);
            const selected = frame.id === selectedId;
            return (
              <FrameBox
                key={frame.id}
                frame={frame}
                view={view}
                tone={effectiveTone(frame, verdict)}
                verdict={verdict}
                dimmed={!visible}
                selected={selected}
                pins={pinsByFrame.get(frame.id) ?? []}
                onPick={(x, y) => {
                  if (wasDrag()) return;
                  if (pinMode) props.onPin(frame.id, x, y);
                  else props.onSelect(frame.id);
                }}
                onPinOpen={props.onPinOpen}
              />
            );
          })}
        </div>
        <div className="rp-stage__marks" aria-hidden="true">
          <i className="rp-stage__mark rp-stage__mark--tl" />
          <i className="rp-stage__mark rp-stage__mark--tr" />
          <i className="rp-stage__mark rp-stage__mark--bl" />
          <i className="rp-stage__mark rp-stage__mark--br" />
        </div>
        <span className="rp-stage__label">{view.build.title}</span>
        <span className="rp-stage__readout">
          {pinMode ? "Click a frame to pin a thread" : `${board.frames.length} frames`}
          <b>{Math.round(camera.zoom * 100)}%</b>
        </span>
      </section>
    </div>
  );
}

function focusStage(ref: Ref<CanvasControls>, stage: Stage) {
  if (ref && typeof ref === "object" && ref.current) ref.current.focusStage(stage);
}

const VERDICT_WORDS: Record<Verdict, string> = { right: "Looks right", work: "Needs work", broken: "Broken" };

function FrameBox({
  frame,
  view,
  tone,
  verdict,
  dimmed,
  selected,
  pins,
  onPick,
  onPinOpen,
}: {
  frame: Frame;
  view: BuildView;
  tone: string;
  verdict: Verdict | undefined;
  dimmed: boolean;
  selected: boolean;
  pins: Annotation[];
  onPick: (x: number, y: number) => void;
  onPinOpen: (annotationId: string) => void;
}) {
  return (
    <div
      className={`frame is-${tone}${selected ? " is-selected" : ""}${dimmed ? " is-dimmed" : ""}`}
      style={{ left: frame.x, top: frame.y - BOARD.frameHead, width: frame.width, height: frame.height + BOARD.frameHead }}
      data-4ge-frame={frame.id}
      data-testid={`frame-${frame.id}`}
    >
      <div className="frame__label">
        <i className="frame__ring" aria-hidden="true" />
        <span className="frame__title">{frame.title}</span>
        {frame.agent ? <span className="frame__meta">{agentName(frame.agent)}</span> : null}
        {verdict ? <span className="frame__verdict">{VERDICT_WORDS[verdict]}</span> : null}
      </div>
      {/* biome-ignore lint/a11y/useKeyWithClickEvents: frames are also reachable with arrow keys and the layer strip. */}
      <div
        className="frame__body"
        style={{ height: frame.height }}
        onClick={(event) => {
          const rect = event.currentTarget.getBoundingClientRect();
          const x = rect.width > 0 ? (event.clientX - rect.left) / rect.width : 0.5;
          const y = rect.height > 0 ? (event.clientY - rect.top) / rect.height : 0.5;
          onPick(Math.min(1, Math.max(0, x)), Math.min(1, Math.max(0, y)));
        }}
      >
        {frame.kind === "stage" ? (
          <StageView stage={frame.stage} view={view} interactive={selected} />
        ) : frame.preview ? (
          <FramePreview buildId={view.build.id} preview={frame.preview} interactive={selected} />
        ) : null}
        {pins.map((pin, index) => (
          <button
            key={pin.id}
            type="button"
            className={`frame__pin${pin.status === "resolved" ? " is-resolved" : ""}`}
            style={{ left: `${(pin.target.x ?? 0) * 100}%`, top: `${(pin.target.y ?? 0) * 100}%` }}
            aria-label={`Thread ${index + 1}: ${pin.body}`}
            data-stage-interactive=""
            onClick={(event) => {
              event.stopPropagation();
              onPinOpen(pin.id);
            }}
          >
            {index + 1}
          </button>
        ))}
      </div>
    </div>
  );
}
