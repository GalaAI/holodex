import { z } from "zod";
import { isoDateTimeSchema, refSchemaFor, sessionIdSchema, slugSchema } from "./common.js";

/**
 * Mods, adapters and augments (SPEC "Mods, adapters, augments").
 *
 * Adapter = a connection to an account or service; auth runs through the
 * provider's own CLI login or token flow and credentials live in the OS
 * credential store, never in files, logs or the repo. Augment = a plugin:
 * an MCP server (stdio or streamable HTTP, as in the MCP specification and
 * Claude Code .mcp.json), an Agent Skill folder with SKILL.md, a prompt, or
 * a tool pack. A Mod bundles adapters and augments and is run by an agent.
 * @4ge/mods owns the catalogs and runners and cites the docs it followed.
 */

export const modIdSchema = refSchemaFor("mod");
export const adapterIdSchema = refSchemaFor("adapter");
export const augmentIdSchema = refSchemaFor("augment");

/* --------------------------------- Adapters -------------------------------- */

export const adapterCategorySchema = z.enum([
  "hosting",
  "cloud",
  "database",
  "network",
  "container",
  "model",
  "source",
  "other",
]);
export type AdapterCategory = z.infer<typeof adapterCategorySchema>;

export const adapterAuthSchema = z.discriminatedUnion("method", [
  z.object({
    method: z.literal("cli-login"),
    /** Executable on PATH, e.g. railway, vercel, gcloud. */
    cli: z.string().min(1).max(80),
    loginArgs: z.array(z.string().max(200)).max(20),
    /** Exits 0 when logged in; stdout may name the account. */
    statusArgs: z.array(z.string().max(200)).max(20),
    installUrl: z.string().url(),
  }),
  z.object({
    method: z.literal("token"),
    /** Environment variable the provider's tools read, e.g. NORTHFLANK_TOKEN. */
    env: z.string().regex(/^[A-Z][A-Z0-9_]*$/),
    /** Where the user creates the token. */
    tokenUrl: z.string().url(),
    /** OS credential store target name, e.g. 4GE:adapter:northflank. */
    credentialTarget: z.string().min(1).max(200),
  }),
  z.object({ method: z.literal("none") }),
]);
export type AdapterAuth = z.infer<typeof adapterAuthSchema>;

export const connectionStatusSchema = z.enum([
  "connected",
  "disconnected",
  "needs-cli",
  "expired",
  "error",
]);
export type ConnectionStatus = z.infer<typeof connectionStatusSchema>;

/**
 * The provider's own CLI as this PC sees it (added by the mods builder,
 * optional so older readers keep working). `installed` comes from a PATH
 * lookup at detection time; no local path is ever returned.
 */
export const adapterCliSchema = z.object({
  bin: z.string().min(1).max(80),
  /** The official install command, e.g. npm i -g vercel. */
  install: z.string().min(1).max(300),
  installUrl: z.string().url(),
  /** The command that signs in with the provider's own flow, e.g. vercel login. */
  login: z.string().min(1).max(300),
  installed: z.boolean(),
  version: z.string().max(80).optional(),
});
export type AdapterCli = z.infer<typeof adapterCliSchema>;

/** One action a mod exposes through an adapter, run with the user's own CLI or MCP. */
export const adapterActionParamSchema = z.object({
  name: z.string().regex(/^[a-z][a-zA-Z0-9]*$/),
  label: z.string().min(1).max(120),
  required: z.boolean().default(true),
  /** Allowed characters; the runner refuses anything else before it reaches a CLI. */
  pattern: z.string().max(200).optional(),
  placeholder: z.string().max(120).optional(),
});
export type AdapterActionParam = z.infer<typeof adapterActionParamSchema>;

export const adapterActionKindSchema = z.enum(["domain", "project", "credentials", "profile", "account"]);
export type AdapterActionKind = z.infer<typeof adapterActionKindSchema>;

