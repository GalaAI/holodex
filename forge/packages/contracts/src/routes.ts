import { z } from "zod";
import {
  adapterActionRunRequestSchema,
  adapterActionRunSchema,
  adapterConnectRequestSchema,
  adapterSchema,
  augmentDraftSchema,
  augmentSchema,
  connectActionSchema,
  modDraftSchema,
  modSchema,
} from "./mods.js";
import {
  atlasEntrySchema,
  blueprintMatchRequestSchema,
  blueprintMatchResponseSchema,
  blueprintSchema,
  craftQuestionSchema,
  craftRequestSchema,
  craftResultSchema,
  seedDecisionSchema,
  seedDraftSchema,
  seedSchema,
  starterSummarySchema,
} from "./atlas.js";
import {
  buildSchema,
  checkpointDecisionSchema,
  checkpointSchema,
  createBuildRequestSchema,
  receiptSchema,
  stageGateDecisionSchema,
} from "./build.js";
import {
  buildIdSchema,
  isoDateTimeSchema,
  relativePathSchema,
  seedIdSchema,
} from "./common.js";
import {
  annotationDraftSchema,
  annotationSchema,
  appliedTweakSchema,
  componentCandidatesResponseSchema,
  componentInventoryItemSchema,
  designSystemSchema,
  tweakOpSchema,
} from "./design.js";
import { agentIdSchema } from "./agents.js";
import { libraryItemSchema, libraryRepoDraftSchema, libraryResponseSchema } from "./library.js";
import { modelInfoSchema, modelRouteSchema } from "./models.js";
import { PORTS } from "./ports.js";
import {
  sandboxRunSchema,
  sandboxStatusSchema,
  startSandboxRequestSchema,
  startTestFlightRequestSchema,
  stopSandboxRequestSchema,
  testFlightSchema,
} from "./sandbox.js";
import {
  chatMessageSchema,
  createSessionRequestSchema,
  sendMessageRequestSchema,
  sessionEventSchema,
  sessionNodeSchema,
  sessionSchema,
} from "./session.js";

/**
 * The 4GE API route table. The server registers handlers against it, the
 * studio's client is typed from it, and e2e tests call through it, so the
 * three cannot drift.
 *
 * Every path is relative to API_PREFIX. Routes are grouped by the server
 * layer slot that owns them (apps/server/src/routes/<layer>.ts); a route no
 * layer has implemented answers 501 not_implemented with plain words.
 */
export const API_PREFIX = "/api";

/**
 * Server layer slots. "core" is the foundation (health); the rest are the
 * route files later builders fill.
 */
export const SERVER_LAYERS = ["core", "atlas", "orchestra", "design", "mods", "sandbox", "library"] as const;
export type ServerLayer = (typeof SERVER_LAYERS)[number];

export type HttpMethod = "GET" | "POST" | "PUT" | "PATCH" | "DELETE";

export interface RouteDef {
  method: HttpMethod;
  path: string;
  layer: ServerLayer;
  summary: string;
  query?: z.ZodObject<z.ZodRawShape>;
  body?: z.ZodType;
  /** JSON response for normal routes; one SSE event for stream routes. */
  response: z.ZodType;
  stream?: boolean;
  /**
   * Raw routes answer with the file itself (image, html, text), not JSON.
   * The studio points <img>, <iframe> and fetch().text() at api.url(...);
   * a server layer registers them with app.get and ctx.implemented.add.
   */
  raw?: boolean;
}

function route<const D extends RouteDef>(def: D): D {
  return def;
}

export const healthSchema = z.object({
  ok: z.literal(true),
  name: z.literal("4ge"),
  version: z.string(),
  startedAt: isoDateTimeSchema,
  ports: z.object({
    server: z.number().int(),
    studioDev: z.number().int(),
    sandboxApp: z.number().int(),
    sandboxCdp: z.number().int(),
    sandboxViewer: z.number().int(),
  }),
  /** Which layer slots have registered real handlers. */
  layers: z.record(z.string(), z.boolean()),
  /** Whether the built studio is being served from apps/studio/dist. */
  studio: z.object({ served: z.boolean(), message: z.string().optional() }),
});
export type Health = z.infer<typeof healthSchema>;

