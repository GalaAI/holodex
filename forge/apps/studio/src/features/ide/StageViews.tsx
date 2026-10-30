import {
  AGENT_ROSTER,
  ORCHESTRA,
  STAGE_LABELS,
  STAGE_ORDER,
  type AgentId,
  type Build,
  type Layer,
  type Stage,
  type WorkStatus,
} from "@4ge/contracts";
import { Eyebrow, Led } from "@4ge/kit";
import { Link } from "react-router";
import { StatusLed, statusWord } from "../../shell/ui.js";
import type { BuildView } from "./state.js";

/**
 * The UI each stage shows while it builds (SPEC: "the preview shows a UI for
 * each stage as it builds"): architecture map, orchestration graph,
 * interface artboards and deploy plus test flight. Each is drawn from the
 * live build, so it changes as layers land.
 */
export function StageView({ stage, view, interactive }: { stage: Stage; view: BuildView; interactive: boolean }) {
  switch (stage) {
    case "architecture":
      return <ArchitectureMap build={view.build} />;
    case "orchestration":
      return <OrchestrationGraph build={view.build} />;
    case "interface":
      return <InterfaceBoard build={view.build} interactive={interactive} />;
    case "deploy":
      return <DeployFlight view={view} />;
  }
}

const BOX = { width: 196, height: 52 };

function toneClass(status: WorkStatus | "not-run" | "running" | "passed" | "failed"): string {
  return `is-${status}`;
}

/** Every layer as a box in its stage column, with an edge for each dependency. */
export function ArchitectureMap({ build }: { build: Build }) {
  const width = 960;
  const height = 600;
  const columnWidth = width / STAGE_ORDER.length;
  const positions = new Map<string, { x: number; y: number; layer: Layer }>();
  STAGE_ORDER.forEach((stage, column) => {
    const layers = build.layers.filter((layer) => layer.stage === stage);
    const step = Math.min(76, (height - 96) / Math.max(1, layers.length));
    layers.forEach((layer, row) => {
      positions.set(layer.key, {
        x: column * columnWidth + (columnWidth - BOX.width) / 2,
        y: 72 + row * step,
        layer,
      });
    });
  });
  if (build.layers.length === 0) {
    return <EmptyStage text="No layers yet. Smith lays out the layers from the Blueprint when the build starts." />;
  }
  return (
    <svg className="stage-map" viewBox={`0 0 ${width} ${height}`} role="img" aria-label="Architecture map of the build's layers">
      {STAGE_ORDER.map((stage, column) => (
        <text key={stage} className="stage-map__col" x={column * columnWidth + columnWidth / 2} y={36} textAnchor="middle">
          {`// ${STAGE_LABELS[stage].toUpperCase()}`}
        </text>
      ))}
      {[...positions.values()].flatMap(({ layer, x, y }) =>
        layer.dependsOn.map((key) => {
          const from = positions.get(key);
          if (!from) return null;
          const x1 = from.x + BOX.width;
          const y1 = from.y + BOX.height / 2;
          const x2 = x;
          const y2 = y + BOX.height / 2;
          const bend = Math.max(24, Math.abs(x2 - x1) / 2);
          return (
            <path
              key={`${key}->${layer.key}`}
              className={`stage-map__edge ${toneClass(from.layer.status)}`}
              d={x2 > x1 ? `M${x1},${y1} C${x1 + bend},${y1} ${x2 - bend},${y2} ${x2},${y2}` : `M${from.x + BOX.width / 2},${from.y + BOX.height} L${x + BOX.width / 2},${y}`}
            />
          );
        }),
      )}
      {[...positions.values()].map(({ layer, x, y }) => (
        <g key={layer.id} className={`stage-map__node ${toneClass(layer.status)}`} transform={`translate(${x},${y})`}>
          <rect width={BOX.width} height={BOX.height} rx={6} />
          <text x={12} y={21} className="stage-map__title">
            {clip(layer.title, 24)}
          </text>
          <text x={12} y={39} className="stage-map__meta">
            {`${AGENT_ROSTER[layer.agent].name} // ${statusWord(layer.status)}`}
          </text>
        </g>
      ))}
    </svg>
  );
}

