import { randomBytes } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
  PORTS,
  assertForgePort,
  blueprintSchema,
  buildIdSchema,
  buildSchema,
  makeId,
  type Blueprint,
  type Build,
  type BuildKind,
  type LayerPlan,
  type Persona,
  type Receipt,
  type SandboxFailure,
  type SandboxRun,
  type SandboxStatus,
  type StartTestFlightRequest,
  type TestFlight,
} from "@4ge/contracts";
import type { DataPaths } from "@4ge/contracts/node";
import { recordReceipt } from "@4ge/gate";
import { botResultsSchema, evaluateFlight, type BotResults } from "./checks.js";
import { checkDocker, createDockerCli, isPortFree, tail, type DockerAvailability, type DockerCli } from "./docker.js";
import { SandboxError } from "./errors.js";
import { STAFF_PATHS, personasFor } from "./personas.js";
import { SandboxStore } from "./store.js";

/**
 * Icarus's sandbox: one docker compose project ("4ge-sandbox", forge/sandbox/
 * compose.yaml) on the user's own Docker. start() copies a build's app into
 * the sandbox and brings up app, aethernet (Chromium with CDP and a noVNC
 * viewer) and, per test flight, the bots that play staff and customer.
 * Ports are fixed (17410 app, 17411 CDP, 17412 viewer), so one build is in
 * the sandbox at a time.
 *
 * Long work (image builds, installs, flights) runs in the background; every
 * change is published as a sandbox.updated / testflight.updated /
 * receipt.recorded event and saved under dataRoot()/sandbox.
 */

export const COMPOSE_PROJECT = "4ge-sandbox";

export type SandboxEventDraft =
  | { type: "sandbox.updated"; buildId: string; data: { run: SandboxRun } }
  | { type: "testflight.updated"; buildId: string; data: { testFlight: TestFlight } }
  | { type: "receipt.recorded"; buildId: string; data: { receipt: Receipt } }
  | { type: "log"; buildId: string; data: { level: "debug" | "info" | "warn" | "error"; message: string } };

export type SandboxListener = (event: SandboxEventDraft) => void;

export interface SandboxPorts {
  app: number;
  cdp: number;
  viewer: number;
}

export interface SandboxManagerOptions {
  paths: DataPaths;
  docker?: DockerCli;
  /** forge/sandbox/compose.yaml; FORGE_SANDBOX_COMPOSE overrides. */
  composeFile?: string;
  ports?: Partial<SandboxPorts>;
  timeouts?: Partial<{ build: number; seed: number; up: number; flight: number; down: number }>;
  env?: Readonly<Record<string, string | undefined>>;
}

/** forge/sandbox/compose.yaml, found from this file in src/ or dist/. */
export function defaultComposeFile(env: Readonly<Record<string, string | undefined>> = process.env): string {
  if (env.FORGE_SANDBOX_COMPOSE) return resolve(env.FORGE_SANDBOX_COMPOSE);
  const here = dirname(fileURLToPath(import.meta.url));
  return resolve(here, "..", "..", "..", "sandbox", "compose.yaml");
}

const token = () => randomBytes(9).toString("base64url");
const now = () => new Date().toISOString();
const dockerPath = (path: string) => resolve(path).replace(/\\/g, "/");

interface BuildContext {
  build?: Build;
  blueprint?: Blueprint;
  kind?: BuildKind;
  plans: Map<string, LayerPlan>;
}

interface ComposePsEntry {
  Service?: string;
  State?: string;
  Health?: string;
  ExitCode?: number;
}

export class SandboxManager {
  readonly paths: DataPaths;
  readonly composeFile: string;
  readonly ports: SandboxPorts;
  private readonly docker: DockerCli;
  private readonly store: SandboxStore;
  private readonly timeouts: { build: number; seed: number; up: number; flight: number; down: number };
  private readonly runs = new Map<string, SandboxRun>();
  private readonly flights = new Map<string, TestFlight>();
  private readonly listeners = new Set<SandboxListener>();
  /** Background work per run or flight id, so callers can wait and stop() can cancel. */
  private readonly work = new Map<string, { promise: Promise<void>; abort: AbortController }>();
  private queue: Promise<unknown> = Promise.resolve();
  private reconciled: Promise<void> | null = null;

