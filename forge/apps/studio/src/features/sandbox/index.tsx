import type { Build, SandboxRun, SandboxStatus, SessionEvent, TestFlight, TestStep } from "@4ge/contracts";
import { Button, CadenceDots, Eyebrow, Led, Panel } from "@4ge/kit";
import { ExternalLink, FlaskConical } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { Link, useParams } from "react-router";
import { ForgeApiError, api, useRoute, type RouteState } from "../../lib/api.js";
import { useRouteStream } from "../../lib/sse.js";
import { ErrorNotice, StatusLed, shortTime } from "../../shell/ui.js";
import "./sandbox.css";

/**
 * The sandbox dry run (SPEC flow 8): Icarus brings the build up in local
 * Docker, bots play the staff user and the customer user, and every step
 * shows what the persona did, what it expected and a screenshot. All of it
 * comes from the server's sandbox routes and the build stream.
 */
export default function Sandbox() {
  const { buildId } = useParams();
  const status = useRoute("sandbox.status", "sandbox-status");
  const { reload } = status;
  useEffect(() => {
    const timer = setInterval(reload, 5000);
    return () => clearInterval(timer);
  }, [reload]);

  return (
    <section className="sbx" data-testid="feature-sandbox">
      <header className="sbx__head">
        <div>
          <Eyebrow>4GE // Sandbox</Eyebrow>
          <h1 className="sbx__title">Sandbox</h1>
          <p className="sbx__lede">Icarus runs the build in local Docker. Bots play your staff and your customers so you can dry-run the app before it joins your Apps.</p>
        </div>
        <DockerState status={status} />
      </header>
      {buildId ? <BuildSandbox key={buildId} buildId={buildId} dockerUp={status.data?.docker.available} onChanged={reload} /> : <BuildChooser />}
    </section>
  );
}

function DockerState({ status }: { status: RouteState<SandboxStatus> }) {
  if (status.error) return <ErrorNotice error={status.error} compact onRetry={status.reload} />;
  if (!status.data) return <CadenceDots tone="cream" label="Checking Docker" />;
  const { docker, ports } = status.data;
  return (
    <div className="sbx__docker" data-testid="docker-state">
      {docker.available ? (
        <Led state="ok">Docker {docker.version ?? "running"}</Led>
      ) : (
        <Led state="err">Docker is not running</Led>
      )}
      {!docker.available && docker.message ? <p className="sbx__muted">{docker.message}</p> : null}
      <ul className="sbx__ports" aria-label="Sandbox ports">
        {ports.map((port) => (
          <li key={port.name}>
            <Led state={port.free ? "ok" : "warn"}>
              {port.name} {port.port} {port.free ? "free" : "taken"}
            </Led>
          </li>
        ))}
      </ul>
    </div>
  );
}

function BuildChooser() {
  const builds = useRoute("builds.list", "builds");
  return (
    <Panel className="sbx__panel">
      <Eyebrow>Choose a build</Eyebrow>
      {builds.error ? <ErrorNotice error={builds.error} onRetry={builds.reload} /> : null}
      {builds.data && builds.data.builds.length === 0 ? (
        <p className="sbx__muted">
          No builds yet. <Link to="/imagistation">Start one in Imagistation.</Link>
        </p>
      ) : null}
      <ul className="sbx__builds">
        {builds.data?.builds.map((build) => (
          <li key={build.id}>
            <Link to={`/sandbox/${build.id}`} className="sbx__build">
              <FlaskConical size={16} aria-hidden="true" />
              <span>{build.title}</span>
              <StatusLed status={build.status} />
            </Link>
          </li>
        ))}
      </ul>
    </Panel>
  );
}

const lastFlightKey = (buildId: string) => `4ge.testflight.${buildId}`;

function rememberFlight(buildId: string, id: string) {
  try {
    window.localStorage.setItem(lastFlightKey(buildId), id);
  } catch {
    /* convenience only */
  }
}

function recallFlight(buildId: string): string | undefined {
  try {
    return window.localStorage.getItem(lastFlightKey(buildId)) ?? undefined;
  } catch {
    return undefined;
  }
}

const asError = (failure: unknown, route: string) =>
  failure instanceof ForgeApiError ? failure : new ForgeApiError({ status: 0, code: "network", message: String(failure), route });

