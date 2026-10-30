import { mkdir, readFile, readdir, rename, writeFile } from "node:fs/promises";
import { join } from "node:path";
import {
  atlasEntrySchema,
  blueprintIdSchema,
  blueprintSchema,
  type AtlasEntry,
  type Blueprint,
} from "@4ge/contracts";
import { matchBlueprints, type MatchOutcome, type MatchQuery } from "./match.js";
import { STARTER_BLUEPRINTS } from "./starters/index.js";

/**
 * The user's Atlas: one JSON file per Blueprint under
 * dataRoot()/atlas/blueprints/<slug>.json, each an AtlasEntry. It starts
 * with the starter Blueprints (refreshed when 4GE ships a newer version,
 * keeping the use count) and holds every Blueprint the user crafted or
 * imported. Starters are read-only; a user Blueprint is replaced only by a
 * higher version. Adapted from GalaAI/ark packages/atlas-core/src/library.ts
 * (the Atlas read model), with persistence added because 4GE owns the
 * user's Atlas on their own machine.
 */
export type AtlasErrorCode = "not_found" | "conflict" | "bad_request";

export class AtlasError extends Error {
  readonly code: AtlasErrorCode;
  readonly hint: string | undefined;
  constructor(code: AtlasErrorCode, message: string, hint?: string) {
    super(message);
    this.name = "AtlasError";
    this.code = code;
    this.hint = hint;
  }
}

export interface AtlasProblem {
  file: string;
  message: string;
}

export interface AtlasRegistryOptions {
  starters?: readonly Blueprint[];
  now?: () => Date;
}

export function compareVersions(left: string, right: string): number {
  const a = left.split(".").map(Number);
  const b = right.split(".").map(Number);
  for (let index = 0; index < 3; index += 1) {
    const difference = (a[index] ?? 0) - (b[index] ?? 0);
    if (difference !== 0) return Math.sign(difference);
  }
  return 0;
}

/** Accept "blueprint:website", "website" or the URL-encoded id. */
export function normalizeBlueprintId(raw: string): string {
  let text = raw.trim();
  try {
    text = decodeURIComponent(text);
  } catch {
    // keep the raw text; validation below reports it
  }
  const id = text.startsWith("blueprint:") ? text : `blueprint:${text}`;
  const checked = blueprintIdSchema.safeParse(id);
  if (!checked.success) throw new AtlasError("bad_request", `${raw} is not a Blueprint id`, "Blueprint ids look like blueprint:<slug>");
  return checked.data;
}

export class AtlasRegistry {
  readonly dir: string;
  private readonly starters: readonly Blueprint[];
  private readonly now: () => Date;
  private ready: Promise<void> | undefined;

  constructor(dir: string, options: AtlasRegistryOptions = {}) {
    this.dir = dir;
    this.starters = options.starters ?? STARTER_BLUEPRINTS;
    this.now = options.now ?? (() => new Date());
  }

  fileFor(blueprintId: string): string {
    return join(this.dir, `${normalizeBlueprintId(blueprintId).slice("blueprint:".length)}.json`);
  }

  private async write(entry: AtlasEntry): Promise<AtlasEntry> {
    const checked = atlasEntrySchema.parse(entry);
    await mkdir(this.dir, { recursive: true });
    const file = this.fileFor(checked.blueprint.blueprintId);
    const temp = `${file}.${process.pid}.${Date.now().toString(36)}.tmp`;
    await writeFile(temp, `${JSON.stringify(checked, null, 2)}\n`, "utf8");
    await rename(temp, file);
    return checked;
  }

  private async read(blueprintId: string): Promise<AtlasEntry | undefined> {
    let text: string;
    try {
      text = await readFile(this.fileFor(blueprintId), "utf8");
    } catch {
      return undefined;
    }
    const parsed = atlasEntrySchema.safeParse(JSON.parse(text));
    return parsed.success ? parsed.data : undefined;
  }

  /** Write missing starters and refresh older ones. Safe to call on every start. */
  async ensureStarters(): Promise<{ added: string[]; updated: string[] }> {
    const added: string[] = [];
    const updated: string[] = [];
    const at = this.now().toISOString();
    for (const blueprint of this.starters) {
      let current: AtlasEntry | undefined;
      try {
        current = await this.read(blueprint.blueprintId);
      } catch {
        current = undefined;
      }
      if (!current || current.source !== "starter") {
        if (current && current.source === "user") continue; // never overwrite the user's own file
        await this.write({ blueprint, source: "starter", registeredAt: at, uses: 0 });
        added.push(blueprint.blueprintId);
      } else if (compareVersions(blueprint.version, current.blueprint.version) > 0) {
        await this.write({ ...current, blueprint });
        updated.push(blueprint.blueprintId);
      }
    }
    return { added, updated };
  }