  constructor(options: SandboxManagerOptions) {
    this.paths = options.paths;
    this.docker = options.docker ?? createDockerCli();
    this.composeFile = options.composeFile ?? defaultComposeFile(options.env);
    this.ports = {
      app: assertForgePort(options.ports?.app ?? PORTS.sandboxApp),
      cdp: assertForgePort(options.ports?.cdp ?? PORTS.sandboxCdp),
      viewer: assertForgePort(options.ports?.viewer ?? PORTS.sandboxViewer),
    };
    this.timeouts = {
      build: 20 * 60_000,
      seed: 10 * 60_000,
      up: 6 * 60_000,
      flight: 10 * 60_000,
      down: 2 * 60_000,
      ...options.timeouts,
    };
    this.store = new SandboxStore(this.paths);
    for (const run of this.store.loadRuns()) this.runs.set(run.id, run);
    for (const flight of this.store.loadFlights()) this.flights.set(flight.id, flight);
  }

  /* ------------------------------ events ------------------------------ */

  on(listener: SandboxListener): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  private emit(event: SandboxEventDraft): void {
    for (const listener of this.listeners) {
      try {
        listener(event);
      } catch {
        // A broken listener must not stop the sandbox.
      }
    }
  }

  private putRun(run: SandboxRun): SandboxRun {
    this.runs.set(run.id, run);
    this.store.saveRun(run);
    this.emit({ type: "sandbox.updated", buildId: run.buildId, data: { run } });
    return run;
  }

  private putFlight(flight: TestFlight, evidenceDir?: string): TestFlight {
    this.flights.set(flight.id, flight);
    this.store.saveFlight(flight, evidenceDir);
    this.emit({ type: "testflight.updated", buildId: flight.buildId, data: { testFlight: flight } });
    return flight;
  }

  private log(buildId: string, level: "info" | "warn" | "error", message: string): void {
    this.emit({ type: "log", buildId, data: { level, message: message.slice(0, 4000) } });
  }

  /* ------------------------------ docker ------------------------------ */

  private compose(
    args: string[],
    options: { source?: string; flight?: string; timeoutMs?: number; signal?: AbortSignal } = {},
  ) {
    return this.docker.exec(["compose", "-p", COMPOSE_PROJECT, "-f", this.composeFile, ...args], {
      cwd: dirname(this.composeFile),
      timeoutMs: options.timeoutMs ?? this.timeouts.down,
      ...(options.signal ? { signal: options.signal } : {}),
      env: {
        // Required by the compose file; commands that do not mount it (ps, down) get the sandbox folder.
        FORGE_SANDBOX_SOURCE: dockerPath(options.source ?? this.paths.sandbox),
        FORGE_SANDBOX_APP_PORT: String(this.ports.app),
        FORGE_SANDBOX_CDP_PORT: String(this.ports.cdp),
        FORGE_SANDBOX_VIEWER_PORT: String(this.ports.viewer),
        ...(options.flight ? { FORGE_SANDBOX_FLIGHT: dockerPath(options.flight) } : {}),
      },
    });
  }

  private async composePs(all = true): Promise<ComposePsEntry[]> {
    const result = await this.compose(["ps", ...(all ? ["-a"] : []), "--format", "json"], { timeoutMs: 30_000 });
    if (result.code !== 0) return [];
    const text = result.stdout.trim();
    if (!text) return [];
    try {
      const parsed: unknown = JSON.parse(text);
      return (Array.isArray(parsed) ? parsed : [parsed]) as ComposePsEntry[];
    } catch {
      return text
        .split(/\r?\n/)
        .map((line) => {
          try {
            return JSON.parse(line) as ComposePsEntry;
          } catch {
            return null;
          }
        })
        .filter((entry): entry is ComposePsEntry => entry !== null);
    }
  }