function BuildSandbox({ buildId, dockerUp, onChanged }: { buildId: string; dockerUp: boolean | undefined; onChanged: () => void }) {
  const build = useRoute("builds.get", `build:${buildId}`, { params: { id: buildId } });
  const runs = useRoute("sandbox.runs.list", `runs:${buildId}`, { query: { buildId } });
  const [run, setRun] = useState<SandboxRun>();
  const [flight, setFlight] = useState<TestFlight>();
  const [busy, setBusy] = useState<"start" | "stop" | "flight">();
  const [error, setError] = useState<ForgeApiError>();

  useEffect(() => {
    if (!runs.data) return;
    const sorted = [...runs.data.runs].sort((left, right) => right.startedAt.localeCompare(left.startedAt));
    setRun((current) => current ?? sorted.find((item) => item.status === "running" || item.status === "starting") ?? sorted[0]);
  }, [runs.data]);

  useEffect(() => {
    const id = recallFlight(buildId);
    if (!id) return;
    api
      .call("sandbox.testFlight.get", { params: { id } })
      .then((result) => setFlight((current) => current ?? result.testFlight))
      .catch(() => {
        /* An old id may be gone; the page simply shows no test flight. */
      });
  }, [buildId]);

  const onEvent = useCallback((event: SessionEvent) => {
    if (event.type === "sandbox.updated") setRun((current) => (!current || current.id === event.data.run.id || event.data.run.status === "starting" ? event.data.run : current));
    if (event.type === "testflight.updated") setFlight(event.data.testFlight);
  }, []);
  const stream = useRouteStream("builds.stream", { id: buildId }, onEvent);

  // Poll a running flight too, so steps appear even if the stream drops.
  const flightLive = flight && (flight.status === "queued" || flight.status === "running");
  useEffect(() => {
    if (!flight || !flightLive) return;
    const timer = setInterval(() => {
      api
        .call("sandbox.testFlight.get", { params: { id: flight.id } })
        .then((result) => setFlight(result.testFlight))
        .catch(() => undefined);
    }, 3000);
    return () => clearInterval(timer);
  }, [flight, flightLive]);

  const start = async () => {
    setBusy("start");
    setError(undefined);
    try {
      const result = await api.call("sandbox.start", { body: { buildId } });
      setRun(result.run);
      onChanged();
    } catch (failure) {
      setError(asError(failure, "sandbox.start"));
    } finally {
      setBusy(undefined);
    }
  };
  const stop = async () => {
    if (!run) return;
    setBusy("stop");
    setError(undefined);
    try {
      const result = await api.call("sandbox.stop", { body: { runId: run.id } });
      setRun(result.run);
      onChanged();
    } catch (failure) {
      setError(asError(failure, "sandbox.stop"));
    } finally {
      setBusy(undefined);
    }
  };
  const fly = async () => {
    setBusy("flight");
    setError(undefined);
    try {
      const result = await api.call("sandbox.testFlight.start", { body: { buildId, ...(run ? { runId: run.id } : {}) } });
      setFlight(result.testFlight);
      rememberFlight(buildId, result.testFlight.id);
    } catch (failure) {
      setError(asError(failure, "sandbox.testFlight.start"));
    } finally {
      setBusy(undefined);
    }
  };

  const up = run && (run.status === "running" || run.status === "starting");
  return (
    <div className="sbx__body">
      <Panel className="sbx__panel">
        <div className="sbx__row">
          <div className="sbx__build-meta">
            <Eyebrow>{`Build // ${build.data?.build.title ?? buildId}`}</Eyebrow>
            {build.data ? <StatusLed status={build.data.build.status} /> : null}
            {build.error ? <ErrorNotice error={build.error} compact /> : null}
          </div>
          <div className="sbx__actions">
            <span className="sbx__muted">{stream.status === "open" ? "Live" : stream.status === "reconnecting" ? "Reconnecting" : "Connecting"}</span>
            {up ? (
              <Button onClick={() => void stop()} disabled={busy !== undefined}>
                {busy === "stop" ? "Stopping" : "Stop the sandbox"}
              </Button>
            ) : (
              <Button variant="primary" onClick={() => void start()} disabled={busy !== undefined || dockerUp === false} data-testid="sandbox-start">
                {busy === "start" ? "Starting" : "Start the sandbox"}
              </Button>
            )}
            <Button variant={up ? "primary" : "default"} onClick={() => void fly()} disabled={busy !== undefined || !up} data-testid="testflight-start">
              {busy === "flight" ? "Launching bots" : "Run a test flight"}
            </Button>
          </div>
        </div>
        {dockerUp === false ? <p className="sbx__muted">Start Docker Desktop first. The sandbox runs in local Docker.</p> : null}
        {!up && dockerUp !== false ? <p className="sbx__muted">A test flight needs the sandbox running.</p> : null}
        {error ? <ErrorNotice error={error} /> : null}
        {runs.error ? <ErrorNotice error={runs.error} compact onRetry={runs.reload} /> : null}
        {run ? <RunCard run={run} /> : runs.data ? <p className="sbx__muted">This build has not been in the sandbox yet.</p> : null}
      </Panel>
      {run?.viewerUrl && up ? (
        <Panel className="sbx__panel sbx__viewer">
          <div className="sbx__row">
            <Eyebrow>What the bots see</Eyebrow>
            <a href={run.viewerUrl} target="_blank" rel="noreferrer" className="sbx__link">
              <ExternalLink size={14} aria-hidden="true" /> Open the viewer
            </a>
          </div>
          <iframe className="sbx__frame" title="Sandbox browser viewer" src={run.viewerUrl} />
        </Panel>
      ) : null}
      {flight ? <FlightView flight={flight} build={build.data?.build} /> : null}
    </div>
  );
}

