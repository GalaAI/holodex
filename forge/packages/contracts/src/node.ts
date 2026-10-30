import { mkdirSync } from "node:fs";
import { homedir } from "node:os";
import { join, resolve } from "node:path";
import { buildIdSchema, relativePathSchema } from "./common.js";

/**
 * Data paths (SPEC "Architecture"). 4GE data lives under
 * %LOCALAPPDATA%\Holodex\4ge on Windows and ~/.4ge elsewhere. This is the
 * only place that resolves it; every package asks here.
 *
 * Node only. Import from "@4ge/contracts/node"; the main entry stays
 * browser safe for the studio.
 *
 * FORGE_DATA_ROOT overrides the root (tests, the sandbox, a second profile).
 */
export const DATA_ROOT_ENV = "FORGE_DATA_ROOT";
export const AINBOX_QUEUE_ENV = "FORGE_AINBOX_QUEUE";

type Env = Record<string, string | undefined>;

export function dataRoot(env: Env = process.env, platform: NodeJS.Platform = process.platform): string {
  const override = env[DATA_ROOT_ENV];
  if (override && override.trim().length > 0) return resolve(override);
  if (platform === "win32") {
    const local = env.LOCALAPPDATA && env.LOCALAPPDATA.length > 0
      ? env.LOCALAPPDATA
      : join(homedir(), "AppData", "Local");
    return join(local, "Holodex", "4ge");
  }
  return join(homedir(), ".4ge");
}

/**
 * The AInbox queue the Seed "later" path writes into, in AInbox's own item
 * format (owned by Holodex: %LOCALAPPDATA%\Omniwand\ainbox-queue).
 * The server does not write AInbox items itself: it drops the Seed (seedSchema
 * JSON, with reminderNote) into ainboxSeedDropDir() and Holodex turns it into
 * AInbox's own sealed item (ForgeSeedInbox.cs, HandoffSeal and OutboundQueue).
 */
export function ainboxQueueDir(env: Env = process.env, platform: NodeJS.Platform = process.platform): string {
  const override = env[AINBOX_QUEUE_ENV];
  if (override && override.trim().length > 0) return resolve(override);
  if (platform === "win32") {
    const local = env.LOCALAPPDATA && env.LOCALAPPDATA.length > 0
      ? env.LOCALAPPDATA
      : join(homedir(), "AppData", "Local");
    return join(local, "Omniwand", "ainbox-queue");
  }
  return join(homedir(), ".omniwand", "ainbox-queue");
}

/** Folder name, inside the AInbox queue, where the server drops "later" Seeds for Holodex. */
export const AINBOX_SEED_DROP = "4ge";

/**
 * Where the Seed "later" path writes the Seed: <ainboxQueueDir>\4ge\<seedId>.json,
 * the whole Seed as JSON (seedSchema). Holodex watches this folder, seals the
 * Seed into an AInbox Handoff Scroll with a reminder to time-block the build,
 * queues it for AInbox (or saves it on a PC without AInbox) and moves the drop
 * to 4ge\adopted. Record the file name in Seed.ainboxItem.
 */
export function ainboxSeedDropDir(env: Env = process.env, platform: NodeJS.Platform = process.platform): string {
  return join(ainboxQueueDir(env, platform), AINBOX_SEED_DROP);
}

export interface BuildPaths {
  root: string;
  /** build.json: the Build record. */
  record: string;
  /** The generated app's source; receipts' artifacts are relative to it. */
  app: string;
  receipts: string;
  checkpoints: string;
  previews: string;
  /** Sandbox and test flight evidence (screenshots, logs). */
  evidence: string;
  design: string;
}

export interface DataPaths {
  root: string;
  atlas: string;
  /** atlas/blueprints/<slug>.json, one per registered Blueprint. */
  atlasBlueprints: string;
  seeds: string;
  builds: string;
  build(id: string): BuildPaths;
  mods: string;
  adapters: string;
  augments: string;
  /** sessions/<id>.json plus sessions/<id>.events.jsonl. */
  sessions: string;
  sandbox: string;
  library: string;
  logs: string;
}

export function dataPaths(root: string = dataRoot()): DataPaths {
  const builds = join(root, "builds");
  return {
    root,
    atlas: join(root, "atlas"),
    atlasBlueprints: join(root, "atlas", "blueprints"),
    seeds: join(root, "seeds"),
    builds,
    build(id: string): BuildPaths {
      const checked = buildIdSchema.parse(id);
      const base = join(builds, checked);
      return {
        root: base,
        record: join(base, "build.json"),
        app: join(base, "app"),
        receipts: join(base, "receipts"),
        checkpoints: join(base, "checkpoints"),
        previews: join(base, "previews"),
        evidence: join(base, "evidence"),
        design: join(base, "design"),
      };
    },
    mods: join(root, "mods"),
    adapters: join(root, "mods", "adapters"),
    augments: join(root, "mods", "augments"),
    sessions: join(root, "sessions"),
    sandbox: join(root, "sandbox"),
    library: join(root, "library"),
    logs: join(root, "logs"),
  };
}

/** Create the top-level data folders. Safe to call on every start. */
export function ensureDataDirs(paths: DataPaths = dataPaths()): DataPaths {
  for (const dir of [
    paths.root,
    paths.atlas,
    paths.atlasBlueprints,
    paths.seeds,
    paths.builds,
    paths.mods,
    paths.adapters,
    paths.augments,
    paths.sessions,
    paths.sandbox,
    paths.library,
    paths.logs,
  ]) {
    mkdirSync(dir, { recursive: true });
  }
  return paths;
}

/** Resolve a build-relative path inside a build's app folder, refusing escapes. */
export function resolveInBuild(paths: BuildPaths, relativePath: string): string {
  const checked = relativePathSchema.parse(relativePath);
  return join(paths.app, checked);
}