  private async requireDocker(): Promise<DockerAvailability> {
    const docker = await checkDocker(this.docker);
    if (!docker.available) {
      throw new SandboxError("docker_unavailable", docker.message ?? "Docker is not available.", {
        hint: "The sandbox runs on Docker Desktop on this PC. Start it and try again.",
      });
    }
    return docker;
  }

  /**
   * After a server restart, runs on disk may say running while Docker says
   * otherwise. Trust Docker.
   */
  private reconcile(): Promise<void> {
    this.reconciled ??= (async () => {
      const open = [...this.runs.values()].filter((run) => run.status === "starting" || run.status === "running" || run.status === "stopping");
      const openFlights = [...this.flights.values()].filter((flight) => flight.status === "running" || flight.status === "queued");
      if (open.length === 0 && openFlights.length === 0) return;
      const docker = await checkDocker(this.docker);
      const running = docker.available ? (await this.composePs(false)).filter((entry) => entry.State === "running") : [];
      const newest = open.sort((left, right) => right.startedAt.localeCompare(left.startedAt))[0];
      for (const run of open) {
        const alive = run === newest && run.status === "running" && running.some((entry) => entry.Service === "app");
        if (alive) continue;
        this.putRun({
          ...run,
          status: run.status === "starting" ? "failed" : "stopped",
          stoppedAt: now(),
          ...(run.status === "starting"
            ? { failure: { code: "cancelled", message: "The 4GE server stopped while this sandbox was starting." } }
            : {}),
        });
      }
      for (const flight of openFlights) {
        this.putFlight({
          ...flight,
          status: "cancelled",
          summary: "The 4GE server stopped during this test flight, so it did not finish.",
          finishedAt: now(),
        });
      }
    })();
    return this.reconciled;
  }

  /* ------------------------------ status ------------------------------ */

  async status(): Promise<SandboxStatus> {
    await this.reconcile();
    const docker = await checkDocker(this.docker);
    const ports = await Promise.all(
      (Object.entries(this.ports) as Array<[keyof SandboxPorts, number]>).map(async ([name, port]) => ({
        name,
        port,
        free: await isPortFree(port),
      })),
    );
    return {
      docker: {
        available: docker.available,
        ...(docker.version ? { version: docker.version.slice(0, 200) } : {}),
        ...(docker.message ? { message: docker.message.slice(0, 1000) } : {}),
      },
      ports,
      active: this.activeRuns(),
    };
  }

  activeRuns(): SandboxRun[] {
    return [...this.runs.values()].filter((run) => run.status === "starting" || run.status === "running" || run.status === "stopping");
  }

  listRuns(buildId?: string): SandboxRun[] {
    return [...this.runs.values()]
      .filter((run) => !buildId || run.buildId === buildId)
      .sort((left, right) => right.startedAt.localeCompare(left.startedAt));
  }

  getRun(id: string): SandboxRun {
    const run = this.runs.get(id);
    if (!run) throw new SandboxError("not_found", `Sandbox run ${id} was not found`);
    return run;
  }

  getTestFlight(id: string): TestFlight {
    const flight = this.flights.get(id);
    if (!flight) throw new SandboxError("not_found", `Test flight ${id} was not found`);
    return flight;
  }

  listTestFlights(buildId?: string): TestFlight[] {
    return [...this.flights.values()]
      .filter((flight) => !buildId || flight.buildId === buildId)
      .sort((left, right) => right.startedAt.localeCompare(left.startedAt));
  }

  /** Resolve once a run is no longer starting (or a flight no longer running). */
  async waitFor(id: string): Promise<void> {
    await this.work.get(id)?.promise;
  }

  /* ------------------------------- start ------------------------------- */

  private serial<T>(task: () => Promise<T>): Promise<T> {
    const next = this.queue.then(task, task);
    this.queue = next.catch(() => undefined);
    return next;
  }

