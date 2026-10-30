import { cpSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import {
  augmentDraftSchema,
  augmentSchema,
  modSchema,
  type Adapter,
  type Augment,
  type AugmentDraft,
  type Mod,
  type ModDraft,
} from "@4ge/contracts";
import type { DataPaths } from "@4ge/contracts/node";
import { findAdapter } from "./catalog/adapters.js";
import { BUILT_IN_AUGMENTS, type AugmentDefinition } from "./catalog/augments.js";
import { BUILT_IN_MOD_DEFINITIONS, type ModDefinition } from "./catalog/mods.js";
import { adapterCredentialTarget } from "./credentials.js";
import type { AdapterDetector } from "./detect.js";
import {
  MOD_MANIFEST_VERSION,
  checkSkillFolder,
  modManifestSchema,
  toClaudePluginManifest,
  toMcpJson,
  type ModManifest,
} from "./manifest.js";

/**
 * Mods and augments on disk, under dataPaths().mods:
 *
 *   mods/skills/<name>/SKILL.md      shipped and user skills (Agent Skills)
 *   mods/augments/<slug>.json        augments the user added
 *   mods/user/<slug>/mod.json        a mod the user made with the plus tile
 *   mods/user/<slug>/.mcp.json       its MCP servers, for any MCP client
 *   mods/user/<slug>/.claude-plugin/plugin.json and skills/  (Claude Code plugin layout)
 *
 * Built-in mods and augments come from the catalogs; their status is
 * computed from live adapter detection, never stored.
 */

export class ModsError extends Error {
  override readonly name = "ModsError";
  constructor(
    readonly code: "not_found" | "bad_request" | "conflict",
    message: string,
  ) {
    super(message);
  }
}

const SHIPPED_SKILLS = resolve(dirname(fileURLToPath(import.meta.url)), "..", "skills");

function slugify(name: string): string {
  const slug = name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 60);
  return slug || "mod";
}

function readJson(file: string): unknown {
  try {
    return JSON.parse(readFileSync(file, "utf8"));
  } catch {
    return undefined;
  }
}

