import { existsSync, mkdirSync, readdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { PlanError, planBlueprint, validateBamfDocument } from "@4ge/bamf";
import {
  STAGE_LABELS,
  STAGE_ORDER,
  STAGE_OWNERS,
  blueprintSchema,
  buildSchema,
  checkpointSchema,
  type Blueprint,
  type Build,
  type Checkpoint,
  type CheckpointDecision,
  type CreateBuildRequest,
  type Layer,
  type LayerPlan,
  type Preview,
  type Receipt,
  type Seed,
  type Sprint,
  type Stage,
  type StageGateDecision,
  type StagePlan,
} from "@4ge/contracts";
import type { DataPaths } from "@4ge/contracts/node";
import type { SandboxRun } from "@4ge/contracts";
import { evaluateAllStages, loadReceipts, receiptSatisfies, recordReceipt, stageSatisfied, type GateOptions } from "@4ge/gate";
import { AGENT_DEFINITIONS, agentName } from "./agents.js";
import { applyParameters } from "./blueprint.js";
import { OrchestraError, isAbort, isOrchestraError, messageOf, notFound } from "./errors.js";
import type { ModelClient } from "./model.js";
import { LayerNotConnected, type AtlasPort, type SandboxPort } from "./ports.js";
import type { SeedStore } from "./seeds.js";
import type { SessionManager } from "./sessions.js";
import { SANDBOX_LAUNCH_KEY, ScaffoldError, scaffoldStarter } from "@4ge/atlas";
import { clip, newId, now, plainText, readJson, sleep, slugify, writeJson, type EventSink } from "./util.js";
import { runLayerAgent, type LayerOutcome } from "./worker.js";

/**
 * Smith's build runner (SPEC flow 5 to 8). A build runs its Blueprint's
 * stage plan: one sprint per stage, the sprint's layers in parallel child
 * sessions under Smith's root session (waves follow dependsOn, taken from
 * the @4ge/bamf planner), every layer a .bamf step that writes real files
 * into dataRoot()/builds/<id>/app, a receipt per layer recorded and
 * verified by @4ge/gate, and a checkpoint at the end of each sprint that
 * waits for the person. A stage starts only when the gate admits it.
 *
 * Preview refs are relative to the build folder (builds/<id>/): the layer
 * report at previews/<key>.md and any page a layer wrote at app/<path>.
 */

export interface BuildDeps {
  paths: DataPaths;
  sessions: SessionManager;
  seeds: SeedStore;
  models: ModelClient;
  events: EventSink;
  atlas: AtlasPort;
  sandbox: SandboxPort;
  startersDir: string;
  /** Tries per layer, counting the first. */
  maxAttempts: number;
  /** How long Icarus waits for a test flight. */
  testFlightTimeoutMs: number;
  /** Smith's design conversation, started beside the first sprint. */
  onBuildStarted?: (build: Build) => Promise<void>;
}

interface BuildPlanContext {
  blueprint: Blueprint;
  seed: Seed;
  commands: Map<string, unknown>;
  waves: Map<string, number>;
}

const ACTIVE: ReadonlySet<Build["status"]> = new Set(["queued", "running", "test-flight"]);

export class BuildRunner {
  private readonly builds = new Map<string, Build>();
  private readonly contexts = new Map<string, BuildPlanContext>();
  /** One sandbox per build: launch and test flights take turns (parallel test-flight layers share it). */
  private readonly sandboxTurns = new Map<string, Promise<unknown>>();
  private readonly tasks = new Map<string, Set<Promise<unknown>>>();

  constructor(private readonly deps: BuildDeps) {
    this.load();
  }

  /* ------------------------------ storage ------------------------------ */

  private load(): void {
    let names: string[] = [];
    try {
      names = readdirSync(this.deps.paths.builds);
    } catch {
      return;
    }
    for (const name of names) {
      if (!/^bld_[A-Za-z0-9_-]+$/.test(name)) continue;
      const build = readJson(this.deps.paths.build(name).record, buildSchema);
      if (!build) continue;
      if (ACTIVE.has(build.status)) {
        build.status = "failed";
        build.error = "The 4GE server stopped while this build ran. Open the last checkpoint and choose revise to pick it up again.";
        build.layers = build.layers.map((layer) =>
          layer.status === "running" || layer.status === "pending"
            ? { ...layer, status: "failed", error: "Interrupted when the 4GE server stopped" }
            : layer,
        );
        build.updatedAt = now();
        writeJson(this.deps.paths.build(name).record, build);
      }
      this.builds.set(build.id, build);
    }
  }

  private save(build: Build): Build {
    const checked = buildSchema.parse({ ...build, updatedAt: now() });
    this.builds.set(checked.id, checked);
    writeJson(this.deps.paths.build(checked.id).record, checked);
    return checked;
  }

  private context(buildId: string): BuildPlanContext {
    const cached = this.contexts.get(buildId);
    if (cached) return cached;
    const build = this.get(buildId);
    const blueprint = readJson(join(this.deps.paths.build(buildId).root, "blueprint.json"), blueprintSchema);
    if (!blueprint) throw new OrchestraError("internal", `The Blueprint for build ${buildId} is missing from its folder`);
    const seed = this.deps.seeds.get(build.seedId);
    const plan = planBlueprint(blueprint);
    const context: BuildPlanContext = {
      blueprint,
      seed,
      commands: new Map(plan.steps.filter((step) => step.layerKey).map((step) => [step.layerKey as string, step.command])),
      waves: new Map(plan.steps.filter((step) => step.layerKey).map((step) => [step.layerKey as string, step.wave])),
    };
    this.contexts.set(buildId, context);
    return context;
  }

  private track<T>(buildId: string, work: Promise<T>): void {
    const set = this.tasks.get(buildId) ?? new Set();
    this.tasks.set(buildId, set);
    const wrapped = work.catch((error: unknown) => this.failBuild(buildId, error)).finally(() => set.delete(wrapped));
    set.add(wrapped);
  }

  /** Resolves when no background work runs for the build (tests, shutdown). */
  async whenIdle(buildId: string): Promise<void> {
    for (;;) {
      const set = this.tasks.get(buildId);
      if (!set || set.size === 0) return;
      await Promise.allSettled([...set]);
    }
  }

  /* ------------------------------ reading ------------------------------ */

  list(): Build[] {
    return [...this.builds.values()].sort((left, right) => right.createdAt.localeCompare(left.createdAt));
  }

  get(id: string): Build {
    const build = this.builds.get(id);
    if (!build) throw notFound(`Build ${id}`);
    return build;
  }

  has(id: string): boolean {
    return this.builds.has(id);
  }

  blueprintOf(buildId: string): Blueprint {
    return this.context(buildId).blueprint;
  }

  seedOf(buildId: string): Seed {
    return this.context(buildId).seed;
  }

  checkpoints(buildId: string): Checkpoint[] {
    this.get(buildId);
    const dir = this.deps.paths.build(buildId).checkpoints;
    let names: string[] = [];
    try {
      names = readdirSync(dir).filter((name) => name.endsWith(".json"));
    } catch {
      return [];
    }
    return names
      .map((name) => readJson(join(dir, name), checkpointSchema))
      .filter((checkpoint): checkpoint is Checkpoint => checkpoint !== undefined)
      .sort((left, right) => left.createdAt.localeCompare(right.createdAt));
  }

  private checkpoint(buildId: string, checkpointId: string): Checkpoint {
    const found = readJson(join(this.deps.paths.build(buildId).checkpoints, `${checkpointId}.json`), checkpointSchema);
    if (!found) throw notFound(`Checkpoint ${checkpointId}`);
    return found;
  }

  private saveCheckpoint(checkpoint: Checkpoint): Checkpoint {
    const checked = checkpointSchema.parse(checkpoint);
    writeJson(join(this.deps.paths.build(checked.buildId).checkpoints, `${checked.id}.json`), checked);
    return checked;
  }

  gateOptions(build: Build): GateOptions {
    const layersByStage: Partial<Record<Stage, string[]>> = {};
    for (const layer of build.layers) (layersByStage[layer.stage] ??= []).push(layer.id);
    return { layersByStage };
  }

  receipts(buildId: string): { receipts: Receipt[]; gate: StageGateDecision[]; broken: string[] } {
    const build = this.get(buildId);
    const { loaded, broken } = loadReceipts(buildId, this.deps.paths);
    const receipts = loaded.map(({ file: _file, provenance: _provenance, ...receipt }) => receipt);
    return { receipts, gate: evaluateAllStages(loaded, this.gateOptions(build)), broken };
  }

  /* ------------------------------ events ------------------------------ */

  private emitBuild(build: Build): void {
    this.deps.events.publish({
      type: "build.status",
      buildId: build.id,
      ...(build.rootSessionId ? { sessionId: build.rootSessionId } : {}),
      data: { buildId: build.id, status: build.status, ...(build.error ? { error: build.error } : {}) },
    });
  }

  private emitSprint(build: Build, sprint: Sprint): void {
    this.deps.events.publish({
      type: "sprint.updated",
      buildId: build.id,
      ...(build.rootSessionId ? { sessionId: build.rootSessionId } : {}),
      data: { sprint },
    });
  }

  private emitPreview(build: Build, preview: Preview, sessionId?: string): void {
    const session = sessionId ?? build.rootSessionId;
    this.deps.events.publish({
      type: "preview.updated",
      buildId: build.id,
      ...(session ? { sessionId: session } : {}),
      data: { buildId: build.id, preview },
    });
  }

  private patchLayer(buildId: string, layerId: string, patch: Partial<Layer>): Layer {
    const build = this.get(buildId);
    const index = build.layers.findIndex((layer) => layer.id === layerId);
    if (index < 0) throw notFound(`Layer ${layerId}`);
    const current = build.layers[index] as Layer;
    // A cancelled build keeps its layers cancelled; late results are dropped.
    if (build.status === "cancelled" && patch.status && patch.status !== "cancelled") return current;
    const next: Layer = { ...current, ...patch };
    for (const key of Object.keys(patch) as Array<keyof Layer>) {
      if (patch[key] === undefined) delete next[key];
    }
    const layers = [...build.layers];
    layers[index] = next;
    const saved = this.save({ ...build, layers });
    const layer = saved.layers[index] as Layer;
    this.deps.events.publish({
      type: "layer.updated",
      buildId,
      ...((layer.sessionId ?? saved.rootSessionId) ? { sessionId: layer.sessionId ?? saved.rootSessionId } : {}),
      data: { layer },
    });
    return layer;
  }

  private failBuild(buildId: string, error: unknown): void {
    if (!this.builds.has(buildId)) return;
    const build = this.get(buildId);
    if (build.status === "cancelled" || isAbort(error)) return;
    const message = messageOf(error);
    const saved = this.save({ ...build, status: "failed", error: clip(message, 2000), finishedAt: now() });
    this.emitBuild(saved);
    if (saved.rootSessionId) {
      this.deps.sessions.addMessage(saved.rootSessionId, {
        role: "assistant",
        agent: "smith",
        content: `The build stopped: ${message}`,
      });
      this.deps.sessions.update(saved.rootSessionId, { status: "failed", summary: "The build stopped", error: message });
    }
  }

  /* ------------------------------ create ------------------------------ */

  private async loadBlueprint(seed: Seed, blueprintId: string): Promise<Blueprint> {
    const held = this.deps.seeds.heldBlueprint(seed.id);
    if (held && held.blueprintId === blueprintId) return held;
    try {
      const entry = await this.deps.atlas.get(blueprintId);
      if (entry) return entry.blueprint;
    } catch (error) {
      if (error instanceof LayerNotConnected) {
        throw new OrchestraError(
          "conflict",
          `The Atlas is not connected, so the Blueprint ${blueprintId} cannot be read.`,
          "Start 4GE with the Atlas layer, or ask Prometheus to rewrite the Seed Scroll.",
        );
      }
      throw error;
    }
    throw notFound(`Blueprint ${blueprintId}`);
  }

  async create(request: CreateBuildRequest): Promise<Build> {
    const seed = this.deps.seeds.get(request.seedId);
    const blueprintId = request.blueprintId ?? seed.blueprintId;
    if (!blueprintId) {
      throw new OrchestraError("bad_request", "This Seed has no Blueprint yet", "Ask Prometheus to write the Seed Scroll first.");
    }
    const running = this.list().find((build) => build.seedId === seed.id && ACTIVE.has(build.status));
    if (running) throw new OrchestraError("conflict", "This Seed is already building", `Open build ${running.id}.`);
    const blueprint = applyParameters(await this.loadBlueprint(seed, blueprintId), seed.parameters);
    let plan;
    try {
      plan = planBlueprint(blueprint);
    } catch (error) {
      if (error instanceof PlanError) throw new OrchestraError("bad_request", `The Blueprint cannot run: ${error.message}`);
      throw error;
    }
    const commands = plan.steps.map((step) => step.command);
    const bamf = validateBamfDocument({ commands });
    if (!bamf.ok) {
      throw new OrchestraError("internal", `The Blueprint plan is not a valid .bamf script: ${JSON.stringify(bamf.problems.slice(0, 3))}`);
    }

    const id = newId("build");
    const folders = this.deps.paths.build(id);
    for (const dir of [folders.root, folders.app, folders.receipts, folders.checkpoints, folders.previews, folders.evidence, folders.design]) {
      mkdirSync(dir, { recursive: true });
    }
    // The starter goes into the empty app folder first, with the build's own
    // values (name, tagline, contact) set in it by the Atlas's scaffold.
    let scaffoldNote: { level: "info" | "warn"; text: string } | undefined;
    if (blueprint.starter) {
      try {
        const scaffolded = scaffoldStarter({
          starter: blueprint.starter,
          target: folders.app,
          values: { appName: seed.title, ...seed.parameters },
          startersDir: this.deps.startersDir,
        });
        scaffoldNote = {
          level: scaffolded.warnings.length ? "warn" : "info",
          text: `Scaffolded the app from the ${blueprint.starter} starter (${scaffolded.files.length} files)${
            scaffolded.warnings.length ? `; not applied: ${scaffolded.warnings.join("; ")}` : ""
          }.`,
        };
      } catch (error) {
        if (!(error instanceof ScaffoldError)) throw error;
        scaffoldNote = {
          level: "warn",
          text: `The ${blueprint.starter} starter could not be used (${error.message}), so the orchestra writes the app from scratch.`,
        };
      }
    }
    writeJson(join(folders.root, "blueprint.json"), blueprint);
    writeJson(join(folders.root, "bamf", "plan.bamf.json"), { commands });
    plan.steps.forEach((step) => {
      writeJson(join(folders.root, "bamf", "steps", `${String(step.index).padStart(2, "0")}-${slugify(step.command.id)}.bamf.json`), step.command);
    });

    const root = this.deps.sessions.create({
      agent: "smith",
      kind: "build",
      task: `Build ${seed.title} from the ${blueprint.name} Blueprint`,
      buildId: id,
      subject: { type: "build", id, label: seed.title },
      status: "running",
      summary: "Reading the Seed Scroll",
    });
    const layers: Layer[] = blueprint.stages.flatMap((stagePlan) =>
      stagePlan.layers.map((layer) => ({
        id: newId("layer"),
        buildId: id,
        key: layer.key,
        kind: layer.kind,
        stage: stagePlan.stage,
        agent: layer.agent,
        title: layer.title,
        status: "pending" as const,
        dependsOn: layer.dependsOn,
        previews: [],
        testFlight: "not-run" as const,
      })),
    );
    const planned = new Set(blueprint.stages.map((stagePlan) => stagePlan.stage));
    const at = now();
    let build = this.save(
      buildSchema.parse({
        id,
        seedId: seed.id,
        blueprintId: blueprint.blueprintId,
        title: seed.title,
        status: "queued",
        stages: STAGE_ORDER.map((stage) => ({
          stage,
          owner: STAGE_OWNERS[stage].owner,
          status: planned.has(stage) ? "pending" : "passed",
        })),
        layers,
        sprints: [],
        rootSessionId: root.id,
        createdAt: at,
        updatedAt: at,
      }),
    );
    this.contexts.set(id, {
      blueprint,
      seed,
      commands: new Map(plan.steps.filter((step) => step.layerKey).map((step) => [step.layerKey as string, step.command])),
      waves: new Map(plan.steps.filter((step) => step.layerKey).map((step) => [step.layerKey as string, step.wave])),
    });
    this.emitBuild(build);

    // A stage the Blueprint does not plan is recorded as such, so the gate can pass it on evidence.
    for (const stage of STAGE_ORDER.filter((item) => !planned.has(item))) {
      const note = `.4ge/stages/${stage}.md`;
      const notePath = join(folders.app, ".4ge", "stages", `${stage}.md`);
      mkdirSync(join(folders.app, ".4ge", "stages"), { recursive: true });
      writeFileSync(notePath, `# ${STAGE_LABELS[stage]}\n\nThe ${blueprint.name} Blueprint plans no ${stage} layers for this build.\n`, "utf8");
      recordReceipt(
        {
          id: newId("receipt"),
          buildId: id,
          stage,
          agentId: STAGE_OWNERS[stage].owner,
          artifacts: [note],
          checks: [{ name: "The Blueprint plans no layers for this stage", passed: true, detail: blueprint.blueprintId }],
          recordedAt: now(),
        },
        this.deps.paths,
      );
    }

    if (scaffoldNote) this.deps.sessions.log(root.id, scaffoldNote.level, scaffoldNote.text);

    this.deps.seeds.update(seed.id, { status: "building", decision: "now", buildId: id });
    const first = blueprint.stages[0] as StagePlan;
    this.deps.sessions.addMessage(root.id, {
      role: "assistant",
      agent: "smith",
      content: [
        `I read your Seed Scroll for ${seed.title} and the ${blueprint.name} Blueprint.`,
        `The build runs ${blueprint.stages.length} sprints: ${blueprint.stages.map((stagePlan) => STAGE_LABELS[stagePlan.stage]).join(", ")}.`,
        `Starting the ${STAGE_LABELS[first.stage]} sprint now: ${first.layers.map((layer) => `${layer.title} (${agentName(layer.agent)})`).join(", ")}.`,
        "While they build, I will lay out a design system and landing page directions on the canvas for you to pick from.",
      ].join(" "),
    });
    build = this.save({ ...build, status: "running" });
    this.emitBuild(build);
    this.track(id, this.runStage(id, first.stage));
    if (this.deps.onBuildStarted) {
      const started = this.deps.onBuildStarted(build);
      // The design conversation never fails the build; it reports in its own session.
      this.track(id, started.catch(() => undefined));
    }
    return build;
  }

  /* ------------------------------ sprints ------------------------------ */

  private nextStage(buildId: string, stage: Stage): Stage | undefined {
    const stages = this.context(buildId).blueprint.stages.map((stagePlan) => stagePlan.stage);
    return stages[stages.indexOf(stage) + 1];
  }

  private async runStage(buildId: string, stage: Stage, feedback?: string, only?: ReadonlySet<string>): Promise<void> {
    let build = this.get(buildId);
    const root = build.rootSessionId as string;
    const signal = this.deps.sessions.signal(root);
    if (signal.aborted || build.status === "cancelled") return;
    const context = this.context(buildId);
    const stagePlan = context.blueprint.stages.find((item) => item.stage === stage);
    if (!stagePlan) throw new OrchestraError("internal", `The Blueprint has no ${stage} stage`);

    const { loaded } = loadReceipts(buildId, this.deps.paths);
    const predecessors = STAGE_ORDER.slice(0, STAGE_ORDER.indexOf(stage));
    const unsatisfied = predecessors.filter((earlier) => !stageSatisfied(earlier, loaded, this.gateOptions(build)));
    if (unsatisfied.length > 0) {
      throw new OrchestraError("gated", `${STAGE_LABELS[stage]} is gated: ${unsatisfied.join(", ")} has no passing receipt`);
    }

    const layers = build.layers.filter((layer) => layer.stage === stage && (!only || only.has(layer.key)));
    const sprint: Sprint = {
      id: newId("sprint"),
      buildId,
      index: build.sprints.length + 1,
      stage,
      layerIds: layers.map((layer) => layer.id),
      status: "running",
      startedAt: now(),
    };
    const { openCheckpointId: _closed, ...rest } = build;
    build = this.save({
      ...rest,
      status: "running",
      sprints: [...build.sprints, sprint],
      currentSprintId: sprint.id,
      stages: build.stages.map((state) => (state.stage === stage ? { ...state, status: "running", sprintId: sprint.id } : state)),
    });
    this.emitSprint(build, sprint);
    this.emitBuild(build);
    this.deps.sessions.update(root, {
      status: "running",
      summary: `${STAGE_LABELS[stage]} sprint: ${layers.length} layer${layers.length === 1 ? "" : "s"} in parallel`,
    });
    for (const layer of layers) {
      this.patchLayer(buildId, layer.id, { status: "pending", error: undefined, finishedAt: undefined });
    }

    const waves = new Map<number, Layer[]>();
    for (const layer of layers) {
      const wave = context.waves.get(layer.key) ?? 0;
      waves.set(wave, [...(waves.get(wave) ?? []), layer]);
    }
    const failed = new Set<string>();
    for (const wave of [...waves.keys()].sort((left, right) => left - right)) {
      if (signal.aborted) return;
      await Promise.all(
        (waves.get(wave) ?? []).map(async (layer) => {
          const plan = stagePlan.layers.find((item) => item.key === layer.key) as LayerPlan;
          const blockedBy = plan.dependsOn.filter((key) => failed.has(key));
          if (blockedBy.length > 0) {
            failed.add(layer.key);
            this.patchLayer(buildId, layer.id, {
              status: "blocked",
              error: `Waits for ${blockedBy.join(", ")}, which did not pass`,
            });
            return;
          }
          const passed = await this.runLayer(buildId, layer.id, feedback);
          if (!passed) failed.add(layer.key);
        }),
      );
    }
    if (signal.aborted || this.get(buildId).status === "cancelled") return;
    await this.openCheckpoint(buildId, stage, sprint.id);
  }

  /** Run one layer in its own child session, with retries. Returns whether it passed. */
  private async runLayer(buildId: string, layerId: string, feedback?: string): Promise<boolean> {
    const build = this.get(buildId);
    const context = this.context(buildId);
    const layer = build.layers.find((item) => item.id === layerId) as Layer;
    const stagePlan = context.blueprint.stages.find((item) => item.stage === layer.stage) as StagePlan;
    const plan = stagePlan.layers.find((item) => item.key === layer.key) as LayerPlan;
    const session = this.deps.sessions.create({
      agent: layer.agent,
      kind: "layer",
      parentId: build.rootSessionId,
      task: `${plan.title}: ${plan.description}`,
      buildId,
      subject: { type: "layer", id: layer.id, label: plan.title },
      status: "running",
      summary: "Reading the build",
    });
    this.patchLayer(buildId, layerId, {
      status: "running",
      sessionId: session.id,
      startedAt: now(),
      error: undefined,
      finishedAt: undefined,
    });
    const signal = this.deps.sessions.signal(session.id);
    const base = feedback ? [`The person asked at the checkpoint: ${feedback}`] : [];
    let notes = [...base];
    let outcome: LayerOutcome | undefined;
    let lastError: unknown;
    let fatal = false;
    for (let attempt = 1; attempt <= this.deps.maxAttempts && !fatal; attempt += 1) {
      if (signal.aborted) break;
      this.deps.sessions.update(session.id, {
        summary: attempt === 1 ? "Writing files" : `Try ${attempt} of ${this.deps.maxAttempts}: fixing ${notes.length - base.length} problem(s)`,
      });
      try {
        outcome = await runLayerAgent(this.deps.models, {
          blueprint: context.blueprint,
          seed: context.seed,
          stagePlan,
          plan,
          paths: this.deps.paths.build(buildId),
          command: context.commands.get(plan.key),
          feedback: notes,
          attempt,
          signal,
        });
        if (outcome.model) this.deps.sessions.update(session.id, { model: outcome.model });
        if (plan.key === SANDBOX_LAUNCH_KEY && outcome.files.length > 0 && outcome.problems.length === 0) {
          const launched = await this.sandboxTurn(buildId, () => this.launchCheck(buildId, session.id, outcome as LayerOutcome, signal));
          outcome = launched.outcome;
          fatal = launched.fatal;
        }
        if (plan.kind === "test-flight" && outcome.files.length > 0) {
          const current = outcome;
          const flown = await this.sandboxTurn(buildId, () => this.testFlight(buildId, layerId, session.id, current, signal));
          outcome = flown.outcome;
          fatal = flown.fatal;
        }
        lastError = undefined;
        if (outcome.problems.length === 0) break;
        this.deps.sessions.log(session.id, "warn", `Try ${attempt} found ${outcome.problems.length} problem(s): ${clip(outcome.problems.join(" "), 600)}`);
        notes = [...base, ...outcome.problems];
      } catch (error) {
        if (isAbort(error, signal)) break;
        lastError = error;
        this.deps.sessions.log(session.id, "warn", `Try ${attempt} failed: ${messageOf(error)}`);
        if (isOrchestraError(error, "model_unavailable")) break;
      }
    }

    if (signal.aborted) {
      this.patchLayer(buildId, layerId, { status: "cancelled", finishedAt: now() });
      return false;
    }

    let receipt: Receipt | undefined;
    let problems = outcome ? [...outcome.problems] : [];
    if (lastError) problems.push(messageOf(lastError));
    if (outcome && outcome.files.length > 0) {
      receipt = {
        id: newId("receipt"),
        buildId,
        stage: layer.stage,
        layerId,
        agentId: layer.agent,
        artifacts: [...new Set(outcome.files)].slice(0, 200),
        checks: outcome.checks.slice(0, 80),
        recordedAt: now(),
      };
      recordReceipt(receipt, this.deps.paths);
      const broken = loadReceipts(buildId, this.deps.paths).broken.find((line) => line.includes(receipt?.id ?? "?"));
      if (broken) problems.push(`The receipt did not verify: ${broken}`);
      this.deps.events.publish({ type: "receipt.recorded", buildId, sessionId: session.id, data: { receipt } });
    } else if (!lastError) {
      problems.push("The layer wrote no files, so there is nothing to receipt.");
    }
    const passed = !!receipt && problems.length === 0 && receiptSatisfies(receipt);
    const previews = this.writeLayerPreview(buildId, layer, plan, outcome, receipt, problems);
    const summary = outcome?.summary ?? `${agentName(layer.agent)} could not finish ${plan.title}.`;
    this.patchLayer(buildId, layerId, {
      status: passed ? "passed" : "failed",
      finishedAt: now(),
      previews,
      receiptId: receipt?.id,
      error: passed ? undefined : clip(problems.join(" ") || "The layer did not pass its checks", 2000),
    });
    const checkLines = (receipt?.checks ?? []).map((check) => `${check.passed ? "passed" : "failed"}: ${check.name}`);
    this.deps.sessions.addMessage(session.id, {
      role: "assistant",
      agent: layer.agent,
      content: [
        summary,
        outcome?.files.length ? `Files: ${outcome.files.join(", ")}` : "",
        checkLines.length ? `Checks:\n${checkLines.join("\n")}` : "",
        passed ? "" : `Open problems: ${clip(problems.join(" "), 1500)}`,
      ]
        .filter(Boolean)
        .join("\n\n"),
      ...(outcome?.usage ? { usage: outcome.usage } : {}),
    });
    this.deps.sessions.update(session.id, {
      status: passed ? "done" : "failed",
      summary: passed ? `Passed: ${outcome?.files.length ?? 0} file(s), receipt ${receipt?.id}` : "Did not pass its checks",
      ...(passed ? {} : { error: clip(problems.join(" ") || "failed", 2000) }),
    });
    return passed;
  }

  /** Run sandbox work for a build after any earlier sandbox work for it has finished. */
  private sandboxTurn<T>(buildId: string, work: () => Promise<T>): Promise<T> {
    const previous = this.sandboxTurns.get(buildId) ?? Promise.resolve();
    const next = previous.catch(() => undefined).then(work);
    this.sandboxTurns.set(buildId, next.catch(() => undefined));
    return next;
  }

  /**
   * Start the sandbox (or take this build's open run) and wait until it is
   * running or has failed. `fresh` restarts an open run first, so the sandbox
   * builds the app's current files rather than the ones it started with.
   */
  private async sandboxUp(buildId: string, sessionId: string, signal: AbortSignal, fresh = false): Promise<SandboxRun> {
    this.deps.sessions.update(sessionId, { summary: fresh ? "Rebuilding the sandbox with the new files" : "Starting the sandbox" });
    let run = await this.deps.sandbox.start(buildId);
    if (fresh && run.status !== "failed" && run.status !== "stopped") {
      await this.deps.sandbox.stop(run.id);
      run = await this.deps.sandbox.start(buildId);
    }
    const deadline = Date.now() + this.deps.testFlightTimeoutMs;
    while (run.status === "starting" && Date.now() < deadline) {
      await sleep(2000, signal);
      run = await this.deps.sandbox.getRun(run.id);
    }
    if (run.status === "starting") {
      return { ...run, status: "failed", failure: { code: "app_unhealthy", message: "The sandbox took too long to start." } };
    }
    return run;
  }

  /**
   * Icarus's sandbox launch layer is checked for real: the app is installed,
   * built and started in the sandbox. A build or start error goes back to
   * the layer as a problem to fix, with the app's own error lines.
   */
  private async launchCheck(
    buildId: string,
    sessionId: string,
    outcome: LayerOutcome,
    signal: AbortSignal,
  ): Promise<{ outcome: LayerOutcome; fatal: boolean }> {
    const name = "The app builds and starts in the sandbox";
    try {
      const run = await this.sandboxUp(buildId, sessionId, signal);
      if (run.status === "running") {
        return {
          outcome: { ...outcome, checks: [...outcome.checks, { name, passed: true, detail: `Sandbox run ${run.id} is running${run.appUrl ? ` at ${run.appUrl}` : ""}` }] },
          fatal: false,
        };
      }
      const detail = run.failure?.message ?? `The sandbox run ended ${run.status}`;
      const fatal = run.failure?.code === "docker_unavailable" || run.failure?.code === "port_taken";
      return {
        outcome: {
          ...outcome,
          checks: [...outcome.checks, { name, passed: false, detail: clip(detail, 1000) }],
          problems: [...outcome.problems, `${name} failed. Fix the app files so it builds: ${clip(detail, 1500)}`],
        },
        fatal,
      };
    } catch (error) {
      if (isAbort(error, signal)) throw error;
      const detail = error instanceof LayerNotConnected ? "The sandbox is not connected yet." : messageOf(error);
      return {
        outcome: { ...outcome, checks: [...outcome.checks, { name, passed: false, detail: clip(detail, 1000) }], problems: [...outcome.problems, `${name} could not be checked: ${detail}`] },
        fatal: error instanceof LayerNotConnected || isOrchestraError(error, "docker_unavailable"),
      };
    }
  }

  /** Icarus's test flight: bring the build up in the sandbox and let the bots play staff and customer. */
  private async testFlight(
    buildId: string,
    layerId: string,
    sessionId: string,
    outcome: LayerOutcome,
    signal: AbortSignal,
  ): Promise<{ outcome: LayerOutcome; fatal: boolean }> {
    const folders = this.deps.paths.build(buildId);
    const fail = (detail: string, fatal: boolean) => ({
      outcome: {
        ...outcome,
        checks: [...outcome.checks, { name: "Test flight", passed: false, detail: clip(detail, 1000) }],
        problems: [...outcome.problems, detail],
      },
      fatal,
    });
    this.patchLayer(buildId, layerId, { testFlight: "running" });
    try {
      // A test-flight layer that changed the app's own files flies a sandbox rebuilt from them.
      const changedApp = outcome.files.some((file) => !file.startsWith(".4ge/") && !/^(?:tests?|e2e)\//.test(file));
      const run = await this.sandboxUp(buildId, sessionId, signal, changedApp);
      if (run.status !== "running") {
        this.patchLayer(buildId, layerId, { testFlight: "failed" });
        return fail(run.failure?.message ?? "The sandbox did not start", run.failure?.code === "docker_unavailable" || run.failure?.code === "port_taken");
      }
      this.deps.sessions.update(sessionId, { summary: "Bots are flying the staff and customer test" });
      let flight = await this.deps.sandbox.startTestFlight({ buildId, runId: run.id });
      const deadline = Date.now() + this.deps.testFlightTimeoutMs;
      while ((flight.status === "queued" || flight.status === "running") && Date.now() < deadline) {
        await sleep(3000, signal);
        flight = await this.deps.sandbox.getTestFlight(flight.id);
      }
      const evidence = `.4ge/test-flights/${flight.id}.json`;
      writeJson(join(folders.app, ".4ge", "test-flights", `${flight.id}.json`), flight);
      const build = this.get(buildId);
      for (const verdict of flight.layers) {
        if (build.layers.some((layer) => layer.id === verdict.layerId) && verdict.status !== "not-covered") {
          this.patchLayer(buildId, verdict.layerId, { testFlight: verdict.status });
        }
      }
      const passedSteps = flight.steps.filter((step) => step.status === "passed").length;
      const ok = flight.status === "passed";
      this.patchLayer(buildId, layerId, { testFlight: ok ? "passed" : "failed" });
      const detail = flight.summary ?? `${passedSteps} of ${flight.steps.length} steps passed (${flight.status})`;
      return {
        outcome: {
          ...outcome,
          files: [...outcome.files, evidence],
          checks: [...outcome.checks, { name: "Test flight", passed: ok, detail: clip(detail, 1000) }],
          problems: ok ? outcome.problems : [...outcome.problems, `The test flight did not pass: ${detail}`],
        },
        fatal: false,
      };
    } catch (error) {
      if (isAbort(error, signal)) throw error;
      this.patchLayer(buildId, layerId, { testFlight: "failed" });
      if (error instanceof LayerNotConnected) {
        return fail("The sandbox is not connected yet, so Icarus could not fly the test.", true);
      }
      return fail(`The test flight could not run: ${messageOf(error)}`, isOrchestraError(error, "docker_unavailable"));
    }
  }

  private writeLayerPreview(
    buildId: string,
    layer: Layer,
    plan: LayerPlan,
    outcome: LayerOutcome | undefined,
    receipt: Receipt | undefined,
    problems: string[],
  ): Preview[] {
    const folders = this.deps.paths.build(buildId);
    const build = this.get(buildId);
    const at = now();
    const report = [
      `# ${plan.title}`,
      "",
      `${agentName(layer.agent)}, ${STAGE_LABELS[layer.stage]} stage`,
      "",
      outcome?.summary ?? "No summary.",
      "",
      "## Files",
      "",
      ...(outcome?.files.length ? outcome.files.map((file) => `- app/${file}`) : ["- none"]),
      "",
      "## Checks",
      "",
      ...(receipt?.checks.map((check) => `- ${check.passed ? "passed" : "failed"}: ${check.name}${check.detail ? `. ${check.detail}` : ""}`) ?? ["- no receipt"]),
      ...(problems.length ? ["", "## Open problems", "", ...problems.map((problem) => `- ${problem}`)] : []),
      ...(receipt ? ["", `Receipt: ${receipt.id}`] : []),
    ].join("\n");
    mkdirSync(folders.previews, { recursive: true });
    writeFileSync(join(folders.previews, `${layer.key}.md`), `${plainText(report)}\n`, "utf8");
    const previews: Preview[] = [
      { kind: "text", title: `${plan.title} report`, ref: `previews/${layer.key}.md`, stage: layer.stage, layerId: layer.id, updatedAt: at },
    ];
    for (const file of (outcome?.files ?? []).filter((item) => /\.html?$/i.test(item)).slice(0, 6)) {
      previews.push({ kind: "html", title: file, ref: `app/${file}`, stage: layer.stage, layerId: layer.id, updatedAt: at });
    }
    for (const preview of previews) this.emitPreview(build, preview, layer.sessionId);
    return previews;
  }

  private async openCheckpoint(buildId: string, stage: Stage, sprintId: string): Promise<void> {
    let build = this.get(buildId);
    const root = build.rootSessionId as string;
    const stagePlan = this.context(buildId).blueprint.stages.find((item) => item.stage === stage) as StagePlan;
    const layers = build.layers.filter((layer) => layer.stage === stage);
    const allPassed = layers.every((layer) => layer.status === "passed");
    const failedLayers = layers.filter((layer) => layer.status !== "passed");
    // The card names what did not pass in plain words whatever the model's summary says.
    const notPassing =
      failedLayers.length > 0
        ? `Not passing yet: ${failedLayers.map((layer) => `${layer.title} (${layer.error ? clip(layer.error, 160) : layer.status})`).join("; ")}. Choose revise with a note and I will rebuild them.`
        : "";
    const modelSummary = await this.summarize(build, stage, layers);
    const summary = notPassing ? `${clip(modelSummary, 3990 - notPassing.length)}

${notPassing}` : modelSummary;
    const checkpoint = this.saveCheckpoint({
      id: newId("checkpoint"),
      buildId,
      sprintId,
      stage,
      title: stagePlan.checkpoint.title,
      summary,
      question: stagePlan.checkpoint.question,
      previews: layers.flatMap((layer) => layer.previews).slice(0, 60),
      status: "open",
      createdAt: now(),
    });
    build = this.get(buildId);
    if (build.status === "cancelled") return;
    const sprints = build.sprints.map((sprint) =>
      sprint.id === sprintId
        ? { ...sprint, status: allPassed ? ("passed" as const) : ("failed" as const), finishedAt: now(), checkpointId: checkpoint.id }
        : sprint,
    );
    build = this.save({
      ...build,
      status: "awaiting-checkpoint",
      sprints,
      openCheckpointId: checkpoint.id,
      stages: build.stages.map((state) => (state.stage === stage ? { ...state, status: allPassed ? "passed" : "failed" } : state)),
    });
    const sprint = build.sprints.find((item) => item.id === sprintId) as Sprint;
    this.emitSprint(build, sprint);
    this.deps.events.publish({ type: "checkpoint.opened", buildId, sessionId: root, data: { checkpoint } });
    this.emitBuild(build);
    this.deps.sessions.addMessage(root, {
      role: "assistant",
      agent: "smith",
      content: [
        `${STAGE_LABELS[stage]} sprint done. ${modelSummary}`,
        notPassing,
        checkpoint.question,
      ]
        .filter(Boolean)
        .join("\n\n"),
    });
    this.deps.sessions.update(root, { status: "waiting", summary: `Checkpoint: ${checkpoint.title}` });
  }

  private async summarize(build: Build, stage: Stage, layers: Layer[]): Promise<string> {
    const facts = layers.map((layer) => {
      const last = layer.sessionId
        ? this.deps.sessions.messages(layer.sessionId).filter((message) => message.role === "assistant").at(-1)?.content
        : undefined;
      return { title: layer.title, agent: layer.agent, status: layer.status, error: layer.error, report: clip(last ?? "", 1200) };
    });
    const fallback = `${layers.filter((layer) => layer.status === "passed").length} of ${layers.length} layers passed with receipts: ${layers.map((layer) => `${layer.title} ${layer.status}`).join(", ")}.`;
    try {
      const result = await this.deps.models.chat({
        tier: "lead",
        purpose: "checkpoint.summary",
        messages: [
          {
            role: "system",
            content: `${AGENT_DEFINITIONS.smith.system}\n\nWrite the checkpoint summary for the ${STAGE_LABELS[stage]} sprint: three to five plain sentences, summary first, naming what was built and what did not pass. Only state what the layer reports say. No headings, no lists.`,
          },
          { role: "user", content: JSON.stringify(facts, null, 1) },
        ],
        maxTokens: 700,
        temperature: 0.3,
        signal: this.deps.sessions.signal(build.rootSessionId as string),
      });
      const text = plainText(result.text).trim();
      return clip(text || fallback, 4000);
    } catch (error) {
      if (isAbort(error)) throw error;
      return fallback;
    }
  }

  /* ------------------------------ decisions ------------------------------ */

  async decide(buildId: string, checkpointId: string, decision: CheckpointDecision): Promise<{ checkpoint: Checkpoint; build: Build }> {
    const build = this.get(buildId);
    const checkpoint = this.checkpoint(buildId, checkpointId);
    if (checkpoint.status !== "open") throw new OrchestraError("conflict", "This checkpoint was already answered");
    if (build.status === "cancelled") throw new OrchestraError("conflict", "This build was cancelled");
    if (build.openCheckpointId !== checkpointId) throw new OrchestraError("conflict", "This checkpoint is not the one the build is waiting on");
    const root = build.rootSessionId as string;
    const note = decision.note?.trim();
    if (decision.decision === "revise" && !note) {
      throw new OrchestraError("bad_request", "Say what you want changed", "Write a note for Smith with the revise decision.");
    }
    const stage = checkpoint.stage;
    if (decision.decision !== "revise") {
      const { loaded } = loadReceipts(buildId, this.deps.paths);
      if (!stageSatisfied(stage, loaded, this.gateOptions(build))) {
        throw new OrchestraError(
          "gated",
          `The ${STAGE_LABELS[stage]} stage has layers without a passing receipt, so the next stage cannot start.`,
          "Choose revise with a note to rebuild the layers that did not pass, or cancel the build.",
        );
      }
    }
    const resolved = this.saveCheckpoint({
      ...checkpoint,
      status: decision.decision === "approve" ? "approved" : decision.decision === "skip" ? "skipped" : "revise",
      decision: { decision: decision.decision, ...(note ? { note } : {}), decidedAt: now() },
    });
    this.deps.events.publish({ type: "checkpoint.resolved", buildId, sessionId: root, data: { checkpoint: resolved } });
    this.deps.sessions.revive(root);
    if (note) this.deps.sessions.addMessage(root, { role: "user", content: note });

    const { openCheckpointId: _open, ...rest } = this.get(buildId);
    if (decision.decision === "revise") {
      const saved = this.save({ ...rest, status: "running" });
      this.emitBuild(saved);
      this.deps.sessions.addMessage(root, {
        role: "assistant",
        agent: "smith",
        content: `Rebuilding the ${STAGE_LABELS[stage]} layers with your note.`,
      });
      this.track(buildId, this.runStage(buildId, stage, note));
      return { checkpoint: resolved, build: saved };
    }
    const next = this.nextStage(buildId, stage);
    if (next) {
      const saved = this.save({ ...rest, status: "running" });
      this.emitBuild(saved);
      this.deps.sessions.addMessage(root, {
        role: "assistant",
        agent: "smith",
        content: `${STAGE_LABELS[stage]} ${decision.decision === "approve" ? "approved" : "accepted as is"}. The gate admits ${STAGE_LABELS[next]}; starting that sprint.`,
      });
      this.track(buildId, this.runStage(buildId, next));
      return { checkpoint: resolved, build: saved };
    }
    const finished = this.save({ ...rest, status: "passed", finishedAt: now() });
    this.emitBuild(finished);
    this.deps.seeds.update(finished.seedId, { status: "built" });
    this.deps.sessions.addMessage(root, {
      role: "assistant",
      agent: "smith",
      content: `Every stage of ${finished.title} passed with receipts. The app is in your build folder and joins your Apps in the Library.`,
    });
    this.deps.sessions.update(root, { status: "done", summary: "Build passed" });
    return { checkpoint: resolved, build: finished };
  }

  /** Smith rebuilds one layer of the open checkpoint's stage with a note (a thread sent to Smith). */
  async rebuildLayer(buildId: string, layerKey: string, note: string): Promise<Layer> {
    const build = this.get(buildId);
    const layer = build.layers.find((item) => item.key === layerKey || item.id === layerKey);
    if (!layer) throw notFound(`Layer ${layerKey}`);
    if (build.status !== "awaiting-checkpoint" && build.status !== "failed") {
      throw new OrchestraError("conflict", "Layers can be rebuilt while the build waits at a checkpoint");
    }
    const open = build.openCheckpointId ? this.checkpoint(buildId, build.openCheckpointId) : undefined;
    if (open && open.stage !== layer.stage) {
      throw new OrchestraError("gated", `${layer.title} belongs to the ${STAGE_LABELS[layer.stage]} stage, not the one at the checkpoint`);
    }
    this.deps.sessions.revive(build.rootSessionId as string);
    const saved = this.save({ ...build, status: "running" });
    this.emitBuild(saved);
    const work = (async () => {
      await this.runLayer(buildId, layer.id, note);
      const after = this.get(buildId);
      if (after.status === "cancelled") return;
      if (open) {
        const layers = after.layers.filter((item) => item.stage === open.stage);
        const refreshed = this.saveCheckpoint({ ...open, previews: layers.flatMap((item) => item.previews).slice(0, 60) });
        this.deps.events.publish({ type: "checkpoint.opened", buildId, sessionId: after.rootSessionId as string, data: { checkpoint: refreshed } });
        const allPassed = layers.every((item) => item.status === "passed");
        const back = this.save({
          ...after,
          status: "awaiting-checkpoint",
          stages: after.stages.map((state) => (state.stage === open.stage ? { ...state, status: allPassed ? "passed" : "failed" } : state)),
        });
        this.emitBuild(back);
      } else {
        const back = this.save({ ...after, status: "failed" });
        this.emitBuild(back);
      }
      const rebuilt = this.get(buildId).layers.find((item) => item.id === layer.id) as Layer;
      this.deps.sessions.addMessage(after.rootSessionId as string, {
        role: "assistant",
        agent: "smith",
        content: `${rebuilt.title} ${rebuilt.status === "passed" ? "is rebuilt and passed its checks" : `was rebuilt but did not pass: ${clip(rebuilt.error ?? "", 300)}`}.`,
      });
    })();
    this.track(buildId, work);
    return layer;
  }

  cancel(buildId: string): Build {
    const build = this.get(buildId);
    if (build.status === "cancelled" || build.status === "passed") return build;
    if (build.rootSessionId) this.deps.sessions.cancel(build.rootSessionId, "The build was cancelled");
    const at = now();
    const saved = this.save({
      ...build,
      status: "cancelled",
      finishedAt: at,
      layers: build.layers.map((layer) =>
        layer.status === "running" || layer.status === "pending" || layer.status === "blocked"
          ? { ...layer, status: "cancelled", finishedAt: at }
          : layer,
      ),
      sprints: build.sprints.map((sprint) => (sprint.status === "running" ? { ...sprint, status: "cancelled", finishedAt: at } : sprint)),
      stages: build.stages.map((state) => (state.status === "running" || state.status === "pending" ? { ...state, status: "cancelled" } : state)),
    });
    for (const layer of saved.layers.filter((item) => item.status === "cancelled")) {
      this.deps.events.publish({ type: "layer.updated", buildId, sessionId: layer.sessionId ?? (saved.rootSessionId as string), data: { layer } });
    }
    this.emitBuild(saved);
    try {
      this.deps.seeds.update(saved.seedId, { status: "ready" });
    } catch {
      // The Seed may have been removed; the build is cancelled either way.
    }
    if (saved.rootSessionId) {
      this.deps.sessions.addMessage(saved.rootSessionId, {
        role: "assistant",
        agent: "smith",
        content: "The build is cancelled. Every running layer stopped; files already written stay in the build folder.",
      });
    }
    return saved;
  }

  /** Stop every running build (server shutdown). */
  async close(): Promise<void> {
    for (const build of this.list()) {
      if (ACTIVE.has(build.status) && build.rootSessionId) this.deps.sessions.cancel(build.rootSessionId, "The 4GE server stopped");
    }
    await Promise.allSettled([...this.tasks.values()].flatMap((set) => [...set]));
  }

  /** The build folder exists (for links in the UI). */
  folderExists(buildId: string): boolean {
    return existsSync(this.deps.paths.build(buildId).root);
  }
}