  start(buildIdInput: string): Promise<SandboxRun> {
    return this.serial(async () => {
      await this.reconcile();
      const buildId = buildIdSchema.parse(buildIdInput);
      const appDir = this.paths.build(buildId).app;
      if (!existsSync(appDir) || readdirSync(appDir).length === 0) {
        throw new SandboxError("conflict", "This build has no app files yet, so there is nothing to put in the sandbox.", {
          hint: "Let the orchestra finish at least one layer that writes the app, then start the sandbox.",
        });
      }
      await this.requireDocker();
      const active = this.activeRuns();
      const same = active.find((run) => run.buildId === buildId && run.status !== "stopping");
      if (same) return same;
      if (active.length > 0) {
        const other = active[0] as SandboxRun;
        throw new SandboxError("conflict", `Another build (${other.buildId}) is in the sandbox. Stop it first.`, {
          hint: `Stop sandbox run ${other.id}, then start this build.`,
          detail: { runId: other.id, buildId: other.buildId },
        });
      }
      // Leftovers from a crash or a restarted server hold the ports; clear them.
      if ((await this.composePs(true)).length > 0) {
        await this.compose(["--profile", "bots", "down", "-v", "--remove-orphans"], { timeoutMs: this.timeouts.down });
      }
      for (const [name, port] of Object.entries(this.ports)) {
        if (!(await isPortFree(port))) {
          throw new SandboxError(
            "port_taken",
            `Port ${port} (sandbox ${name}) is in use by another program, so the sandbox cannot start.`,
            { port, hint: `Close whatever is using 127.0.0.1:${port} and try again.` },
          );
        }
      }
      const run = this.putRun({
        id: makeId("sandboxRun", token()),
        buildId,
        status: "starting",
        ports: { ...this.ports },
        composeProject: COMPOSE_PROJECT,
        startedAt: now(),
      });
      const abort = new AbortController();
      const promise = this.bringUp(run, appDir, abort.signal).finally(() => this.work.delete(run.id));
      this.work.set(run.id, { promise, abort });
      return run;
    });
  }