  private async init(): Promise<void> {
    this.ready ??= this.ensureStarters().then(() => undefined);
    try {
      await this.ready;
    } catch (error) {
      this.ready = undefined;
      throw error;
    }
  }

  /** Every entry plus files that could not be read, for diagnostics. */
  async scan(): Promise<{ entries: AtlasEntry[]; problems: AtlasProblem[] }> {
    await this.init();
    const entries: AtlasEntry[] = [];
    const problems: AtlasProblem[] = [];
    let names: string[] = [];
    try {
      names = (await readdir(this.dir)).filter((name) => name.endsWith(".json"));
    } catch {
      return { entries, problems };
    }
    for (const name of names) {
      try {
        const parsed = atlasEntrySchema.safeParse(JSON.parse(await readFile(join(this.dir, name), "utf8")));
        if (parsed.success) entries.push(parsed.data);
        else problems.push({ file: name, message: parsed.error.issues[0]?.message ?? "not an Atlas entry" });
      } catch (error) {
        problems.push({ file: name, message: error instanceof Error ? error.message : String(error) });
      }
    }
    const starterOrder = new Map(this.starters.map((blueprint, index) => [blueprint.blueprintId, index]));
    entries.sort((left, right) => {
      const a = starterOrder.get(left.blueprint.blueprintId);
      const b = starterOrder.get(right.blueprint.blueprintId);
      if (a !== undefined && b !== undefined) return a - b;
      if (a !== undefined) return -1;
      if (b !== undefined) return 1;
      return left.registeredAt.localeCompare(right.registeredAt) || left.blueprint.blueprintId.localeCompare(right.blueprint.blueprintId);
    });
    return { entries, problems };
  }

  /** Starters first in their shipped order, then the user's Blueprints oldest first. */
  async list(): Promise<AtlasEntry[]> {
    return (await this.scan()).entries;
  }

  async get(blueprintId: string): Promise<AtlasEntry | undefined> {
    await this.init();
    return this.read(normalizeBlueprintId(blueprintId));
  }

  async ids(): Promise<Set<string>> {
    return new Set((await this.list()).map((entry) => entry.blueprint.blueprintId));
  }

  /**
   * Register a crafted or imported Blueprint. A starter id is refused; an
   * existing user Blueprint is replaced only by a higher version, keeping
   * its use count.
   */
  async register(input: unknown): Promise<AtlasEntry> {
    await this.init();
    const parsed = blueprintSchema.safeParse(input);
    if (!parsed.success) {
      const issue = parsed.error.issues[0];
      throw new AtlasError("bad_request", `That Blueprint is not valid: ${issue ? `${issue.path.join(".") || "blueprint"}: ${issue.message}` : "unknown problem"}`);
    }
    const blueprint = parsed.data;
    if (blueprint.origin === "starter") {
      throw new AtlasError("bad_request", "Only 4GE ships starter Blueprints", "Register it with origin crafted or imported");
    }
    if (this.starters.some((starter) => starter.blueprintId === blueprint.blueprintId)) {
      throw new AtlasError("conflict", `${blueprint.blueprintId} is a starter Blueprint`, "Give your Blueprint its own name");
    }
    const at = this.now().toISOString();
    const existing = await this.read(blueprint.blueprintId);
    if (existing) {
      if (compareVersions(blueprint.version, existing.blueprint.version) <= 0) {
        throw new AtlasError(
          "conflict",
          `${blueprint.blueprintId} is already in your Atlas at version ${existing.blueprint.version}`,
          `Raise the version above ${existing.blueprint.version} to replace it, or give it a new name`,
        );
      }
      return this.write({
        ...existing,
        blueprint: { ...blueprint, createdAt: existing.blueprint.createdAt ?? blueprint.createdAt ?? at, updatedAt: at },
      });
    }
    return this.write({
      blueprint: { ...blueprint, createdAt: blueprint.createdAt ?? at, updatedAt: at },
      source: "user",
      registeredAt: at,
      uses: 0,
    });
  }

  /** Count one build started from this Blueprint. */
  async recordUse(blueprintId: string): Promise<AtlasEntry> {
    const entry = await this.get(blueprintId);
    if (!entry) throw new AtlasError("not_found", `${blueprintId} is not in your Atlas`);
    return this.write({ ...entry, uses: entry.uses + 1, lastUsedAt: this.now().toISOString() });
  }

  async match(query: MatchQuery, limit?: number): Promise<MatchOutcome> {
    return matchBlueprints(await this.list(), query, limit);
  }
}
