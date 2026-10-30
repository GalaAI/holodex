import { existsSync, readFileSync } from "node:fs";
import { basename, join } from "node:path";
import { z } from "zod";
import { parse as parseYaml } from "yaml";
import { adapterIdSchema, augmentIdSchema, isoDateTimeSchema, modIdSchema, type Augment, type McpServerConfig } from "@4ge/contracts";

/**
 * Manifest formats (documented in packages/mods/README.md).
 *
 * 1. mod.json: the 4GE mod manifest. Names the adapters and augments a mod
 *    composes; holds no secret, only credential target names.
 * 2. .mcp.json: the mod's MCP servers in the shape Claude Code and other MCP
 *    clients read ({ mcpServers: { name: { command, args, env } | { type:
 *    "http", url, headers } } }), with ${VAR} placeholders for secrets.
 * 3. .claude-plugin/plugin.json: a Claude Code plugin manifest, so a mod
 *    folder loads as a plugin (name, displayName, version, description; the
 *    default skills/ and .mcp.json locations carry the components).
 * 4. skills/<name>/SKILL.md: Agent Skills (agentskills.io specification).
 */

export const MOD_MANIFEST_VERSION = 1;

export const modManifestSchema = z.object({
  manifestVersion: z.literal(MOD_MANIFEST_VERSION),
  id: modIdSchema,
  name: z.string().min(1).max(120),
  description: z.string().min(1).max(1000),
  version: z.string().regex(/^\d+\.\d+\.\d+$/),
  kind: z.enum(["dev", "pc", "launch", "user", "build"]),
  /** The agent that runs the mod. */
  agent: z.literal("smith"),
  adapters: z.array(
    z.object({
      id: adapterIdSchema,
      auth: z.enum(["cli-login", "token", "none"]),
      /** Windows Credential Manager target for token adapters. Never the secret. */
      credentialTarget: z.string().max(200).optional(),
    }),
  ),
  augments: z.array(augmentIdSchema),
  createdAt: isoDateTimeSchema,
  updatedAt: isoDateTimeSchema,
});
export type ModManifest = z.infer<typeof modManifestSchema>;

/* ---------------------------------- .mcp.json --------------------------------- */

export type McpJsonServer =
  | { command: string; args: string[]; env?: Record<string, string> }
  | { type: "http"; url: string; headers?: Record<string, string> };

/** One augment's server in .mcp.json shape. Secrets are ${VAR} placeholders the client expands. */
export function toMcpJsonServer(config: McpServerConfig): McpJsonServer {
  if (config.transport === "stdio") {
    const env = Object.fromEntries((config.envKeys ?? []).map((key) => [key, `\${${key}}`]));
    return { command: config.command, args: [...(config.args ?? [])], ...(Object.keys(env).length ? { env } : {}) };
  }
  const headers = Object.fromEntries(
    (config.headerKeys ?? []).map((key) => [key, `\${${key.toUpperCase().replace(/[^A-Z0-9]+/g, "_")}}`]),
  );
  return { type: "http", url: config.url, ...(Object.keys(headers).length ? { headers } : {}) };
}

export function mcpServerName(augmentId: string): string {
  return augmentId.replace(/^augment:/, "").replace(/-mcp$/, "");
}

export function toMcpJson(augments: Augment[]): { mcpServers: Record<string, McpJsonServer> } {
  const servers: Record<string, McpJsonServer> = {};
  for (const augment of augments) {
    if (augment.kind === "mcp-server" && augment.mcp) servers[mcpServerName(augment.id)] = toMcpJsonServer(augment.mcp);
  }
  return { mcpServers: servers };
}

/* ------------------------------ Claude Code plugin ----------------------------- */

export function toClaudePluginManifest(manifest: ModManifest) {
  return {
    name: manifest.id.replace(/^mod:/, "4ge-"),
    displayName: manifest.name,
    version: manifest.version,
    description: manifest.description,
    keywords: ["4ge", "mod", manifest.kind],
  };
}

/* --------------------------------- Agent Skills -------------------------------- */

export interface SkillCheck {
  ok: boolean;
  name?: string;
  description?: string;
  problems: string[];
}

const SKILL_NAME = /^[a-z0-9]+(-[a-z0-9]+)*$/;

/** Check a skill folder against the Agent Skills specification (agentskills.io/specification). */
export function checkSkillFolder(folder: string): SkillCheck {
  const file = join(folder, "SKILL.md");
  if (!existsSync(file)) return { ok: false, problems: ["The folder has no SKILL.md."] };
  const text = readFileSync(file, "utf8");
  const match = /^---\r?\n([\s\S]*?)\r?\n---\r?\n?/.exec(text);
  if (!match) return { ok: false, problems: ["SKILL.md must start with YAML frontmatter between --- lines."] };
  let front: Record<string, unknown>;
  try {
    front = (parseYaml(match[1] as string) ?? {}) as Record<string, unknown>;
  } catch {
    return { ok: false, problems: ["The SKILL.md frontmatter is not valid YAML."] };
  }
  const problems: string[] = [];
  const name = typeof front.name === "string" ? front.name : undefined;
  const description = typeof front.description === "string" ? front.description : undefined;
  if (!name) problems.push("name is required.");
  else {
    if (name.length > 64) problems.push("name must be 64 characters or fewer.");
    if (!SKILL_NAME.test(name)) problems.push("name may only use lowercase letters, numbers and single hyphens.");
    if (name !== basename(folder)) problems.push("name must match the folder name.");
  }
  if (!description) problems.push("description is required.");
  else if (description.length > 1024) problems.push("description must be 1024 characters or fewer.");
  if (front.compatibility !== undefined && (typeof front.compatibility !== "string" || front.compatibility.length > 500)) {
    problems.push("compatibility must be text of 500 characters or fewer.");
  }
  if (front.metadata !== undefined) {
    const meta = front.metadata;
    if (typeof meta !== "object" || meta === null || Array.isArray(meta) || Object.values(meta).some((value) => typeof value !== "string")) {
      problems.push("metadata must map text keys to text values.");
    }
  }
  return { ok: problems.length === 0, ...(name ? { name } : {}), ...(description ? { description } : {}), problems };
}