  private async bringUp(run: SandboxRun, appDir: string, signal: AbortSignal): Promise<void> {
    const evidence = this.store.evidenceDir(run.buildId, `sandbox-${run.id}`);
    mkdirSync(evidence, { recursive: true });
    const fail = async (failure: SandboxFailure, logs?: string) => {
      if (logs) writeFileSync(join(evidence, "compose.log"), logs, "utf8");
      await this.compose(["--profile", "bots", "down", "-v", "--remove-orphans"], { timeoutMs: this.timeouts.down });
      if (this.runs.get(run.id)?.status === "stopped") return;
      this.putRun({ ...run, status: "failed", stoppedAt: now(), failure });
      this.log(run.buildId, "error", failure.message);
    };
    const cancelled = () => signal.aborted;
    try {
      this.log(run.buildId, "info", "Icarus is preparing the sandbox images (the first time takes a few minutes).");
      const built = await this.compose(["--profile", "bots", "build"], { source: appDir, timeoutMs: this.timeouts.build, signal });
      if (cancelled()) return;
      if (built.code !== 0) {
        await fail(
          { code: "compose_failed", message: `The sandbox images did not build. ${tail(built.stderr || built.stdout, 8)}`.slice(0, 2000) },
          `${built.stdout}\n${built.stderr}`,
        );
        return;
      }

      this.log(run.buildId, "info", "Icarus is copying the app into the sandbox and installing it.");
      const seeded = await this.compose(["run", "--rm", "--no-deps", "-T", "seed"], { source: appDir, timeoutMs: this.timeouts.seed, signal });
      writeFileSync(join(evidence, "seed.log"), `${seeded.stdout}\n${seeded.stderr}`, "utf8");
      if (cancelled()) return;
      if (seeded.code !== 0) {
        await fail({
          code: "compose_failed",
          message: (seeded.timedOut
            ? "Preparing the app took too long, so the sandbox gave up."
            : `The app could not be prepared in the sandbox. ${tail(seeded.stdout.trim() ? seeded.stdout : seeded.stderr, 12)}`
          ).slice(0, 2000),
        });
        return;
      }

      this.log(run.buildId, "info", "Icarus is starting the app and the sandbox browser.");
      const up = await this.compose(
        ["up", "-d", "--no-deps", "--wait", "--wait-timeout", String(Math.round(this.timeouts.up / 1000)), "app", "aethernet"],
        { source: appDir, timeoutMs: this.timeouts.up + 30_000, signal },
      );
      if (cancelled()) return;
      if (up.code !== 0) {
        const output = `${up.stdout}\n${up.stderr}`;
        const logs = await this.compose(["logs", "--no-color", "--tail", "200"], { source: appDir, timeoutMs: 30_000 });
        const services = await this.composePs(true);
        const app = services.find((entry) => entry.Service === "app");
        const portMatch = /(?:port is already allocated|address already in use|bind)[^\n]*?(\d{4,5})/i.exec(output) ??
          /(\d{4,5})[^\n]*(?:port is already allocated|address already in use)/i.exec(output);
        const failure: SandboxFailure = portMatch
          ? {
              code: "port_taken",
              message: `Port ${portMatch[1]} was taken while the sandbox started. Close whatever uses it and try again.`,
              port: Number(portMatch[1]),
            }
          : app && (app.Health === "unhealthy" || app.State === "exited")
            ? {
                code: "app_unhealthy",
                message: `The app started but did not answer on its port. ${tail(
                  logs.stdout.split(/\r?\n/).filter((line) => line.startsWith("app")).join("\n"),
                  8,
                )}`.slice(0, 2000),
              }
            : { code: "compose_failed", message: `The sandbox did not come up. ${tail(output, 8)}`.slice(0, 2000) };
        await fail(failure, `${output}\n${logs.stdout}`);
        return;
      }
      const appLogs = await this.compose(["logs", "--no-color", "--tail", "200", "app"], { source: appDir, timeoutMs: 30_000 });
      writeFileSync(join(evidence, "app.log"), appLogs.stdout, "utf8");
      if (this.runs.get(run.id)?.status !== "starting") return;
      this.putRun({
        ...run,
        status: "running",
        appUrl: `http://127.0.0.1:${this.ports.app}/`,
        viewerUrl: `http://127.0.0.1:${this.ports.viewer}/vnc.html?autoconnect=1&resize=scale&view_only=1`,
      });
      this.log(run.buildId, "info", `The sandbox is running. App: http://127.0.0.1:${this.ports.app}/`);
    } catch (error) {
      await fail({ code: "internal", message: `The sandbox failed: ${error instanceof Error ? error.message : String(error)}`.slice(0, 2000) });
    }
  }

  /* ------------------------------- stop -------------------------------- */

  stop(runId: string): Promise<SandboxRun> {
    const pending = this.work.get(runId);
    pending?.abort.abort();
    for (const flight of this.flights.values()) {
      if (flight.runId === runId && flight.status === "running") this.work.get(flight.id)?.abort.abort();
    }
    return this.serial(async () => {
      const run = this.getRun(runId);
      if (run.status === "stopped" || run.status === "failed") return run;
      this.putRun({ ...run, status: "stopping" });
      await pending?.promise.catch(() => undefined);
      const down = await this.compose(["--profile", "bots", "down", "-v", "--remove-orphans"], { timeoutMs: this.timeouts.down });
      const latest = this.getRun(runId);
      if (down.code !== 0) {
        this.log(run.buildId, "warn", `Docker reported a problem while stopping the sandbox: ${tail(down.stderr, 4)}`);
      }
      return this.putRun({
        ...latest,
        status: "stopped",
        stoppedAt: now(),
        ...(run.status === "starting" ? { failure: { code: "cancelled", message: "Stopped before the sandbox finished starting." } } : {}),
      });
    });
  }

  /** Stop every active run (server shutdown). */
  async close(): Promise<void> {
    for (const run of this.activeRuns()) {
      await this.stop(run.id).catch(() => undefined);
    }
  }

  /* ---------------------------- test flight ---------------------------- */