export const ROUTES = {
  /* core */
  health: route({
    method: "GET",
    path: "/health",
    layer: "core",
    summary: "Server liveness, ports and which layers are connected",
    response: healthSchema,
  }),

  /* models (orchestra) */
  "models.list": route({
    method: "GET",
    path: "/models",
    layer: "orchestra",
    summary: "The active model route and the models the provider lists",
    response: z.object({ route: modelRouteSchema, models: z.array(modelInfoSchema) }),
  }),

  /* imagistation (orchestra) */
  "imagistation.sessions.list": route({
    method: "GET",
    path: "/imagistation/sessions",
    layer: "orchestra",
    summary: "Imagistation sessions, newest first",
    response: z.object({ sessions: z.array(sessionSchema) }),
  }),
  "imagistation.sessions.create": route({
    method: "POST",
    path: "/imagistation/sessions",
    layer: "orchestra",
    summary: "Open an Imagistation session with Prometheus",
    body: z.object({ idea: z.string().max(4000).optional() }),
    response: z.object({ session: sessionSchema, messages: z.array(chatMessageSchema) }),
  }),
  "imagistation.sessions.get": route({
    method: "GET",
    path: "/imagistation/sessions/:id",
    layer: "orchestra",
    summary: "One Imagistation session with its messages",
    response: z.object({ session: sessionSchema, messages: z.array(chatMessageSchema) }),
  }),
  "imagistation.messages.send": route({
    method: "POST",
    path: "/imagistation/sessions/:id/messages",
    layer: "orchestra",
    summary: "Send a message to Prometheus; tokens stream on /sessions/:id/stream",
    body: sendMessageRequestSchema,
    response: z.object({ message: chatMessageSchema, reply: chatMessageSchema }),
  }),
  "imagistation.seed": route({
    method: "POST",
    path: "/imagistation/sessions/:id/seed",
    layer: "orchestra",
    summary: "Ask Prometheus to write the Seed Scroll from the session",
    response: z.object({ seed: seedSchema }),
  }),

  /* seeds (orchestra) */
  "seeds.list": route({
    method: "GET",
    path: "/seeds",
    layer: "orchestra",
    summary: "Seed Scrolls, newest first",
    response: z.object({ seeds: z.array(seedSchema) }),
  }),
  "seeds.create": route({
    method: "POST",
    path: "/seeds",
    layer: "orchestra",
    summary: "Save a Seed Scroll draft",
    body: seedDraftSchema,
    response: z.object({ seed: seedSchema }),
  }),
  "seeds.get": route({
    method: "GET",
    path: "/seeds/:id",
    layer: "orchestra",
    summary: "One Seed Scroll",
    response: z.object({ seed: seedSchema }),
  }),
  "seeds.decide": route({
    method: "POST",
    path: "/seeds/:id/decision",
    layer: "orchestra",
    summary: "Build now (starts a build) or later (queues it in AInbox with a reminder)",
    body: z.object({
      when: seedDecisionSchema.exclude(["pending"]),
      reminderNote: z.string().max(600).optional(),
    }),
    response: z.object({ seed: seedSchema, build: buildSchema.optional() }),
  }),

  /* atlas */
  "atlas.blueprints.list": route({
    method: "GET",
    path: "/atlas/blueprints",
    layer: "atlas",
    summary: "Every Blueprint in the user's Atlas",
    response: z.object({ entries: z.array(atlasEntrySchema) }),
  }),
  "atlas.blueprints.get": route({
    method: "GET",
    path: "/atlas/blueprints/:id",
    layer: "atlas",
    summary: "One Atlas entry; :id is the URL-encoded blueprintId",
    response: z.object({ entry: atlasEntrySchema }),
  }),
  "atlas.blueprints.register": route({
    method: "POST",
    path: "/atlas/blueprints",
    layer: "atlas",
    summary: "Register a crafted Blueprint in the user's Atlas",
    body: blueprintSchema,
    response: z.object({ entry: atlasEntrySchema }),
  }),
  "atlas.match": route({
    method: "POST",
    path: "/atlas/match",
    layer: "atlas",
    summary: "Rank Atlas Blueprints for a Seed or a bare idea",
    body: blueprintMatchRequestSchema,
    response: blueprintMatchResponseSchema,
  }),
  "atlas.starters.list": route({
    method: "GET",
    path: "/atlas/starters",
    layer: "atlas",
    summary: "The app starters Blueprints scaffold from",
    response: z.object({ starters: z.array(starterSummarySchema) }),
  }),
  "atlas.craft.questions": route({
    method: "GET",
    path: "/atlas/craft/questions",
    layer: "atlas",
    summary: "Prometheus's crafting questions, with suggestions from the idea",
    query: z.object({ idea: z.string().max(4000).optional() }),
    response: z.object({ questions: z.array(craftQuestionSchema) }),
  }),
  "atlas.craft": route({
    method: "POST",
    path: "/atlas/craft",
    layer: "atlas",
    summary: "Turn crafting answers into a validated Blueprint and its .bamf plan; register it when asked",
    body: craftRequestSchema,
    response: craftResultSchema,
  }),

  /* builds (orchestra) */
  "builds.list": route({
    method: "GET",
    path: "/builds",
    layer: "orchestra",
    summary: "Builds, newest first",
    response: z.object({ builds: z.array(buildSchema) }),
  }),
  "builds.create": route({
    method: "POST",
    path: "/builds",
    layer: "orchestra",
    summary: "Start a build from a Seed and its Blueprint",
    body: createBuildRequestSchema,
    response: z.object({ build: buildSchema }),
  }),
  "builds.get": route({
    method: "GET",
    path: "/builds/:id",
    layer: "orchestra",
    summary: "One build with its stages, layers and sprints",
    response: z.object({ build: buildSchema }),
  }),
  "builds.checkpoints.list": route({
    method: "GET",
    path: "/builds/:id/checkpoints",
    layer: "orchestra",
    summary: "Sprint checkpoints for a build",
    response: z.object({ checkpoints: z.array(checkpointSchema) }),
  }),
  "builds.checkpoints.decide": route({
    method: "POST",
    path: "/builds/:id/checkpoints/:checkpointId",
    layer: "orchestra",
    summary: "Approve, revise or skip a sprint checkpoint",
    body: checkpointDecisionSchema,
    response: z.object({ checkpoint: checkpointSchema, build: buildSchema }),
  }),
  "builds.cancel": route({
    method: "POST",
    path: "/builds/:id/cancel",
    layer: "orchestra",
    summary: "Cancel a build and every running layer session",
    response: z.object({ build: buildSchema }),
  }),
  "builds.receipts": route({
    method: "GET",
    path: "/builds/:id/receipts",
    layer: "orchestra",
    summary: "Receipts and the stage gate verdict for each stage",
    response: z.object({
      receipts: z.array(receiptSchema),
      gate: z.array(stageGateDecisionSchema),
    }),
  }),
  "builds.files.get": route({
    method: "GET",
    path: "/builds/:id/files",
    layer: "orchestra",
    summary: "One file of a build (a preview or a test flight screenshot) by its build-relative path",
    query: z.object({ path: relativePathSchema }),
    response: z.unknown(),
    raw: true,
  }),
  "builds.stream": route({
    method: "GET",
    path: "/builds/:id/stream",
    layer: "orchestra",
    summary: "SSE: every event for a build and its sessions",
    response: sessionEventSchema,
    stream: true,
  }),

  /* sessions (orchestra) */
  "sessions.list": route({
    method: "GET",
    path: "/sessions",
    layer: "orchestra",
    summary: "Sessions, optionally for one build",
    query: z.object({ buildId: buildIdSchema.optional() }),
    response: z.object({ sessions: z.array(sessionSchema) }),
  }),
  "sessions.create": route({
    method: "POST",
    path: "/sessions",
    layer: "orchestra",
    summary: "Open a session, e.g. Smith about a Library item",
    body: createSessionRequestSchema,
    response: z.object({ session: sessionSchema }),
  }),
  "sessions.get": route({
    method: "GET",
    path: "/sessions/:id",
    layer: "orchestra",
    summary: "One session with its messages",
    response: z.object({ session: sessionSchema, messages: z.array(chatMessageSchema) }),
  }),
  "sessions.tree": route({
    method: "GET",
    path: "/sessions/:id/tree",
    layer: "orchestra",
    summary: "The nested session tree under a session",
    response: z.object({ tree: sessionNodeSchema }),
  }),
  "sessions.messages.send": route({
    method: "POST",
    path: "/sessions/:id/messages",
    layer: "orchestra",
    summary: "Send a message in a session (the IDE talks to Smith here)",
    body: sendMessageRequestSchema,
    response: z.object({ message: chatMessageSchema, reply: chatMessageSchema }),
  }),
  "sessions.cancel": route({
    method: "POST",
    path: "/sessions/:id/cancel",
    layer: "orchestra",
    summary: "Cancel a session and its children",
    response: z.object({ session: sessionSchema }),
  }),
  "sessions.stream": route({
    method: "GET",
    path: "/sessions/:id/stream",
    layer: "orchestra",
    summary: "SSE: events for a session and its children",
    response: sessionEventSchema,
    stream: true,
  }),

  /* design */
  "design.systems.list": route({
    method: "GET",
    path: "/design/systems",
    layer: "design",
    summary: "Design systems for a build",
    query: z.object({ buildId: buildIdSchema }),
    response: z.object({ systems: z.array(designSystemSchema) }),
  }),
  "design.systems.save": route({
    method: "PUT",
    path: "/design/systems/:id",
    layer: "design",
    summary: "Save a design system",
    body: designSystemSchema,
    response: z.object({ system: designSystemSchema }),
  }),
  "design.inventory.list": route({
    method: "GET",
    path: "/design/inventory",
    layer: "design",
    summary: "Component inventory for a build",
    query: z.object({ buildId: buildIdSchema }),
    response: z.object({ items: z.array(componentInventoryItemSchema) }),
  }),
  "design.inventory.candidates": route({
    method: "GET",
    path: "/design/inventory/candidates",
    layer: "design",
    summary: "Component candidates for the inventory picker: @4ge/kit exports and the 21st registry, each with its source",
    query: z.object({
      buildId: buildIdSchema.optional(),
      q: z.string().trim().max(200).optional(),
    }),
    response: componentCandidatesResponseSchema,
  }),
  "design.inventory.save": route({
    method: "PUT",
    path: "/design/inventory/:id",
    layer: "design",
    summary: "Add or update one inventory item",
    body: componentInventoryItemSchema,
    response: z.object({ item: componentInventoryItemSchema }),
  }),
  "design.tweaks.list": route({
    method: "GET",
    path: "/design/tweaks",
    layer: "design",
    summary: "Tweaks applied to a build",
    query: z.object({ buildId: buildIdSchema }),
    response: z.object({ tweaks: z.array(appliedTweakSchema) }),
  }),
  "design.tweaks.apply": route({
    method: "POST",
    path: "/design/tweaks",
    layer: "design",
    summary: "Apply one tweak to the build",
    body: tweakOpSchema,
    response: z.object({ tweak: appliedTweakSchema }),
  }),
  "design.tweaks.revert": route({
    method: "POST",
    path: "/design/tweaks/:id/revert",
    layer: "design",
    summary: "Revert one applied tweak",
    response: z.object({ tweak: appliedTweakSchema }),
  }),
  "design.annotations.list": route({
    method: "GET",
    path: "/design/annotations",
    layer: "design",
    summary: "Annotations on a build",
    query: z.object({ buildId: buildIdSchema }),
    response: z.object({ annotations: z.array(annotationSchema) }),
  }),
  "design.annotations.create": route({
    method: "POST",
    path: "/design/annotations",
    layer: "design",
    summary: "Pin an annotation on the build",
    body: annotationDraftSchema,
    response: z.object({ annotation: annotationSchema }),
  }),
  "design.annotations.update": route({
    method: "PATCH",
    path: "/design/annotations/:id",
    layer: "design",
    summary: "Reply to or resolve an annotation",
    body: z.object({
      status: z.enum(["open", "resolved"]).optional(),
      reply: z.string().trim().min(1).max(4000).optional(),
      /** Who wrote the reply (default user); Smith's answers are marked smith. */
      author: z.union([z.literal("user"), agentIdSchema]).optional(),
    }),
    response: z.object({ annotation: annotationSchema }),
  }),

  /* mods, adapters, augments */
  "mods.list": route({
    method: "GET",
    path: "/mods",
    layer: "mods",
    summary: "The user's mods",
    response: z.object({ mods: z.array(modSchema) }),
  }),
  "mods.create": route({
    method: "POST",
    path: "/mods",
    layer: "mods",
    summary: "Create a mod from adapters and augments (the plus tile)",
    body: modDraftSchema,
    response: z.object({ mod: modSchema }),
  }),
  "mods.get": route({
    method: "GET",
    path: "/mods/:id",
    layer: "mods",
    summary: "One mod; :id is the URL-encoded mod ref",
    response: z.object({ mod: modSchema }),
  }),
  "adapters.list": route({
    method: "GET",
    path: "/adapters",
    layer: "mods",
    summary: "Adapters with live connection status; refresh=true skips the short cache",
    query: z.object({ refresh: z.enum(["true", "false"]).optional() }),
    response: z.object({ adapters: z.array(adapterSchema) }),
  }),
  "adapters.actions.run": route({
    method: "POST",
    path: "/adapters/:id/actions/:action",
    layer: "mods",
    summary: "Run one adapter action (domain, project, credentials, profile) through the user's own CLI",
    body: adapterActionRunRequestSchema,
    response: z.object({ run: adapterActionRunSchema }),
  }),
  "adapters.connect": route({
    method: "POST",
    path: "/adapters/:id/connect",
    layer: "mods",
    summary: "Start or finish connecting an adapter",
    body: adapterConnectRequestSchema,
    response: z.object({ adapter: adapterSchema, action: connectActionSchema }),
  }),
  "adapters.disconnect": route({
    method: "POST",
    path: "/adapters/:id/disconnect",
    layer: "mods",
    summary: "Forget an adapter's stored credential",
    response: z.object({ adapter: adapterSchema }),
  }),
  "augments.list": route({
    method: "GET",
    path: "/augments",
    layer: "mods",
    summary: "Augments: MCP servers, skills, prompts and tool packs",
    response: z.object({ augments: z.array(augmentSchema) }),
  }),
  "augments.create": route({
    method: "POST",
    path: "/augments",
    layer: "mods",
    summary: "Add an augment",
    body: augmentDraftSchema,
    response: z.object({ augment: augmentSchema }),
  }),

  /* library */
  "library.list": route({
    method: "GET",
    path: "/library",
    layer: "library",
    summary: "Every Library item grouped by origin: My projects, Repos, Mods, Augments, Adapters",
    response: libraryResponseSchema,
  }),
  "library.repos.add": route({
    method: "POST",
    path: "/library/repos",
    layer: "library",
    summary: "Add a repository shortcut to the Repos group",
    body: libraryRepoDraftSchema,
    response: z.object({ item: libraryItemSchema }),
  }),
  "library.repos.remove": route({
    method: "DELETE",
    path: "/library/repos/:id",
    layer: "library",
    summary: "Remove a repository shortcut; :id is the URL-encoded item id (repo:<slug>)",
    response: z.object({ removed: z.boolean() }),
  }),

  /* sandbox */
  "sandbox.status": route({
    method: "GET",
    path: "/sandbox/status",
    layer: "sandbox",
    summary: "Docker availability, sandbox ports and active runs",
    response: sandboxStatusSchema,
  }),
  "sandbox.start": route({
    method: "POST",
    path: "/sandbox/start",
    layer: "sandbox",
    summary: "Bring a build up in the sandbox",
    body: startSandboxRequestSchema,
    response: z.object({ run: sandboxRunSchema }),
  }),
  "sandbox.stop": route({
    method: "POST",
    path: "/sandbox/stop",
    layer: "sandbox",
    summary: "Stop a sandbox run",
    body: stopSandboxRequestSchema,
    response: z.object({ run: sandboxRunSchema }),
  }),
  "sandbox.runs.list": route({
    method: "GET",
    path: "/sandbox/runs",
    layer: "sandbox",
    summary: "Sandbox runs, optionally for one build",
    query: z.object({ buildId: buildIdSchema.optional() }),
    response: z.object({ runs: z.array(sandboxRunSchema) }),
  }),
  "sandbox.runs.get": route({
    method: "GET",
    path: "/sandbox/runs/:id",
    layer: "sandbox",
    summary: "One sandbox run",
    response: z.object({ run: sandboxRunSchema }),
  }),
  "sandbox.testFlight.start": route({
    method: "POST",
    path: "/sandbox/test-flight",
    layer: "sandbox",
    summary: "Run a test flight: bots play staff and customer against the sandbox",
    body: startTestFlightRequestSchema,
    response: z.object({ testFlight: testFlightSchema }),
  }),
  "sandbox.testFlight.get": route({
    method: "GET",
    path: "/sandbox/test-flight/:id",
    layer: "sandbox",
    summary: "One test flight with its steps",
    response: z.object({ testFlight: testFlightSchema }),
  }),
} as const satisfies Record<string, RouteDef>;

