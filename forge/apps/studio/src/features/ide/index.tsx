import type { Annotation, Build, Checkpoint, Receipt, SessionEvent } from "@4ge/contracts";
import { Button, CadenceDots, Eyebrow, Panel } from "@4ge/kit";
import { FlaskConical, Hammer, SlidersHorizontal } from "lucide-react";
import { useCallback, useEffect, useMemo, useReducer, useRef, useState } from "react";
import { Link, useParams } from "react-router";
import { ForgeApiError, api, useRoute } from "../../lib/api.js";
import { useRouteStream } from "../../lib/sse.js";
import { useThread } from "../../shell/thread/useThread.js";
import { ErrorNotice, Notice, StatusLed, elapsed, shortTime, statusWord } from "../../shell/ui.js";
import { AgentTree } from "./AgentTree.js";
import { BuildCanvas, type CanvasControls } from "./BuildCanvas.js";
import { buildBoard, matchesFilter, type FrameFilter, type Verdict } from "./frames.js";
import { Inspector, type InspectorControls } from "./Inspector.js";
import { SessionThread, SmithPanel } from "./SmithPanel.js";
import { StageStrip } from "./StageStrip.js";
import { FINISHED_BUILD, initialView, reduceView, type BuildView, type ViewAction } from "./state.js";
import { Findings, threadMarker } from "./threads.js";
import "./ide.css";

/**
 * The 4GE IDE (SPEC flow 5 to 7): Smith on the left (or folded into a HUD),
 * the StageCanvas audit board in the middle, the agent tree, findings and
 * frame inspector on the right, and the stage and layer strip with sprint
 * checkpoints along the bottom. Everything is the live build from the
 * server: GET once, then /builds/:id/stream.
 */
export default function Ide() {
  const { buildId } = useParams();
  return buildId ? <Workspace key={buildId} buildId={buildId} /> : <BuildPicker />;
}

function BuildPicker() {
  const builds = useRoute("builds.list", "builds");
  return (
    <section className="ide-pick" data-testid="feature-ide">
      <Panel className="ide-pick__panel">
        <Eyebrow>4GE // Build</Eyebrow>
        <h1 className="ide-pick__title">Builds</h1>
        <p className="ide-pick__lede">Open a build to work on it with Smith. New builds start from a Seed in Imagistation.</p>
        {builds.loading ? <CadenceDots tone="cream" label="Loading builds" /> : null}
        {builds.error ? <ErrorNotice error={builds.error} onRetry={builds.reload} /> : null}
        {builds.data && builds.data.builds.length === 0 ? (
          <p className="ide-pick__lede">
            No builds yet. <Link to="/imagistation">Start an idea in Imagistation</Link>, and choose Build now on its Seed.
          </p>
        ) : null}
        <ul className="ide-pick__list">
          {builds.data?.builds.map((build) => (
            <li key={build.id}>
              <Link className="ide-pick__item" to={`/ide/${build.id}`}>
                <Hammer size={16} aria-hidden="true" />
                <span className="ide-pick__name">{build.title}</span>
                <StatusLed status={build.status} />
                <span className="ide-pick__meta">
                  {build.layers.filter((layer) => layer.status === "passed").length} of {build.layers.length} layers // {shortTime(build.updatedAt)}
                </span>
              </Link>
            </li>
          ))}
        </ul>
      </Panel>
    </section>
  );
}

type Load = { state: "loading" } | { state: "error"; error: ForgeApiError } | { state: "ready" };

function viewReducer(view: BuildView | undefined, action: ViewAction): BuildView | undefined {
  if (action.type === "reset") return initialView(action.build);
  return view ? reduceView(view, action) : view;
}

function readVerdicts(buildId: string): Record<string, Verdict> {
  try {
    const raw = window.localStorage.getItem(`4ge.verdicts.${buildId}`);
    return raw ? (JSON.parse(raw) as Record<string, Verdict>) : {};
  } catch {
    return {};
  }
}