export const adapterActionSchema = z.object({
  id: slugSchema,
  title: z.string().min(1).max(120),
  description: z.string().min(1).max(600),
  kind: adapterActionKindSchema,
  via: z.enum(["cli", "mcp"]),
  /**
   * Interactive actions open the provider's CLI in its own window so the CLI
   * asks its own questions (sign-in, purchase confirmation). The rest run in
   * the background and return their output.
   */
  interactive: z.boolean(),
  /** True when the action can spend money (a domain purchase). Needs confirm. */
  spends: z.boolean().default(false),
  params: z.array(adapterActionParamSchema).max(10).default([]),
  /** The command with {param} placeholders, for display. Never holds a secret. */
  command: z.string().min(1).max(600),
  /** For via mcp: the tool on the adapter's official MCP server. */
  mcpTool: z.string().max(120).optional(),
});
export type AdapterAction = z.infer<typeof adapterActionSchema>;

export const adapterActionRunSchema = z.object({
  adapterId: z.string().max(160),
  actionId: z.string().max(80),
  status: z.enum(["done", "failed", "opened", "refused"]),
  /** The command as run, secrets never included. */
  command: z.string().max(800),
  exitCode: z.number().int().optional(),
  /** Redacted, trimmed CLI output. */
  output: z.string().max(8000).optional(),
  message: z.string().min(1).max(600),
  ranAt: isoDateTimeSchema,
});
export type AdapterActionRun = z.infer<typeof adapterActionRunSchema>;

export const adapterActionRunRequestSchema = z.object({
  params: z.record(z.string(), z.string().max(300)).default({}),
  /** Required true for actions that spend money. */
  confirm: z.boolean().optional(),
});
export type AdapterActionRunRequest = z.infer<typeof adapterActionRunRequestSchema>;

export const adapterSchema = z.object({
  id: adapterIdSchema,
  name: z.string().min(1).max(120),
  provider: z.string().min(1).max(120),
  category: adapterCategorySchema,
  description: z.string().min(1).max(600),
  auth: adapterAuthSchema,
  signUpUrl: z.string().url(),
  docsUrl: z.string().url(),
  status: connectionStatusSchema,
  /** Account label the provider reports, e.g. a team name. Never a secret. */
  account: z.string().max(200).optional(),
  /** Plain words for the current status. */
  statusMessage: z.string().max(600).optional(),
  lastCheckedAt: isoDateTimeSchema.optional(),
  /** The provider's official CLI and whether this PC has it (mods builder addition). */
  cli: adapterCliSchema.optional(),
  /** The augment holding the provider's official MCP server, when one exists. */
  mcpAugment: z.string().max(160).optional(),
  /** Plain words about the provider's MCP server, including when none is official. */
  mcpNote: z.string().max(600).optional(),
  /** What a mod can do through this adapter. */
  actions: z.array(adapterActionSchema).max(30).optional(),
});
export type Adapter = z.infer<typeof adapterSchema>;

/** What the UI should do next to connect an adapter. */
export const connectActionSchema = z.discriminatedUnion("kind", [
  z.object({
    kind: z.literal("run-cli"),
    /** The command the server started or the user should run, no secrets. */
    command: z.string().min(1).max(600),
    message: z.string().min(1).max(600),
  }),
  z.object({ kind: z.literal("open-url"), url: z.string().url(), message: z.string().min(1).max(600) }),
  z.object({
    kind: z.literal("enter-token"),
    tokenUrl: z.string().url(),
    message: z.string().min(1).max(600),
  }),
  z.object({ kind: z.literal("install-cli"), url: z.string().url(), message: z.string().min(1).max(600) }),
  z.object({ kind: z.literal("done"), message: z.string().min(1).max(600) }),
]);
export type ConnectAction = z.infer<typeof connectActionSchema>;

/** Token entry goes straight to the OS credential store; it is never echoed. */
export const adapterConnectRequestSchema = z.object({
  token: z.string().min(1).max(8000).optional(),
});