const FAILURE_WORDS: Record<string, string> = {
  docker_unavailable: "Docker is not running.",
  port_taken: "A sandbox port is taken by another program.",
  compose_failed: "docker compose could not bring the sandbox up.",
  app_unhealthy: "The app started but did not answer its health check.",
  cancelled: "The run was cancelled.",
  internal: "The sandbox hit an internal error.",
};

function RunCard({ run }: { run: SandboxRun }) {
  return (
    <div className="sbx__run" data-testid="sandbox-run">
      <div className="sbx__row">
        <StatusLed status={run.status} />
        <span className="sbx__mono">{run.composeProject}</span>
        <span className="sbx__muted">started {shortTime(run.startedAt)}</span>
      </div>
      <ul className="sbx__ports">
        <li>app {run.ports.app}</li>
        <li>browser {run.ports.cdp}</li>
        <li>viewer {run.ports.viewer}</li>
      </ul>
      {run.appUrl && run.status === "running" ? (
        <a href={run.appUrl} target="_blank" rel="noreferrer" className="sbx__link">
          <ExternalLink size={14} aria-hidden="true" /> Open the app
        </a>
      ) : null}
      {run.failure ? (
        <div className="studio-notice studio-notice--err" role="alert">
          <strong>{FAILURE_WORDS[run.failure.code] ?? "The sandbox failed."}</strong>
          <span>{run.failure.message}</span>
          {run.failure.code === "port_taken" && run.failure.port ? (
            <span>Port {run.failure.port} is in use. Close the program using it, then start the sandbox again.</span>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

function FlightView({ flight, build }: { flight: TestFlight; build: Build | undefined }) {
  const live = flight.status === "queued" || flight.status === "running";
  return (
    <Panel className="sbx__panel" data-testid="test-flight">
      <div className="sbx__row">
        <div>
          <Eyebrow>{`Test flight // ${flight.id}`}</Eyebrow>
          <StatusLed status={flight.status} />
        </div>
        {live ? <CadenceDots tone="patina" label="Bots are flying" /> : null}
      </div>
      {flight.summary ? <p className="sbx__summary">{flight.summary}</p> : null}
      <div className="sbx__personas">
        {flight.personas.map((persona) => {
          const steps = flight.steps.filter((step) => step.personaId === persona.id);
          return (
            <section key={persona.id} className={`sbx__persona is-${persona.role}`} aria-label={`${persona.name}, ${persona.role}`}>
              <header>
                <Eyebrow>{persona.role === "staff" ? "Staff user" : "Customer user"}</Eyebrow>
                <h3>{persona.name}</h3>
                <p className="sbx__muted">{persona.brief}</p>
              </header>
              <ol className="sbx__steps">
                {steps.map((step) => (
                  <StepRow key={step.id} step={step} buildId={flight.buildId} />
                ))}
                {steps.length === 0 ? <li className="sbx__muted">{live ? "Waiting for the first step" : "No steps recorded."}</li> : null}
              </ol>
            </section>
          );
        })}
      </div>
      {flight.layers.length ? (
        <section aria-label="Layer verdicts">
          <Eyebrow>Layers</Eyebrow>
          <ul className="sbx__verdicts">
            {flight.layers.map((verdict) => (
              <li key={verdict.layerId}>
                <StatusLed status={verdict.status}>
                  {build?.layers.find((layer) => layer.id === verdict.layerId)?.title ?? verdict.layerId}: {verdict.status === "not-covered" ? "not covered" : verdict.status}
                </StatusLed>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </Panel>
  );
}

function StepRow({ step, buildId }: { step: TestStep; buildId: string }) {
  const [broken, setBroken] = useState(false);
  return (
    <li className={`sbx__step is-${step.status}`} data-testid={`step-${step.id}`}>
      <div className="sbx__step-head">
        <StatusLed status={step.status} />
        {step.at ? <span className="sbx__muted">{shortTime(step.at)}</span> : null}
      </div>
      <p className="sbx__action">{step.action}</p>
      <p className="sbx__muted">Expects: {step.expectation}</p>
      {step.error ? <p className="sbx__error">{step.error}</p> : null}
      {step.screenshot ? (
        broken ? (
          <p className="sbx__muted">The screenshot could not be loaded from the server.</p>
        ) : (
          <a href={api.url("builds.files.get", { id: buildId }, { path: step.screenshot })} target="_blank" rel="noreferrer">
            <img
              className="sbx__shot"
              src={api.url("builds.files.get", { id: buildId }, { path: step.screenshot })}
              alt={`Screenshot after: ${step.action}`}
              loading="lazy"
              onError={() => setBroken(true)}
            />
          </a>
        )
      ) : null}
    </li>
  );
}
