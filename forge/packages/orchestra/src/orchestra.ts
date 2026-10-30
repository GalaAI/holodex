import { ainboxQueueDir, type DataPaths } from "@4ge/contracts/node";
import type { Build, Seed, SeedDraftInput } from "@4ge/contracts";
import { BuildRunner } from "./builds.js";
import { Conversations } from "./chat.js";
import { runDesignConversation } from "./design.js";
import { OrchestraError } from "./errors.js";
import { Imagistation } from "./imagistation.js";
import { createModelClient, type ModelClient } from "./model.js";
import { LayerNotConnected, type AtlasPort, type DesignPort, type SandboxPort } from "./ports.js";
import { SeedStore, queueSeedInAinbox } from "./seeds.js";
import { SessionManager } from "./sessions.js";
import { startersDir as defaultStartersDir } from "@4ge/atlas";
import type { EventSink } from "./util.js";

/**
 * The orchestra, assembled: one object the server's orchestra routes call.
 * Everything it touches lives under dataRoot(): sessions/, seeds/,
 * builds/<id>/ (app, receipts, checkpoints, previews, design, bamf).
 */
export interface OrchestraOptions {
  paths: DataPaths;
  env: Readonly<Record<string, string | undefined>>;
  events: EventSink;
  atlas: AtlasPort;
  design?: DesignPort;
  sandbox?: SandboxPort;
  /** Replace the model client (tests). Defaults to the SPEC route order over env. */
  models?: ModelClient;
  startersDir?: string;
  ainboxDir?: string;
  maxAttempts?: number;
  testFlightTimeoutMs?: number;
}

const disconnected = (layer: string) => async (): Promise<never> => {
  throw new LayerNotConnected(layer, `The ${layer} layer is not connected yet`);
};

export class Orchestra {
  readonly sessions: SessionManager;
  readonly seeds: SeedStore;
  readonly models: ModelClient;
  readonly imagistation: Imagistation;
  readonly builds: BuildRunner;
  readonly conversations: Conversations;
  private readonly ainboxDir: string;
  private readonly background = new Set<Promise<unknown>>();

  constructor(options: OrchestraOptions) {
    const { paths, events } = options;
    this.models = options.models ?? createModelClient(options.env);
    this.sessions = new SessionManager(paths.sessions, events);
    this.seeds = new SeedStore(paths.seeds, events);
    this.ainboxDir = options.ainboxDir ?? ainboxQueueDir(options.env as Record<string, string | undefined>);
    const startersDir = options.startersDir ?? defaultStartersDir(options.env);
    const design: DesignPort = options.design ?? { saveSystem: disconnected("design"), saveInventoryItem: disconnected("design") };
    const sandbox: SandboxPort = options.sandbox ?? {
      start: disconnected("sandbox"),
      getRun: disconnected("sandbox"),
      stop: disconnected("sandbox"),
      startTestFlight: disconnected("sandbox"),
      getTestFlight: disconnected("sandbox"),
    };
    this.imagistation = new Imagistation({
      sessions: this.sessions,
      seeds: this.seeds,
      models: this.models,
      atlas: options.atlas,
      sessionsDir: paths.sessions,
      startersDir,
    });
    this.builds = new BuildRunner({
      paths,
      sessions: this.sessions,
      seeds: this.seeds,
      models: this.models,
      events,
      atlas: options.atlas,
      sandbox,
      startersDir,
      maxAttempts: options.maxAttempts ?? 3,
      testFlightTimeoutMs: options.testFlightTimeoutMs ?? 15 * 60 * 1000,
      onBuildStarted: (build) =>
        runDesignConversation(
          { paths, sessions: this.sessions, models: this.models, events, design },
          build,
          this.builds.seedOf(build.id),
          this.builds.blueprintOf(build.id),
        ),
    });
    this.conversations = new Conversations({
      paths,
      sessions: this.sessions,
      models: this.models,
      builds: this.builds,
      imagistation: this.imagistation,
    });
  }

  /** Keep a promise alive and visible to close(); its errors are reported where it runs. */
  run(work: Promise<unknown>): void {
    const wrapped = work.catch(() => undefined).finally(() => this.background.delete(wrapped));
    this.background.add(wrapped);
  }

  createSeed(draft: SeedDraftInput): Seed {
    return this.seeds.create(draft);
  }

  /** Build now (starts a build) or later (queues the Seed in AInbox with a reminder). */
  async decideSeed(seedId: string, when: "now" | "later", reminderNote?: string): Promise<{ seed: Seed; build?: Build }> {
    const seed = this.seeds.get(seedId);
    if (seed.status === "building" && seed.buildId && this.builds.has(seed.buildId)) {
      const current = this.builds.get(seed.buildId);
      if (["queued", "running", "awaiting-checkpoint", "test-flight"].includes(current.status)) {
        throw new OrchestraError("conflict", "This Seed is already building", `Open build ${current.id}.`);
      }
    }
    if (when === "later") {
      const ainboxItem = queueSeedInAinbox(this.ainboxDir, seed, reminderNote);
      const note = reminderNote?.trim();
      const queued = this.seeds.update(seed.id, {
        decision: "later",
        status: "queued",
        ainboxItem,
        ...(note ? { reminderNote: note } : {}),
      });
      this.imagistation.noteDecision(
        queued,
        `Queued for later in your AInbox with a reminder to time-block it${note ? `: ${note}` : ""}. Open it from AInbox or the Library when you are ready.`,
      );
      return { seed: queued };
    }
    const build = await this.builds.create({ seedId });
    const started = this.seeds.get(seedId);
    this.imagistation.noteDecision(started, `Building now. Smith has the Seed in the 4GE IDE (build ${build.id}).`);
    return { seed: started, build };
  }

  async close(): Promise<void> {
    await this.builds.close();
    await Promise.allSettled([...this.background]);
  }
}