  private loadBuildContext(buildId: string): BuildContext {
    const build = this.paths.build(buildId);
    const context: BuildContext = { plans: new Map() };
    const readJson = (path: string): unknown => {
      try {
        return existsSync(path) ? JSON.parse(readFileSync(path, "utf8")) : undefined;
      } catch {
        return undefined;
      }
    };
    const record = buildSchema.safeParse(readJson(build.record));
    if (record.success) context.build = record.data;
    const blueprintId = context.build?.blueprintId;
    if (blueprintId) {
      const raw = readJson(join(this.paths.atlasBlueprints, `${blueprintId.slice("blueprint:".length)}.json`)) as
        | { blueprint?: unknown }
        | undefined;
      const parsed = blueprintSchema.safeParse(raw && typeof raw === "object" && "blueprint" in raw ? raw.blueprint : raw);
      if (parsed.success) {
        context.blueprint = parsed.data;
        context.kind = parsed.data.kind;
        for (const stage of parsed.data.stages) for (const layer of stage.layers) context.plans.set(layer.key, layer);
      }
    }
    if (!context.kind && context.build?.seedId) {
      const seed = readJson(join(this.paths.seeds, `${context.build.seedId}.json`)) as { kind?: BuildKind } | undefined;
      if (seed?.kind) context.kind = seed.kind;
    }
    return context;
  }

  startTestFlight(request: StartTestFlightRequest): Promise<TestFlight> {
    return this.serial(async () => {
      await this.reconcile();
      const buildId = buildIdSchema.parse(request.buildId);
      const run = request.runId
        ? this.getRun(request.runId)
        : this.listRuns(buildId).find((candidate) => candidate.status === "running" || candidate.status === "starting");
      if (!run) {
        throw new SandboxError("conflict", "This build is not in the sandbox yet. Start the sandbox first, then run the test flight.", {
          hint: "POST /api/sandbox/start with this buildId.",
        });
      }
      if (run.buildId !== buildId) {
        throw new SandboxError("bad_request", `Sandbox run ${run.id} belongs to build ${run.buildId}, not ${buildId}.`);
      }
      if (run.status !== "running") {
        throw new SandboxError(
          "conflict",
          run.status === "starting"
            ? "The sandbox is still starting. Run the test flight once it says running."
            : `The sandbox run is ${run.status}. Start the sandbox again, then run the test flight.`,
        );
      }
      const busy = [...this.flights.values()].find((flight) => flight.runId === run.id && flight.status === "running");
      if (busy) {
        throw new SandboxError("conflict", `Test flight ${busy.id} is still running in this sandbox. Wait for it to finish.`, {
          detail: { testFlightId: busy.id },
        });
      }
      const context = this.loadBuildContext(buildId);
      const personas: Persona[] = personasFor({ requested: request.personas, blueprint: context.blueprint, kind: context.kind });
      const flight = this.putFlight({
        id: makeId("testFlight", token()),
        buildId,
        runId: run.id,
        status: "running",
        personas,
        steps: [],
        layers: [],
        startedAt: now(),
      });
      const abort = new AbortController();
      const promise = this.fly(flight, run, context, abort.signal).finally(() => this.work.delete(flight.id));
      this.work.set(flight.id, { promise, abort });
      return flight;
    });
  }