/** Smith delegates to the orchestra; each agent's layers hang off it with live status. */
export function OrchestrationGraph({ build }: { build: Build }) {
  const width = 960;
  const height = 600;
  const agents: AgentId[] = (["smith", ...ORCHESTRA] as AgentId[]).filter((agent) => agent === "smith" || build.layers.some((layer) => layer.agent === agent));
  const workers = agents.filter((agent) => agent !== "smith");
  const root = { x: 48, y: height / 2 - BOX.height / 2 };
  const agentY = (index: number) => 64 + (index * (height - 128)) / Math.max(1, workers.length - 1 || 1);
  const layerRows = workers.flatMap((agent) => build.layers.filter((layer) => layer.agent === agent));
  const smithLayers = build.layers.filter((layer) => layer.agent === "smith");
  const allLayers = [...smithLayers, ...layerRows];
  const layerStep = Math.min(60, (height - 64) / Math.max(1, allLayers.length));
  const layerPos = new Map(allLayers.map((layer, index) => [layer.id, { x: 700, y: 32 + index * layerStep }]));
  const agentPos = new Map(workers.map((agent, index) => [agent, { x: 374, y: workers.length === 1 ? root.y : agentY(index) }]));
  const aggregate = (agent: AgentId): WorkStatus => {
    const mine = build.layers.filter((layer) => layer.agent === agent);
    if (mine.some((layer) => layer.status === "failed")) return "failed";
    if (mine.some((layer) => layer.status === "running")) return "running";
    if (mine.length > 0 && mine.every((layer) => layer.status === "passed")) return "passed";
    if (mine.some((layer) => layer.status === "cancelled")) return "cancelled";
    return "pending";
  };
  if (build.layers.length === 0) {
    return <EmptyStage text="The orchestra has no work yet. Smith delegates layers once the build starts." />;
  }
  const curve = (x1: number, y1: number, x2: number, y2: number) =>
    `M${x1},${y1} C${(x1 + x2) / 2},${y1} ${(x1 + x2) / 2},${y2} ${x2},${y2}`;
  return (
    <svg className="stage-map" viewBox={`0 0 ${width} ${height}`} role="img" aria-label="Orchestration graph: Smith, the orchestra and their layers">
      {workers.map((agent) => {
        const position = agentPos.get(agent) as { x: number; y: number };
        return (
          <path
            key={`smith-${agent}`}
            className={`stage-map__edge ${toneClass(aggregate(agent))}`}
            d={curve(root.x + BOX.width, root.y + BOX.height / 2, position.x, position.y + BOX.height / 2)}
          />
        );
      })}
      {allLayers.map((layer) => {
        const target = layerPos.get(layer.id) as { x: number; y: number };
        const source = layer.agent === "smith" ? { x: root.x, y: root.y } : (agentPos.get(layer.agent) as { x: number; y: number });
        return (
          <path
            key={`edge-${layer.id}`}
            className={`stage-map__edge ${toneClass(layer.status)}`}
            d={curve(source.x + BOX.width, source.y + BOX.height / 2, target.x, target.y + 18)}
          />
        );
      })}
      <g className={`stage-map__node is-root ${toneClass(build.status === "failed" ? "failed" : build.status === "passed" ? "passed" : "running")}`} transform={`translate(${root.x},${root.y})`}>
        <rect width={BOX.width} height={BOX.height} rx={6} />
        <text x={12} y={21} className="stage-map__title">Smith</text>
        <text x={12} y={39} className="stage-map__meta">{`leads // ${statusWord(build.status)}`}</text>
      </g>
      {workers.map((agent) => {
        const position = agentPos.get(agent) as { x: number; y: number };
        const status = aggregate(agent);
        const count = build.layers.filter((layer) => layer.agent === agent).length;
        return (
          <g key={agent} className={`stage-map__node ${toneClass(status)}`} transform={`translate(${position.x},${position.y})`}>
            <rect width={BOX.width} height={BOX.height} rx={6} />
            <text x={12} y={21} className="stage-map__title">{AGENT_ROSTER[agent].name}</text>
            <text x={12} y={39} className="stage-map__meta">{`${count} layer${count === 1 ? "" : "s"} // ${statusWord(status)}`}</text>
          </g>
        );
      })}
      {allLayers.map((layer) => {
        const position = layerPos.get(layer.id) as { x: number; y: number };
        return (
          <g key={layer.id} className={`stage-map__leaf ${toneClass(layer.status)}`} transform={`translate(${position.x},${position.y})`}>
            <rect width={232} height={36} rx={4} />
            <circle cx={14} cy={18} r={4} />
            <text x={26} y={22} className="stage-map__meta">{clip(layer.title, 28)}</text>
          </g>
        );
      })}
    </svg>
  );
}