function writeJson(file: string, value: unknown): void {
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

export class ModService {
  readonly userModsDir: string;
  readonly skillsDir: string;

  constructor(
    private readonly paths: DataPaths,
    private readonly detector: AdapterDetector,
    private readonly now: () => Date = () => new Date(),
  ) {
    this.userModsDir = join(paths.mods, "user");
    this.skillsDir = join(paths.mods, "skills");
    mkdirSync(this.userModsDir, { recursive: true });
    mkdirSync(paths.augments, { recursive: true });
    this.installShippedSkills();
  }

  /** Copy the skills 4GE ships into the data root so skillPath resolves. Ours are refreshed every start. */
  installShippedSkills(): void {
    if (!existsSync(SHIPPED_SKILLS)) return;
    for (const name of readdirSync(SHIPPED_SKILLS)) {
      cpSync(join(SHIPPED_SKILLS, name), join(this.skillsDir, name), { recursive: true, force: true });
    }
  }

  /* -------------------------------- augments ------------------------------- */

  private userAugments(): AugmentDefinition[] {
    if (!existsSync(this.paths.augments)) return [];
    const out: AugmentDefinition[] = [];
    for (const file of readdirSync(this.paths.augments)) {
      if (!file.endsWith(".json")) continue;
      const parsed = augmentDraftSchema.safeParse(readJson(join(this.paths.augments, file)));
      if (parsed.success) out.push(parsed.data);
    }
    return out;
  }

  private augmentStatus(def: AugmentDefinition, adapters: Adapter[]): Pick<Augment, "status" | "statusMessage"> {
    const missing = def.requires.filter((id) => adapters.find((adapter) => adapter.id === id)?.status !== "connected");
    if (def.kind === "skill") {
      const folder = def.skillPath ? this.resolveInMods(def.skillPath) : undefined;
      const check = folder ? checkSkillFolder(folder) : { ok: false, problems: ["No skill folder is set."] };
      return check.ok
        ? { status: "ready", statusMessage: "SKILL.md checks out." }
        : { status: "error", statusMessage: check.problems.join(" ").slice(0, 600) };
    }
    if (def.kind === "mcp-server" && def.mcp?.transport === "stdio" && !this.detector.dependencies.runner.which(def.mcp.command)) {
      return { status: "needs-setup", statusMessage: `${def.mcp.command} is not installed on this PC.` };
    }
    if (missing.length > 0) {
      const names = missing.map((id) => findAdapter(id)?.name ?? id).join(", ");
      return { status: "needs-setup", statusMessage: `Connect ${names} first.` };
    }
    if (def.kind === "mcp-server" && def.mcp?.transport === "streamable-http") {
      return { status: "ready", statusMessage: "Your MCP client signs in to it with OAuth the first time it connects." };
    }
    return { status: "ready" };
  }

  async listAugments(adapters?: Adapter[]): Promise<Augment[]> {
    const live = adapters ?? (await this.detector.snapshot());
    const defs = [...BUILT_IN_AUGMENTS, ...this.userAugments()];
    return defs.map((def) => augmentSchema.parse({ ...def, ...this.augmentStatus(def, live) }));
  }

  private resolveInMods(relative: string): string | undefined {
    const full = resolve(this.paths.mods, relative);
    return full.startsWith(resolve(this.paths.mods) + sep) ? full : undefined;
  }

  async createAugment(draft: AugmentDraft): Promise<Augment> {
    const parsed = augmentDraftSchema.parse(draft);
    const slug = parsed.id.replace(/^augment:/, "");
    if (BUILT_IN_AUGMENTS.some((augment) => augment.id === parsed.id) || existsSync(join(this.paths.augments, `${slug}.json`))) {
      throw new ModsError("conflict", `An augment called ${parsed.id} already exists`);
    }
    for (const id of parsed.requires) {
      if (!findAdapter(id)) throw new ModsError("bad_request", `There is no adapter called ${id}`);
    }
    if (parsed.kind === "mcp-server" && !parsed.mcp) throw new ModsError("bad_request", "An MCP server augment needs an mcp config");
    if (parsed.kind === "prompt" && !parsed.prompt) throw new ModsError("bad_request", "A prompt augment needs its prompt text");
    if (parsed.kind === "skill") {
      const folder = parsed.skillPath ? this.resolveInMods(parsed.skillPath) : undefined;
      if (!folder) throw new ModsError("bad_request", "A skill augment needs a skillPath inside the mods folder, e.g. skills/my-skill");
      const check = checkSkillFolder(folder);
      if (!check.ok) throw new ModsError("bad_request", `The skill does not follow the Agent Skills format: ${check.problems.join(" ")}`);
    }
    writeJson(join(this.paths.augments, `${slug}.json`), parsed);
    const [augment] = await this.listAugments().then((all) => all.filter((item) => item.id === parsed.id));
    return augment as Augment;
  }

  /* ---------------------------------- mods --------------------------------- */

  private userManifests(): ModManifest[] {
    if (!existsSync(this.userModsDir)) return [];
    const out: ModManifest[] = [];
    for (const slug of readdirSync(this.userModsDir)) {
      const parsed = modManifestSchema.safeParse(readJson(join(this.userModsDir, slug, "mod.json")));
      if (parsed.success) out.push(parsed.data);
    }
    return out;
  }

  private modStatus(adapterIds: string[], augmentIds: string[], adapters: Adapter[], augments: Augment[]): Pick<Mod, "status" | "statusMessage"> {
    const total = adapterIds.length;
    const connected = adapterIds.filter((id) => adapters.find((adapter) => adapter.id === id)?.status === "connected").length;
    const broken = augmentIds.filter((id) => augments.find((augment) => augment.id === id)?.status === "error");
    if (broken.length > 0) return { status: "error", statusMessage: `${broken.length} augment${broken.length === 1 ? "" : "s"} need fixing.` };
    if (total === 0) return { status: "ready", statusMessage: "No adapters to connect." };
    if (connected === total) return { status: "ready", statusMessage: `All ${total} adapters connected.` };
    return { status: "needs-setup", statusMessage: `${connected} of ${total} adapters connected.` };
  }

  async listMods(): Promise<Mod[]> {
    const adapters = await this.detector.snapshot();
    const augments = await this.listAugments(adapters);
    const stamp = new Date(0).toISOString();
    const builtIn = BUILT_IN_MOD_DEFINITIONS.map((def: ModDefinition) =>
      modSchema.parse({
        ...def,
        builtIn: true,
        ...this.modStatus(def.adapters, def.augments, adapters, augments),
        createdAt: stamp,
        updatedAt: stamp,
      }),
    );
    const user = this.userManifests().map((manifest) =>
      modSchema.parse({
        id: manifest.id,
        name: manifest.name,
        kind: manifest.kind,
        description: manifest.description,
        adapters: manifest.adapters.map((adapter) => adapter.id),
        augments: manifest.augments,
        builtIn: false,
        ...this.modStatus(
          manifest.adapters.map((adapter) => adapter.id),
          manifest.augments,
          adapters,
          augments,
        ),
        createdAt: manifest.createdAt,
        updatedAt: manifest.updatedAt,
      }),
    );
    return [...builtIn, ...user];
  }

  async getMod(id: string): Promise<Mod> {
    const mod = (await this.listMods()).find((candidate) => candidate.id === id);
    if (!mod) throw new ModsError("not_found", `There is no mod called ${id}`);
    return mod;
  }

  /** The plus tile: compose adapters and augments into a new mod folder. */
  async createMod(draft: ModDraft): Promise<{ mod: Mod; folder: string }> {
    const slug = slugify(draft.name);
    const id = `mod:${slug}`;
    if (["dev", "pc", "launch"].includes(slug) || existsSync(join(this.userModsDir, slug))) {
      throw new ModsError("conflict", `A mod called ${draft.name} already exists. Pick another name.`);
    }
    const adapters = draft.adapters.map((adapterId) => {
      const def = findAdapter(adapterId);
      if (!def || def.id !== adapterId) throw new ModsError("bad_request", `There is no adapter called ${adapterId}`);
      return def;
    });
    const allAugments = await this.listAugments();
    const augments = draft.augments.map((augmentId) => {
      const augment = allAugments.find((candidate) => candidate.id === augmentId);
      if (!augment) throw new ModsError("bad_request", `There is no augment called ${augmentId}`);
      return augment;
    });
    // An augment's required adapters come along, so the mod is complete.
    const adapterIds = new Set(adapters.map((def) => def.id));
    for (const augment of augments) for (const required of augment.requires) adapterIds.add(required);

    const stamp = this.now().toISOString();
    const manifest: ModManifest = modManifestSchema.parse({
      manifestVersion: MOD_MANIFEST_VERSION,
      id,
      name: draft.name,
      description: draft.description,
      version: "0.1.0",
      kind: "user",
      agent: "smith",
      adapters: [...adapterIds].map((adapterId) => {
        const def = findAdapter(adapterId);
        return {
          id: adapterId,
          auth: def?.auth.method ?? "none",
          ...(def?.auth.method === "token" ? { credentialTarget: adapterCredentialTarget(def.slug) } : {}),
        };
      }),
      augments: augments.map((augment) => augment.id),
      createdAt: stamp,
      updatedAt: stamp,
    });

    const folder = join(this.userModsDir, slug);
    try {
      writeJson(join(folder, "mod.json"), manifest);
      writeJson(join(folder, ".mcp.json"), toMcpJson(augments));
      writeJson(join(folder, ".claude-plugin", "plugin.json"), toClaudePluginManifest(manifest));
      for (const augment of augments) {
        if (augment.kind !== "skill" || !augment.skillPath) continue;
        const source = this.resolveInMods(augment.skillPath);
        if (source && existsSync(source)) {
          cpSync(source, join(folder, "skills", source.split(sep).pop() as string), { recursive: true });
        }
      }
    } catch (error) {
      rmSync(folder, { recursive: true, force: true });
      throw error;
    }
    return { mod: await this.getMod(id), folder };
  }
}