/* --------------------------------- Augments -------------------------------- */

export const mcpServerConfigSchema = z.discriminatedUnion("transport", [
  z.object({
    transport: z.literal("stdio"),
    command: z.string().min(1).max(400),
    args: z.array(z.string().max(1000)).max(60).default([]),
    /** Names of env vars to pass; values come from the credential store. */
    envKeys: z.array(z.string().regex(/^[A-Za-z_][A-Za-z0-9_]*$/)).max(40).default([]),
  }),
  z.object({
    transport: z.literal("streamable-http"),
    url: z.string().url(),
    /** Header names whose values come from the credential store. */
    headerKeys: z.array(z.string().min(1).max(120)).max(20).default([]),
  }),
]);
export type McpServerConfig = z.infer<typeof mcpServerConfigSchema>;

export const augmentKindSchema = z.enum(["mcp-server", "skill", "prompt", "tool-pack", "plugin"]);
export type AugmentKind = z.infer<typeof augmentKindSchema>;

export const augmentSchema = z.object({
  id: augmentIdSchema,
  name: z.string().min(1).max(120),
  kind: augmentKindSchema,
  description: z.string().min(1).max(1000),
  mcp: mcpServerConfigSchema.optional(),
  /** Folder holding SKILL.md (Agent Skills), relative to the mods data root. */
  skillPath: z.string().max(400).optional(),
  /** Prompt text for kind prompt. */
  prompt: z.string().max(20_000).optional(),
  /** Adapters this augment needs connected first. */
  requires: z.array(adapterIdSchema).max(20).default([]),
  sourceUrl: z.string().url().optional(),
  enabled: z.boolean().default(true),
  status: z.enum(["ready", "needs-setup", "error"]).default("needs-setup"),
  statusMessage: z.string().max(600).optional(),
});
export type Augment = z.infer<typeof augmentSchema>;

export const augmentDraftSchema = augmentSchema.omit({ status: true, statusMessage: true });
export type AugmentDraft = z.infer<typeof augmentDraftSchema>;

/* ----------------------------------- Mods ---------------------------------- */

export const modKindSchema = z.enum(["dev", "pc", "launch", "user", "build"]);
export type ModKind = z.infer<typeof modKindSchema>;

/** The three mods every user starts with. */
export const BUILT_IN_MODS = ["mod:dev", "mod:pc", "mod:launch"] as const;

/** Launch Mod adapters to start with (SPEC). */
export const LAUNCH_MOD_ADAPTERS = [
  "adapter:northflank",
  "adapter:cloudflare",
  "adapter:gcp",
  "adapter:railway",
  "adapter:vercel",
  "adapter:supabase",
  "adapter:docker",
  "adapter:tailscale",
] as const;

export const modSchema = z.object({
  id: modIdSchema,
  name: z.string().min(1).max(120),
  kind: modKindSchema,
  description: z.string().min(1).max(1000),
  adapters: z.array(adapterIdSchema).max(60).default([]),
  augments: z.array(augmentIdSchema).max(60).default([]),
  builtIn: z.boolean().default(false),
  status: z.enum(["ready", "needs-setup", "error"]),
  statusMessage: z.string().max(600).optional(),
  /** The Smith session that manages this mod, once one exists. */
  sessionId: sessionIdSchema.optional(),
  createdAt: isoDateTimeSchema,
  updatedAt: isoDateTimeSchema,
});
export type Mod = z.infer<typeof modSchema>;

/** The plus tile: create a mod from adapters and augments. */
export const modDraftSchema = z.object({
  name: z.string().trim().min(1).max(120),
  description: z.string().trim().min(1).max(1000),
  adapters: z.array(adapterIdSchema).max(60).default([]),
  augments: z.array(augmentIdSchema).max(60).default([]),
});
export type ModDraft = z.infer<typeof modDraftSchema>;