function writeVerdicts(buildId: string, verdicts: Record<string, Verdict>) {
  try {
    window.localStorage.setItem(`4ge.verdicts.${buildId}`, JSON.stringify(verdicts));
  } catch {
    /* Verdicts are a per-viewer convenience; the board works without storage. */
  }
}

function readMode(): "panel" | "hud" {
  try {
    return window.localStorage.getItem("4ge.smith.mode") === "hud" ? "hud" : "panel";
  } catch {
    return "panel";
  }
}

const asError = (failure: unknown, route: string) =>
  failure instanceof ForgeApiError
    ? failure
    : new ForgeApiError({ status: 0, code: "network", message: String(failure), route });

function Workspace({ buildId }: { buildId: string }) {
  const [view, dispatch] = useReducer(viewReducer, undefined);
  const [load, setLoad] = useState<Load>({ state: "loading" });
  const [tick, setTick] = useState(0);
  const [annotations, setAnnotations] = useState<Annotation[]>([]);
  const [annotationsError, setAnnotationsError] = useState<ForgeApiError>();
  const [sideError, setSideError] = useState<ForgeApiError>();
  const [verdicts, setVerdicts] = useState<Record<string, Verdict>>(() => readVerdicts(buildId));
  const [selectedId, setSelectedId] = useState<string>();
  const [filter, setFilter] = useState<FrameFilter>("all");
  const [pinMode, setPinMode] = useState(false);
  const [mode, setMode] = useState<"panel" | "hud">(readMode);
  const [tab, setTab] = useState<"agents" | "findings">("agents");
  const [subSession, setSubSession] = useState<string>();
  const [confirmCancel, setConfirmCancel] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const [dismissedError, setDismissedError] = useState<string>();
  const canvas = useRef<CanvasControls>(null);
  const inspector = useRef<InspectorControls>(null);

  useEffect(() => {
    const controller = new AbortController();
    const signal = controller.signal;
    setLoad({ state: "loading" });
    api
      .call("builds.get", { params: { id: buildId }, signal })
      .then((result) => {
        dispatch({ type: "reset", build: result.build });
        setLoad({ state: "ready" });
        // The rest fills in around the build; each failure is shown, none blocks.
        api
          .call("sessions.list", { query: { buildId }, signal })
          .then((sessions) => dispatch({ type: "sessions", sessions: sessions.sessions }))
          .catch((failure: unknown) => !signal.aborted && setSideError(asError(failure, "sessions.list")));
        api
          .call("builds.checkpoints.list", { params: { id: buildId }, signal })
          .then((checkpoints) => dispatch({ type: "checkpoints", checkpoints: checkpoints.checkpoints }))
          .catch((failure: unknown) => !signal.aborted && setSideError(asError(failure, "builds.checkpoints.list")));
        api
          .call("design.annotations.list", { query: { buildId }, signal })
          .then((result) => {
            setAnnotations(result.annotations);
            setAnnotationsError(undefined);
          })
          .catch((failure: unknown) => !signal.aborted && setAnnotationsError(asError(failure, "design.annotations.list")));
      })
      .catch((failure: unknown) => {
        if (!signal.aborted) setLoad({ state: "error", error: asError(failure, "builds.get") });
      });
    return () => controller.abort();
  }, [buildId, tick]);

  const onEvent = useCallback((event: SessionEvent) => dispatch({ type: "event", event }), []);
  const stream = useRouteStream("builds.stream", load.state === "ready" ? { id: buildId } : null, onEvent);
  const smith = useThread(view?.build.rootSessionId ?? null, "session");
  const sub = useThread(subSession ?? null, "session");

  const board = useMemo(() => (view ? buildBoard(view.build) : undefined), [view]);
  const navigable = useMemo(
    () => board?.frames.filter((frame) => matchesFilter(frame, verdicts[frame.id], filter)) ?? [],
    [board, verdicts, filter],
  );
  const selected = board?.frames.find((frame) => frame.id === selectedId);

  const select = useCallback((frameId: string | undefined) => {
    setSelectedId(frameId);
    setPinMode(false);
    if (frameId) canvas.current?.focusFrame(frameId);
  }, []);
  const step = useCallback(
    (delta: number) => {
      if (navigable.length === 0) return;
      const index = navigable.findIndex((frame) => frame.id === selectedId);
      const next = navigable[(index + delta + navigable.length) % navigable.length] ?? navigable[0];
      if (next) select(next.id);
    },
    [navigable, selectedId, select],
  );
  const setVerdict = useCallback(
    (frameId: string, verdict: Verdict | undefined) => {
      setVerdicts((current) => {
        const next = { ...current };
        if (verdict) next[frameId] = verdict;
        else delete next[frameId];
        writeVerdicts(buildId, next);
        return next;
      });
    },
    [buildId],
  );

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const target = event.target;
      if (target instanceof Element && (target.closest("input, textarea, select, [contenteditable='true']") || target.tagName === "IFRAME")) return;
      if (event.metaKey || event.ctrlKey || event.altKey) return;
      if (event.key === "ArrowRight" || event.key === "ArrowDown") {
        event.preventDefault();
        step(1);
      } else if (event.key === "ArrowLeft" || event.key === "ArrowUp") {
        event.preventDefault();
        step(-1);
      } else if (event.key === "f" || event.key === "F") {
        canvas.current?.fit();
      } else if (event.key === "Escape") {
        if (pinMode) setPinMode(false);
        else setSelectedId(undefined);
      } else if (selectedId && ["1", "2", "3"].includes(event.key)) {
        const verdict = ({ "1": "right", "2": "work", "3": "broken" } as const)[event.key as "1" | "2" | "3"];
        setVerdict(selectedId, verdicts[selectedId] === verdict ? undefined : verdict);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [step, pinMode, selectedId, verdicts, setVerdict]);

  const upsertAnnotation = useCallback((annotation: Annotation) => {
    setAnnotations((current) => [...current.filter((item) => item.id !== annotation.id), annotation]);
  }, []);
  const sendToSmith = useCallback(
    (annotation: Annotation) =>
      smith.send(
        `${threadMarker(annotation.id)} On "${annotation.target.label ?? "a frame"}" (${annotation.target.page}): ${annotation.body}`,
      ),
    [smith],
  );
  const openThread = useCallback(
    (annotationId: string) => {
      const annotation = annotations.find((item) => item.id === annotationId);
      const frameId = annotation ? /^\[data-4ge-frame="(.+)"\]$/.exec(annotation.target.selector)?.[1] : undefined;
      if (frameId) select(frameId);
    },
    [annotations, select],
  );
  const selectLayer = useCallback(
    (layerId: string) => {
      const frame = board?.frames.find((item) => item.layerId === layerId);
      const layer = view?.build.layers.find((item) => item.id === layerId);
      select(frame?.id ?? (layer ? `stage:${layer.stage}` : undefined));
    },
    [board, view, select],
  );
  const onReceipts = useCallback((receipts: Receipt[]) => dispatch({ type: "receipts", receipts }), []);
  const onCheckpoint = useCallback((checkpoint: Checkpoint, build: Build) => {
    dispatch({ type: "checkpoints", checkpoints: [checkpoint] });
    dispatch({ type: "build", build });
  }, []);
  const changeMode = (next: "panel" | "hud") => {
    setMode(next);
    try {
      window.localStorage.setItem("4ge.smith.mode", next);
    } catch {
      /* per-viewer convenience only */
    }
  };
  const cancelBuild = async () => {
    setCancelling(true);
    try {
      const result = await api.call("builds.cancel", { params: { id: buildId } });
      dispatch({ type: "build", build: result.build });
      setConfirmCancel(false);
    } catch (failure) {
      setSideError(asError(failure, "builds.cancel"));
    } finally {
      setCancelling(false);
    }
  };

  if (load.state === "loading" || (load.state === "ready" && !view)) {
    return (
      <div className="studio-loading" role="status">
        <CadenceDots tone="cream" label="Opening the build" />
      </div>
    );
  }
  if (load.state === "error" || !view || !board) {
    return (
      <section className="ide-pick" data-testid="feature-ide">
        <Panel className="ide-pick__panel">
          <Eyebrow>4GE // Build</Eyebrow>
          <h1 className="ide-pick__title">This build could not open</h1>
          {load.state === "error" ? <ErrorNotice error={load.error} onRetry={() => setTick((value) => value + 1)} /> : null}
          <Link to="/ide">See every build</Link>
        </Panel>
      </section>
    );
  }

  const { build } = view;
  const finished = FINISHED_BUILD.has(build.status);
  const showError = view.lastError && view.lastError.at !== dismissedError ? view.lastError : undefined;

  return (
    <section className={`ide${mode === "hud" ? " ide--hud" : ""}`} data-testid="feature-ide" aria-label={`Build ${build.title}`}>
      <header className="ide__top">
        <Link to="/ide" className="ide__back">
          Builds
        </Link>
        <div className="ide__title">
          <Eyebrow>4GE // Build</Eyebrow>
          <h1>{build.title}</h1>
        </div>
        <StatusLed status={build.status} />
        <span className="ide__meta">
          {build.layers.filter((layer) => layer.status === "passed").length} of {build.layers.length} layers passed // started{" "}
          {shortTime(build.createdAt)}
          {build.finishedAt ? ` // took ${elapsed(build.createdAt, build.finishedAt)}` : ""}
        </span>
        <span className={`ide__stream is-${stream.status}`} title={stream.dropped ? `${stream.dropped} event(s) did not match the contract and were dropped` : undefined}>
          {stream.status === "open" ? "Live" : stream.status === "reconnecting" ? "Reconnecting" : stream.status === "closed" ? "Live updates stopped" : "Connecting"}
        </span>
        <nav className="ide__links" aria-label="Build tools">
          <Link to={`/tweak/${build.id}`}>
            <SlidersHorizontal size={14} aria-hidden="true" /> Tweak
          </Link>
          <Link to={`/sandbox/${build.id}`}>
            <FlaskConical size={14} aria-hidden="true" /> Sandbox
          </Link>
        </nav>
        {!finished ? (
          confirmCancel ? (
            <span className="ide__confirm">
              <span>Stop every layer?</span>
              <Button onClick={() => setConfirmCancel(false)} disabled={cancelling}>
                Keep building
              </Button>
              <Button variant="danger" onClick={() => void cancelBuild()} disabled={cancelling} data-testid="confirm-cancel">
                {cancelling ? "Cancelling" : "Cancel the build"}
              </Button>
            </span>
          ) : (
            <Button variant="danger" onClick={() => setConfirmCancel(true)} data-testid="cancel-build">
              Cancel build
            </Button>
          )
        ) : null}
      </header>

      <div className="ide__banners">
        {build.status === "cancelled" ? (
          <Notice tone="warn">
            <strong>This build was cancelled{build.finishedAt ? ` at ${shortTime(build.finishedAt)}` : ""}.</strong>
            <span>Nothing else runs. Its layers keep what they finished. To build again, choose Build now on its Seed in Imagistation.</span>
          </Notice>
        ) : null}
        {build.status === "failed" ? (
          <Notice tone="err">
            <strong>The build failed.</strong>
            <span>{build.error ?? "A layer failed and the stage gate stopped the build. Ask Smith what happened."}</span>
          </Notice>
        ) : null}
        {build.status === "passed" ? (
          <Notice tone="ok">
            <strong>Every layer passed.</strong>
            <span>The build joins your Apps in the Holodex Library. Tune it with Smith, or open the sandbox for another test flight.</span>
          </Notice>
        ) : null}
        {showError ? (
          <div className="studio-notice studio-notice--err" role="alert">
            <strong>{statusWord(showError.code)}</strong>
            <span>{showError.message}</span>
            <span>
              <Button onClick={() => setDismissedError(showError.at)}>Dismiss</Button>
            </span>
          </div>
        ) : null}
        {sideError ? <ErrorNotice error={sideError} compact /> : null}
      </div>

      <div className="ide__body">
        {mode === "panel" ? (
          <SmithPanel
            thread={smith}
            hasSession={Boolean(build.rootSessionId)}
            mode="panel"
            onMode={changeMode}
            finished={finished}
            onOpenThreadMarker={openThread}
          />
        ) : null}
        <div className="ide__canvas">
          <BuildCanvas
            board={board}
            view={view}
            verdicts={verdicts}
            filter={filter}
            onFilter={setFilter}
            selectedId={selectedId}
            onSelect={select}
            annotations={annotations}
            pinMode={pinMode}
            onPin={(frameId, x, y) => {
              if (frameId !== selectedId) {
                select(frameId);
                return;
              }
              inspector.current?.pin(x, y);
              setPinMode(false);
            }}
            onPinOpen={openThread}
            controlRef={canvas}
          />
          {mode === "hud" ? (
            <SmithPanel
              thread={smith}
              hasSession={Boolean(build.rootSessionId)}
              mode="hud"
              onMode={changeMode}
              finished={finished}
              onOpenThreadMarker={openThread}
            />
          ) : null}
        </div>
        <div className="ide__side">
          {selected ? (
            <Inspector
              frame={selected}
              view={view}
              verdict={verdicts[selected.id]}
              onVerdict={(verdict) => setVerdict(selected.id, verdict)}
              onPrevious={() => step(-1)}
              onNext={() => step(1)}
              onClose={() => setSelectedId(undefined)}
              annotations={annotations}
              onAnnotation={upsertAnnotation}
              smithMessages={smith.messages}
              onSendToSmith={sendToSmith}
              smithReady={Boolean(build.rootSessionId)}
              pinMode={pinMode}
              onPinMode={setPinMode}
              pinRef={inspector}
            />
          ) : subSession ? (
            <SessionThread thread={sub} session={view.sessions[subSession]} onClose={() => setSubSession(undefined)} />
          ) : (
            <>
              <div className="ide__tabs" role="tablist" aria-label="Build panels">
                <button type="button" role="tab" aria-selected={tab === "agents"} className={tab === "agents" ? "is-active" : undefined} onClick={() => setTab("agents")}>
                  Agents <b>{Object.keys(view.sessions).length}</b>
                </button>
                <button type="button" role="tab" aria-selected={tab === "findings"} className={tab === "findings" ? "is-active" : undefined} onClick={() => setTab("findings")}>
                  Findings <b>{annotations.filter((annotation) => annotation.status === "open").length}</b>
                </button>
              </div>
              <div className="ide__panel" role="tabpanel">
                {tab === "agents" ? (
                  <AgentTree view={view} onOpenThread={setSubSession} onSelectLayer={selectLayer} onReceipts={onReceipts} />
                ) : annotationsError ? (
                  <ErrorNotice error={annotationsError} compact />
                ) : (
                  <Findings
                    annotations={annotations}
                    smithMessages={smith.messages}
                    frameTitle={(frameId) => board.frames.find((frame) => frame.id === frameId)?.title}
                    onOpen={(annotation) => openThread(annotation.id)}
                  />
                )}
              </div>
            </>
          )}
        </div>
      </div>

      <StageStrip
        build={build}
        checkpoints={view.checkpoints}
        selectedLayerId={selected?.layerId}
        onSelectLayer={selectLayer}
        onCheckpoint={onCheckpoint}
      />
    </section>
  );
}
