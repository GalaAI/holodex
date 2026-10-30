import type {
  Build,
  Checkpoint,
  Layer,
  Receipt,
  SandboxRun,
  Session,
  SessionEvent,
  Sprint,
  TestFlight,
} from "@4ge/contracts";

/**
 * The IDE's live picture of one build, folded from the initial GETs and
 * every event on /builds/:id/stream. Pure, so it is tested on its own.
 */
export interface BuildView {
  build: Build;
  sessions: Record<string, Session>;
  checkpoints: Record<string, Checkpoint>;
  receipts: Record<string, Receipt>;
  sandbox: SandboxRun | undefined;
  testFlight: TestFlight | undefined;
  /** The last error event, in plain words. */
  lastError: { code: string; message: string; at: string } | undefined;
  /** Recent log lines, newest last, capped. */
  logs: Array<{ level: string; message: string; at: string }>;
}

export function initialView(build: Build): BuildView {
  return {
    build,
    sessions: {},
    checkpoints: {},
    receipts: {},
    sandbox: undefined,
    testFlight: undefined,
    lastError: undefined,
    logs: [],
  };
}

function upsertLayer(build: Build, layer: Layer): Build {
  const exists = build.layers.some((item) => item.id === layer.id);
  return {
    ...build,
    layers: exists ? build.layers.map((item) => (item.id === layer.id ? layer : item)) : [...build.layers, layer],
  };
}

function upsertSprint(build: Build, sprint: Sprint): Build {
  const exists = build.sprints.some((item) => item.id === sprint.id);
  const sprints = exists ? build.sprints.map((item) => (item.id === sprint.id ? sprint : item)) : [...build.sprints, sprint];
  // The stage row follows its sprint, so the strip shows the live state.
  const stages = build.stages.map((stage) =>
    stage.stage === sprint.stage ? { ...stage, status: sprint.status, sprintId: sprint.id } : stage,
  );
  return { ...build, sprints, stages };
}

export type ViewAction =
  | { type: "reset"; build: Build }
  | { type: "build"; build: Build }
  | { type: "sessions"; sessions: Session[] }
  | { type: "checkpoints"; checkpoints: Checkpoint[] }
  | { type: "receipts"; receipts: Receipt[] }
  | { type: "event"; event: SessionEvent };

export function reduceView(view: BuildView, action: ViewAction): BuildView {
  switch (action.type) {
    case "reset":
      return initialView(action.build);
    case "build":
      return { ...view, build: action.build };
    case "sessions": {
      const sessions = { ...view.sessions };
      for (const session of action.sessions) sessions[session.id] = session;
      return { ...view, sessions };
    }
    case "checkpoints": {
      const checkpoints = { ...view.checkpoints };
      for (const checkpoint of action.checkpoints) checkpoints[checkpoint.id] = checkpoint;
      return { ...view, checkpoints };
    }
    case "receipts": {
      const receipts = { ...view.receipts };
      for (const receipt of action.receipts) receipts[receipt.id] = receipt;
      return { ...view, receipts };
    }
    case "event":
      return applyEvent(view, action.event);
  }
}

export function applyEvent(view: BuildView, event: SessionEvent): BuildView {
  const { build } = view;
  switch (event.type) {
    case "build.status":
      if (event.data.buildId !== build.id) return view;
      return {
        ...view,
        build: {
          ...build,
          status: event.data.status,
          updatedAt: event.at,
          ...(event.data.error !== undefined ? { error: event.data.error } : {}),
          ...(["passed", "failed", "cancelled"].includes(event.data.status) ? { finishedAt: event.at } : {}),
        },
      };
    case "layer.updated":
      if (event.data.layer.buildId !== build.id) return view;
      return { ...view, build: upsertLayer(build, event.data.layer) };
    case "sprint.updated":
      if (event.data.sprint.buildId !== build.id) return view;
      return {
        ...view,
        build: {
          ...upsertSprint(build, event.data.sprint),
          ...(event.data.sprint.status === "running" ? { currentSprintId: event.data.sprint.id } : {}),
        },
      };
    case "checkpoint.opened":
    case "checkpoint.resolved": {
      const checkpoint = event.data.checkpoint;
      if (checkpoint.buildId !== build.id) return view;
      const open = event.type === "checkpoint.opened" && checkpoint.status === "open";
      const { openCheckpointId: _previous, ...rest } = build;
      return {
        ...view,
        checkpoints: { ...view.checkpoints, [checkpoint.id]: checkpoint },
        build: open
          ? { ...build, openCheckpointId: checkpoint.id }
          : build.openCheckpointId === checkpoint.id
            ? rest
            : build,
      };
    }
    case "preview.updated": {
      if (event.data.buildId !== build.id) return view;
      const preview = event.data.preview;
      if (!preview.layerId) return view;
      const layer = build.layers.find((item) => item.id === preview.layerId);
      if (!layer) return view;
      const previews = [...layer.previews.filter((item) => item.ref !== preview.ref), preview];
      return { ...view, build: upsertLayer(build, { ...layer, previews }) };
    }
    case "receipt.recorded":
      if (event.data.receipt.buildId !== build.id) return view;
      return { ...view, receipts: { ...view.receipts, [event.data.receipt.id]: event.data.receipt } };
    case "session.created":
      return { ...view, sessions: { ...view.sessions, [event.data.session.id]: event.data.session } };
    case "session.status": {
      const session = view.sessions[event.data.sessionId];
      if (!session) return view;
      return {
        ...view,
        sessions: {
          ...view.sessions,
          [session.id]: {
            ...session,
            status: event.data.status,
            updatedAt: event.at,
            ...(event.data.summary !== undefined ? { summary: event.data.summary } : {}),
            ...(event.data.error !== undefined ? { error: event.data.error } : {}),
            ...(["done", "failed", "cancelled"].includes(event.data.status) ? { finishedAt: event.at } : {}),
          },
        },
      };
    }
    case "sandbox.updated":
      if (event.data.run.buildId !== build.id) return view;
      return { ...view, sandbox: event.data.run };
    case "testflight.updated":
      if (event.data.testFlight.buildId !== build.id) return view;
      return { ...view, testFlight: event.data.testFlight };
    case "error":
      return { ...view, lastError: { code: event.data.code, message: event.data.message, at: event.at } };
    case "log":
      return { ...view, logs: [...view.logs, { level: event.data.level, message: event.data.message, at: event.at }].slice(-200) };
    default:
      return view;
  }
}

/** The layer a session works on, when it is a layer session. */
export function layerForSession(build: Build, sessionId: string): Layer | undefined {
  return build.layers.find((layer) => layer.sessionId === sessionId);
}

export const FINISHED_BUILD = new Set(["passed", "failed", "cancelled"]);