export type Routes = typeof ROUTES;
export type RouteName = keyof Routes;

/* ------------------------------ type helpers ------------------------------ */

/** Names of the `:param` segments in a path. */
export type PathParamNames<P extends string> =
  P extends `${string}:${infer Param}/${infer Rest}`
    ? Param | PathParamNames<`/${Rest}`>
    : P extends `${string}:${infer Param}`
      ? Param
      : never;

export type RouteParams<N extends RouteName> = [PathParamNames<Routes[N]["path"]>] extends [never]
  ? Record<string, never>
  : Record<PathParamNames<Routes[N]["path"]>, string>;

export type RouteBody<N extends RouteName> = Routes[N] extends { body: infer B extends z.ZodType }
  ? z.input<B>
  : never;

export type RouteQuery<N extends RouteName> = Routes[N] extends { query: infer Q extends z.ZodType }
  ? z.input<Q>
  : never;

export type RouteResponse<N extends RouteName> = z.output<Routes[N]["response"]>;

export type JsonRouteName = {
  [N in RouteName]: Routes[N] extends { stream: true } | { raw: true } ? never : N;
}[RouteName];
export type StreamRouteName = {
  [N in RouteName]: Routes[N] extends { stream: true } ? N : never;
}[RouteName];
/** Routes that answer with a file rather than JSON (see RouteDef.raw). */
export type RawRouteName = {
  [N in RouteName]: Routes[N] extends { raw: true } ? N : never;
}[RouteName];

/** Fill `:param` segments, URL-encoding each value. */
export function buildPath(path: string, params: Record<string, string> = {}): string {
  return path.replace(/:([A-Za-z][A-Za-z0-9]*)/g, (_, name: string) => {
    const value = params[name];
    if (value === undefined) throw new Error(`Missing path parameter ${name} for ${path}`);
    return encodeURIComponent(value);
  });
}

/** Every route as a flat list, for registration and docs. */
export function listRoutes(): Array<RouteDef & { name: RouteName }> {
  return (Object.keys(ROUTES) as RouteName[]).map((name) => ({ name, ...ROUTES[name] }));
}

/** Default dev ports, re-exported beside the routes for the client. */
export const DEFAULT_SERVER_PORT = PORTS.server;
