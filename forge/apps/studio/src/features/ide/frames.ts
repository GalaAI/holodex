import { STAGE_ORDER, type AgentId, type Build, type Layer, type Preview, type Stage, type WorkStatus } from "@4ge/contracts";

/**
 * The StageCanvas board as data (SPEC "StageCanvas = audit board"): one
 * titled row per stage, one frame per stage view and per layer preview, at
 * 1:1, each with a status ring. Verdicts are the user's review of a frame.
 */
export type FrameTone = "ok" | "warn" | "err" | "rest";
export type Verdict = "right" | "work" | "broken";
export type FrameFilter = "all" | "unreviewed" | "work" | "broken";

export interface Frame {
  id: string;
  stage: Stage;
  kind: "stage" | "preview";
  title: string;
  agent: AgentId | undefined;
  layerId: string | undefined;
  preview: Preview | undefined;
  /** What the build says about this frame. */
  tone: FrameTone;
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface BoardRow {
  stage: Stage;
  index: number;
  y: number;
  height: number;
}

export interface Board {
  frames: Frame[];
  rows: BoardRow[];
  width: number;
  height: number;
}

export const BOARD = {
  pad: 96,
  rowHead: 72,
  frameHead: 36,
  gapX: 72,
  gapY: 112,
  stage: { width: 960, height: 600 },
} as const;

export function previewSize(preview: Preview): { width: number; height: number } {
  switch (preview.kind) {
    case "url":
    case "html":
      return { width: 1280, height: 800 };
    case "image":
      return { width: 960, height: 600 };
    default:
      return { width: 720, height: 600 };
  }
}

export function workTone(status: WorkStatus): FrameTone {
  if (status === "passed") return "ok";
  if (status === "failed") return "err";
  if (status === "cancelled") return "rest";
  return "warn";
}

/**
 * A stage's live status. The stage row is authoritative, but a server may
 * move layers before it moves the stage row, so a pending stage whose
 * layers are already working reads as running.
 */
export function stageStatus(build: Build, stage: Stage): WorkStatus {
  const state = build.stages.find((item) => item.stage === stage);
  const status = state?.status ?? "pending";
  if (status !== "pending") return status;
  const layers = build.layers.filter((layer) => layer.stage === stage);
  if (layers.some((layer) => layer.status === "running" || layer.status === "passed" || layer.status === "failed")) return "running";
  return status;
}

export function layerTone(layer: Layer): FrameTone {
  if (layer.testFlight === "failed" || layer.status === "failed") return "err";
  if (layer.status === "cancelled") return "rest";
  if (layer.testFlight === "passed" || layer.status === "passed") return "ok";
  return "warn";
}

export function frameIdFor(layerId: string, ref: string): string {
  return `preview:${layerId}:${ref}`;
}

export function buildBoard(build: Build): Board {
  const frames: Frame[] = [];
  const rows: BoardRow[] = [];
  let y = BOARD.pad;
  let width = 0;
  STAGE_ORDER.forEach((stage, index) => {
    const state = build.stages.find((item) => item.stage === stage);
    const rowFrames: Frame[] = [];
    let x = BOARD.pad;
    const frameTop = y + BOARD.rowHead + BOARD.frameHead;
    rowFrames.push({
      id: `stage:${stage}`,
      stage,
      kind: "stage",
      title: STAGE_TITLES[stage],
      agent: state?.owner,
      layerId: undefined,
      preview: undefined,
      tone: state ? workTone(stageStatus(build, stage)) : "rest",
      x,
      y: frameTop,
      ...BOARD.stage,
    });
    x += BOARD.stage.width + BOARD.gapX;
    for (const layer of build.layers.filter((item) => item.stage === stage)) {
      for (const preview of layer.previews) {
        const size = previewSize(preview);
        rowFrames.push({
          id: frameIdFor(layer.id, preview.ref),
          stage,
          kind: "preview",
          title: preview.title,
          agent: layer.agent,
          layerId: layer.id,
          preview,
          tone: layerTone(layer),
          x,
          y: frameTop,
          ...size,
        });
        x += size.width + BOARD.gapX;
      }
    }
    const height = BOARD.rowHead + BOARD.frameHead + Math.max(...rowFrames.map((frame) => frame.height));
    rows.push({ stage, index, y, height });
    frames.push(...rowFrames);
    width = Math.max(width, x - BOARD.gapX + BOARD.pad);
    y += height + BOARD.gapY;
  });
  return { frames, rows, width, height: y - BOARD.gapY + BOARD.pad };
}

export const STAGE_TITLES: Record<Stage, string> = {
  architecture: "Architecture map",
  orchestration: "Orchestration graph",
  interface: "Interface artboards",
  deploy: "Deploy and test flight",
};

export function effectiveTone(frame: Frame, verdict: Verdict | undefined): FrameTone {
  if (verdict === "right") return "ok";
  if (verdict === "work") return "warn";
  if (verdict === "broken") return "err";
  return frame.tone;
}

export function matchesFilter(frame: Frame, verdict: Verdict | undefined, filter: FrameFilter): boolean {
  switch (filter) {
    case "unreviewed":
      return verdict === undefined;
    case "work":
      return effectiveTone(frame, verdict) === "warn";
    case "broken":
      return effectiveTone(frame, verdict) === "err";
    default:
      return true;
  }
}

export function filterCounts(frames: Frame[], verdicts: Record<string, Verdict>): Record<FrameFilter, number> {
  const counts: Record<FrameFilter, number> = { all: frames.length, unreviewed: 0, work: 0, broken: 0 };
  for (const frame of frames) {
    const verdict = verdicts[frame.id];
    if (matchesFilter(frame, verdict, "unreviewed")) counts.unreviewed += 1;
    if (matchesFilter(frame, verdict, "work")) counts.work += 1;
    if (matchesFilter(frame, verdict, "broken")) counts.broken += 1;
  }
  return counts;
}

/** The frame selector a thread anchors to (DesignTarget.selector). */
export function frameSelector(frameId: string): string {
  return `[data-4ge-frame="${frameId}"]`;
}

export function frameIdFromSelector(selector: string): string | undefined {
  const match = /^\[data-4ge-frame="(.+)"\]$/.exec(selector);
  return match?.[1];
}