function InterfaceBoard({ build, interactive }: { build: Build; interactive: boolean }) {
  const layers = build.layers.filter((layer) => layer.stage === "interface");
  if (build.previewUrl) {
    return (
      <div className="stage-live">
        <iframe
          className="stage-live__frame"
          title="Live app"
          src={build.previewUrl}
          sandbox="allow-scripts allow-same-origin allow-forms allow-popups"
          style={{ pointerEvents: interactive ? "auto" : "none" }}
          data-stage-interactive={interactive ? "" : undefined}
        />
      </div>
    );
  }
  return (
    <div className="stage-list">
      <Eyebrow>Interface // layers</Eyebrow>
      {layers.length === 0 ? (
        <p className="stage-list__muted">No interface layers yet.</p>
      ) : (
        <ul>
          {layers.map((layer) => (
            <li key={layer.id}>
              <StatusLed status={layer.status} />
              <span className="stage-list__title">{layer.title}</span>
              <span className="stage-list__muted">
                {AGENT_ROSTER[layer.agent].name} // {layer.previews.length} artboard{layer.previews.length === 1 ? "" : "s"}
              </span>
            </li>
          ))}
        </ul>
      )}
      <p className="stage-list__muted">The running app shows here once Icarus brings it up. Artboards appear to the right as each layer renders them.</p>
    </div>
  );
}

function DeployFlight({ view }: { view: BuildView }) {
  const { sandbox, testFlight, build } = view;
  const passed = testFlight?.steps.filter((step) => step.status === "passed").length ?? 0;
  const failed = testFlight?.steps.filter((step) => step.status === "failed").length ?? 0;
  return (
    <div className="stage-list">
      <Eyebrow>Sandbox</Eyebrow>
      {sandbox ? (
        <ul>
          <li>
            <StatusLed status={sandbox.status} />
            <span className="stage-list__title">{sandbox.composeProject}</span>
            <span className="stage-list__muted">
              app {sandbox.ports.app} // browser {sandbox.ports.cdp} // viewer {sandbox.ports.viewer}
            </span>
          </li>
          {sandbox.failure ? (
            <li>
              <Led state="err">{sandbox.failure.message}</Led>
            </li>
          ) : null}
        </ul>
      ) : (
        <p className="stage-list__muted">The sandbox is not up for this build.</p>
      )}
      <Eyebrow>Test flight</Eyebrow>
      {testFlight ? (
        <>
          <ul>
            <li>
              <StatusLed status={testFlight.status} />
              <span className="stage-list__title">
                {testFlight.personas.map((persona) => `${persona.name} (${persona.role})`).join(", ")}
              </span>
              <span className="stage-list__muted">
                {passed} passed // {failed} failed // {testFlight.steps.length} steps
              </span>
            </li>
            {testFlight.layers.map((verdict) => (
              <li key={verdict.layerId}>
                <StatusLed status={verdict.status} />
                <span className="stage-list__muted">{build.layers.find((layer) => layer.id === verdict.layerId)?.title ?? verdict.layerId}</span>
              </li>
            ))}
          </ul>
          {testFlight.summary ? <p className="stage-list__muted">{testFlight.summary}</p> : null}
        </>
      ) : (
        <p className="stage-list__muted">No test flight has run yet.</p>
      )}
      <Link className="studio-link-button" to={`/sandbox/${build.id}`} data-stage-interactive="">
        Open the sandbox
      </Link>
    </div>
  );
}

function EmptyStage({ text }: { text: string }) {
  return (
    <div className="stage-list">
      <p className="stage-list__muted">{text}</p>
    </div>
  );
}

function clip(text: string, max: number): string {
  return text.length > max ? `${text.slice(0, max - 1)}...` : text;
}