  private async fly(flight: TestFlight, run: SandboxRun, context: BuildContext, signal: AbortSignal): Promise<void> {
    const buildPaths = this.paths.build(flight.buildId);
    const evidence = this.store.evidenceDir(flight.buildId, flight.id);
    const evidenceRel = `evidence/${flight.id}`;
    mkdirSync(evidence, { recursive: true });
    const plan = {
      flightId: flight.id,
      browserUrl: `http://127.0.0.1:${this.ports.app}/`,
      directUrl: "http://app:3000/",
      cdp: { host: "aethernet", port: 9223 },
      personas: flight.personas,
      staffPaths: [...STAFF_PATHS],
      limits: { pages: 6, buttons: 3, forms: 2, stepTimeoutMs: 20_000 },
    };
    writeFileSync(join(evidence, "plan.json"), `${JSON.stringify(plan, null, 2)}\n`, "utf8");
    try {
      this.log(flight.buildId, "info", `Icarus is flying ${flight.personas.map((persona) => persona.name).join(" and ")} through the app.`);
      const result = await this.compose(["--profile", "bots", "run", "--rm", "--no-deps", "-T", "bots"], {
        source: buildPaths.app,
        flight: evidence,
        timeoutMs: this.timeouts.flight,
        signal,
      });
      writeFileSync(join(evidence, "bots.log"), `${result.stdout}\n${result.stderr}`, "utf8");
      if (signal.aborted) {
        this.putFlight(
          { ...flight, status: "cancelled", summary: "The sandbox was stopped during the test flight.", finishedAt: now() },
          evidence,
        );
        return;
      }
      let results: BotResults | null = null;
      let botsFailure: string | undefined;
      const resultsFile = join(evidence, "results.json");
      if (existsSync(resultsFile)) {
        const parsed = botResultsSchema.safeParse(JSON.parse(readFileSync(resultsFile, "utf8")));
        if (parsed.success) results = parsed.data;
        else botsFailure = "The bots wrote results this version of 4GE cannot read.";
      } else {
        botsFailure = result.timedOut
          ? "The bots ran out of time before they finished."
          : `The bots did not finish. ${tail(result.stderr || result.stdout, 6)}`;
      }
      if (results && results.personas.length === 0 && results.issues.length > 0) {
        botsFailure = `The bots could not start: ${results.issues[0]}`;
        results = null;
      }
      const evaluation = evaluateFlight({
        flightId: flight.id,
        appReady: this.runs.get(run.id)?.status === "running",
        results,
        ...(botsFailure ? { botsFailure } : {}),
        build: context.build,
        plans: context.plans,
        appDir: buildPaths.app,
        evidenceRel,
        newReceiptToken: token,
      });
      for (const layer of evaluation.layers) {
        if (!layer.receipt) continue;
        try {
          recordReceipt(layer.receipt, this.paths);
          this.emit({ type: "receipt.recorded", buildId: flight.buildId, data: { receipt: layer.receipt } });
        } catch (error) {
          this.log(flight.buildId, "warn", `A test flight receipt for ${layer.layerId} could not be written: ${String(error).slice(0, 300)}`);
        }
      }
      this.putFlight(
        {
          ...flight,
          status: evaluation.status,
          steps: evaluation.steps,
          layers: evaluation.layers.map((layer) => ({ layerId: layer.layerId, status: layer.status })),
          summary: evaluation.summary,
          finishedAt: now(),
        },
        evidence,
      );
      this.log(flight.buildId, evaluation.status === "passed" ? "info" : "warn", evaluation.summary);
    } catch (error) {
      this.putFlight(
        {
          ...flight,
          status: "failed",
          summary: `The test flight could not run: ${error instanceof Error ? error.message : String(error)}`.slice(0, 4000),
          finishedAt: now(),
        },
        evidence,
      );
    }
  }
}

/** One manager per data root, so the server routes and the orchestra share it. */
const managers = new Map<string, SandboxManager>();

export function sandboxManagerFor(paths: DataPaths, options: Omit<SandboxManagerOptions, "paths"> = {}): SandboxManager {
  const key = resolve(paths.root);
  let manager = managers.get(key);
  if (!manager) {
    manager = new SandboxManager({ ...options, paths });
    managers.set(key, manager);
  }
  return manager;
}

/** Forget cached managers (tests). */
export function resetSandboxManagers(): void {
  managers.clear();
}

/** Write a layer verdict onto a Build record; the orchestra owns build.json and calls this. */
export function applyTestFlightToBuild(build: Build, flight: TestFlight, receipts: readonly Receipt[] = []): Build {
  const byLayer = new Map(flight.layers.map((entry) => [entry.layerId, entry.status]));
  const receiptByLayer = new Map(receipts.filter((receipt) => receipt.layerId).map((receipt) => [receipt.layerId as string, receipt.id]));
  return {
    ...build,
    layers: build.layers.map((layer) => {
      const verdict = byLayer.get(layer.id);
      if (!verdict || verdict === "not-covered") return layer;
      const receiptId = receiptByLayer.get(layer.id);
      return { ...layer, testFlight: verdict, ...(receiptId ? { receiptId } : {}) };
    }),
    updatedAt: now(),
  };
}
