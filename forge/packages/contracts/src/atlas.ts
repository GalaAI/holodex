import { z } from "zod";
import { agentIdSchema } from "./agents.js";
import { personaSchema } from "./sandbox.js";
import {
  buildIdSchema,
  isoDateTimeSchema,
  seedIdSchema,
  sessionIdSchema,
} from "./common.js";
import { designTokensSchema } from "./design.js";
// Type-only: library.ts imports session.ts, which imports this file.
import type { LibraryOrigin } from "./library.js";
import {
  buildKindSchema,
  layerKindSchema,
  platformSchema,
  stageSchema,
  type Stage,
} from "./stages.js";

/**
 * Blueprints, the Atlas and Seed Scrolls.
 *
 * Adapted from GalaAI/ark packages/atlas-core/src/blueprint.ts (blueprintId
 * shape, semver version, draft to promoted state, typed dependencies and
 * {{param}} parameters) and docs/architecture/SEED_SCROLL.md (a Seed keeps
 * the idea the user's own: their framing, the problem, who it serves and how
 * success shows). The Ark lexicon facets (trade, archetype, vessel kind) are
 * replaced by the 4GE build kind and platforms, and the Ark BuildComponent
 * templates by a stage plan the orchestra runs directly.
 */

export const blueprintIdSchema = z
  .string()
  .max(140)
  .regex(/^blueprint:[a-z0-9][a-z0-9.-]*$/, "blueprint ids look like blueprint:<slug>");
export type BlueprintId = z.infer<typeof blueprintIdSchema>;

export const blueprintStateSchema = z.enum([
  "draft",
  "reviewed",
  "promoted",
  "deprecated",
]);
export type BlueprintState = z.infer<typeof blueprintStateSchema>;

export const blueprintOriginSchema = z.enum(["starter", "crafted", "imported"]);
export type BlueprintOrigin = z.infer<typeof blueprintOriginSchema>;

export const blueprintDependencySchema = z.object({
  name: z.string().min(1).max(200),
  kind: z.enum([
    "npm-package",
    "runtime",
    "service",
    "development-tool",
    "deployment-target",
    "adapter",
    "augment",
  ]),
  scope: z.enum(["build", "runtime", "development", "deployment"]),
  versionRange: z.string().min(1).max(120).optional(),
  required: z.boolean().default(true),
  role: z.string().min(1).max(400),
});
export type BlueprintDependency = z.infer<typeof blueprintDependencySchema>;

export const blueprintParameterSchema = z.object({
  key: z
    .string()
    .max(60)
    .regex(/^[a-z][a-zA-Z0-9]*$/, "parameter keys are camelCase identifiers"),
  description: z.string().min(1).max(400),
  required: z.boolean().default(true),
  defaultValue: z.string().max(240).optional(),
});
export type BlueprintParameter = z.infer<typeof blueprintParameterSchema>;

/** One layer in a stage plan. String fields may carry {{param}} placeholders. */
export const layerPlanSchema = z.object({
  /** Stable within the Blueprint, kebab-case, e.g. "booking-api". */
  key: z
    .string()
    .min(2)
    .max(80)
    .regex(/^[a-z0-9][a-z0-9-]*$/, "layer keys are kebab-case"),
  kind: layerKindSchema,
  title: z.string().min(1).max(160),
  agent: agentIdSchema,
  description: z.string().min(1).max(2000),
  /** Keys of layers (any stage) that must pass first. */
  dependsOn: z.array(z.string().min(2).max(80)).max(40).default([]),
  /** Build-relative paths or named outputs this layer produces. */
  deliverables: z.array(z.string().min(1).max(400)).max(60).default([]),
  /** Checks the layer's receipt must record as passed. */
  checks: z.array(z.string().min(1).max(400)).min(1).max(40),
});
export type LayerPlan = z.infer<typeof layerPlanSchema>;

/**
 * A typed reference to something a build connects: an adapter (account or
 * service), an augment (MCP server, skill, prompt, tool pack), a mod, an app
 * in the user's Library (app:ryozan) or a repo shortcut. Each kind files
 * under one Library origin group (CONNECTION_ORIGIN), so a Blueprint's
 * connections show up grouped the same way the Library is.
 */
export const CONNECTION_KINDS = ["adapter", "augment", "mod", "app", "repo"] as const;
export type ConnectionKind = (typeof CONNECTION_KINDS)[number];

export const connectionRefSchema = z
  .string()
  .max(160)
  .regex(/^(adapter|augment|mod|app|repo):[a-z0-9][a-z0-9._-]*$/, "connection refs look like adapter:<slug>, augment:<slug>, mod:<slug>, app:<slug> or repo:<slug>");
export type ConnectionRef = z.infer<typeof connectionRefSchema>;

/** Which Library origin group each connection kind files under. */
export const CONNECTION_ORIGIN: Record<ConnectionKind, LibraryOrigin> = {
  app: "project",
  repo: "repo",
  mod: "mod",
  augment: "augment",
  adapter: "adapter",
};

/** Library origin order (mirrors LIBRARY_ORIGINS; kept here to avoid an import cycle). */
const ORIGIN_ORDER: readonly LibraryOrigin[] = ["project", "repo", "mod", "augment", "adapter"];

export interface ConnectionGroup {
  origin: LibraryOrigin;
  refs: ConnectionRef[];
}

/**
 * Group connection refs by Library origin, in Library order (My projects,
 * Repos, Mods, Augments, Adapters). Empty groups are kept, so a UI can show
 * all five; strings that are not connection refs are dropped.
 */
export function connectionsByOrigin(refs: readonly string[]): ConnectionGroup[] {
  const groups = new Map<LibraryOrigin, ConnectionRef[]>(ORIGIN_ORDER.map((origin) => [origin, []]));
  for (const ref of new Set(refs)) {
    if (!connectionRefSchema.safeParse(ref).success) continue;
    const kind = ref.slice(0, ref.indexOf(":")) as ConnectionKind;
    groups.get(CONNECTION_ORIGIN[kind])?.push(ref);
  }
  return ORIGIN_ORDER.map((origin) => ({ origin, refs: groups.get(origin) ?? [] }));
}

/** The design system a Blueprint starts the build with (step 5); Smith and the user tune it from here. */
export const blueprintDesignSystemSchema = z.object({
  name: z.string().min(1).max(160),
  source: z.enum(["kit", "generated", "imported"]).default("kit"),
  /** One line on the look and feel. */
  summary: z.string().min(1).max(600),
  tokens: designTokensSchema.prefault({}),
  principles: z.array(z.string().min(1).max(400)).max(20).default([]),
});
export type BlueprintDesignSystem = z.infer<typeof blueprintDesignSystemSchema>;

/**
 * One job the AI orchestration runs after launch (step 6): who runs it, how
 * often, and what it connects to (for a site: content, SEO, social and
 * bookings through Ryozan).
 */
export const orchestrationJobSchema = z.object({
  key: z
    .string()
    .min(2)
    .max(80)
    .regex(/^[a-z0-9][a-z0-9-]*$/, "orchestration keys are kebab-case"),
  title: z.string().min(1).max(160),
  agent: agentIdSchema,
  description: z.string().min(1).max(1000),
  cadence: z.enum(["continuous", "daily", "weekly", "monthly", "on-event"]),
  connections: z.array(connectionRefSchema).max(20).default([]),
});
export type OrchestrationJob = z.infer<typeof orchestrationJobSchema>;

export const stagePlanSchema = z.object({
  stage: stageSchema,
  /** One line the checkpoint shows above the previews. */
  goal: z.string().min(1).max(600),
  layers: z.array(layerPlanSchema).min(1).max(40),
  checkpoint: z.object({
    title: z.string().min(1).max(160),
    /** The question the user answers at the end of the sprint. */
    question: z.string().min(1).max(600),
  }),
});
export type StagePlan = z.infer<typeof stagePlanSchema>;

export const blueprintSchema = z
  .object({
    blueprintId: blueprintIdSchema,
    name: z.string().min(1).max(200),
    version: z.string().regex(/^\d+\.\d+\.\d+$/, "version is semver x.y.z"),
    state: blueprintStateSchema.default("draft"),
    origin: blueprintOriginSchema,
    kind: buildKindSchema,
    platforms: z.array(platformSchema).min(1).max(6),
    description: z.string().min(1).max(2000),
    /** forge/starters/<name> the scaffold starts from, when one fits. */
    starter: z.string().min(1).max(120).optional(),
    parameters: z.array(blueprintParameterSchema).max(40).default([]),
    dependencies: z.array(blueprintDependencySchema).max(100).default([]),
    /** The four stages in order; the orchestra runs each as one sprint. */
    stages: z.array(stagePlanSchema).min(1).max(4),
    /** Adapter, augment and mod refs the build is likely to connect. */
    suggestedConnections: z.array(z.string().min(3).max(160)).max(40).default([]),
    /** What Smith proposes to manage and promote the result (step 6). */
    orchestrationSuggestions: z.array(z.string().min(1).max(400)).max(20).default([]),
    /** The same proposal, structured: agents, cadence and connections per job. */
    orchestration: z.array(orchestrationJobSchema).max(20).default([]),
    /** The design system the build starts from. */
    designSystem: blueprintDesignSystemSchema.optional(),
    /**
     * Who the sandbox bots play in a test flight (SPEC flow step 8): at least a
     * staff user and a customer user. Optional; Icarus derives both from the
     * build kind when a Blueprint names none. Added by the sandbox builder.
     */
    personas: z.array(personaSchema).max(8).optional(),
    sourceRefs: z.array(z.string().min(1).max(400)).max(40).default([]),
    tags: z.array(z.string().min(1).max(60)).max(30).default([]),
    createdAt: isoDateTimeSchema.optional(),
    updatedAt: isoDateTimeSchema.optional(),
  })
  .superRefine((blueprint, context) => {
    const seenStages = new Set<string>();
    const keys = new Set<string>();
    blueprint.stages.forEach((plan, stageIndex) => {
      if (seenStages.has(plan.stage)) {
        context.addIssue({
          code: "custom",
          path: ["stages", stageIndex, "stage"],
          message: `stage ${plan.stage} appears twice`,
        });
      }
      seenStages.add(plan.stage);
      plan.layers.forEach((layer, layerIndex) => {
        if (keys.has(layer.key)) {
          context.addIssue({
            code: "custom",
            path: ["stages", stageIndex, "layers", layerIndex, "key"],
            message: `duplicate layer key '${layer.key}'`,
          });
        }
        keys.add(layer.key);
      });
    });
    blueprint.stages.forEach((plan, stageIndex) => {
      plan.layers.forEach((layer, layerIndex) => {
        layer.dependsOn.forEach((dependency, dependencyIndex) => {
          if (!keys.has(dependency)) {
            context.addIssue({
              code: "custom",
              path: ["stages", stageIndex, "layers", layerIndex, "dependsOn", dependencyIndex],
              message: `depends on unknown layer '${dependency}'`,
            });
          }
        });
      });
    });
    const dependencyNames = new Set<string>();
    blueprint.dependencies.forEach((dependency, index) => {
      if (dependencyNames.has(dependency.name)) {
        context.addIssue({
          code: "custom",
          path: ["dependencies", index, "name"],
          message: `duplicate Blueprint dependency '${dependency.name}'`,
        });
      }
      dependencyNames.add(dependency.name);
    });
    const jobKeys = new Set<string>();
    blueprint.orchestration.forEach((job, index) => {
      if (jobKeys.has(job.key)) {
        context.addIssue({
          code: "custom",
          path: ["orchestration", index, "key"],
          message: `duplicate orchestration job '${job.key}'`,
        });
      }
      jobKeys.add(job.key);
    });
    const order = blueprint.stages.map((plan) => plan.stage);
    const sorted = [...order].sort(
      (left, right) => STAGE_INDEX[left] - STAGE_INDEX[right],
    );
    if (order.join() !== sorted.join()) {
      context.addIssue({
        code: "custom",
        path: ["stages"],
        message: "stages must follow architecture, orchestration, interface, deploy order",
      });
    }
  });
export type Blueprint = z.infer<typeof blueprintSchema>;
export type BlueprintInput = z.input<typeof blueprintSchema>;

const STAGE_INDEX: Record<Stage, number> = {
  architecture: 0,
  orchestration: 1,
  interface: 2,
  deploy: 3,
};

/** One Blueprint as registered in the user's Atlas. */
export const atlasEntrySchema = z.object({
  blueprint: blueprintSchema,
  source: z.enum(["starter", "user"]),
  registeredAt: isoDateTimeSchema,
  /** How many builds started from it. */
  uses: z.number().int().nonnegative().default(0),
  lastUsedAt: isoDateTimeSchema.optional(),
});
export type AtlasEntry = z.infer<typeof atlasEntrySchema>;

export const blueprintMatchSchema = z.object({
  blueprintId: blueprintIdSchema,
  /** 0 to 1. */
  score: z.number().min(0).max(1),
  reason: z.string().min(1).max(600),
});
export type BlueprintMatch = z.infer<typeof blueprintMatchSchema>;

/**
 * The Seed Scroll Prometheus writes. The user's own framing stays verbatim
 * in `idea`; Prometheus questions and organizes but does not author it.
 */
export const seedStatusSchema = z.enum([
  "draft",
  "ready",
  "queued",
  "building",
  "built",
  "archived",
]);
export type SeedStatus = z.infer<typeof seedStatusSchema>;

export const seedDecisionSchema = z.enum(["pending", "now", "later"]);
export type SeedDecision = z.infer<typeof seedDecisionSchema>;

export const seedDraftSchema = z.object({
  title: z.string().min(1).max(160),
  /** The user's own words, kept as said. */
  idea: z.string().trim().min(1, "Say what you want to build").max(4000),
  problem: z.string().max(1200).default(""),
  audience: z.string().max(1200).default(""),
  successSignal: z.string().max(1200).default(""),
  kind: buildKindSchema,
  platforms: z.array(platformSchema).min(1).max(6),
  features: z.array(z.string().min(1).max(400)).max(60).default([]),
  constraints: z.array(z.string().min(1).max(400)).max(40).default([]),
  references: z.array(z.string().min(1).max(600)).max(40).default([]),
  /** Answers to Prometheus's follow-up questions, in order. */
  answers: z
    .array(z.object({ question: z.string().max(600), answer: z.string().max(2000) }))
    .max(40)
    .default([]),
  blueprintId: blueprintIdSchema.optional(),
  blueprintMatch: blueprintMatchSchema.optional(),
  /** Blueprint parameter values, keyed by parameter key. */
  parameters: z.record(z.string(), z.string().max(400)).default({}),
});
export type SeedDraft = z.infer<typeof seedDraftSchema>;
export type SeedDraftInput = z.input<typeof seedDraftSchema>;

export const seedSchema = seedDraftSchema.extend({
  id: seedIdSchema,
  status: seedStatusSchema,
  decision: seedDecisionSchema,
  imagistationSessionId: sessionIdSchema.optional(),
  buildId: buildIdSchema.optional(),
  /** Where the "later" path wrote the AInbox item (file name, not a full path). */
  ainboxItem: z.string().max(260).optional(),
  reminderNote: z.string().max(600).optional(),
  createdAt: isoDateTimeSchema,
  updatedAt: isoDateTimeSchema,
});
export type Seed = z.infer<typeof seedSchema>;

/** SEED_SCROLL.md promotion readiness: problem, audience and success stated. */
export function seedReadiness(seed: Pick<SeedDraft, "problem" | "audience" | "successSignal" | "idea">): {
  ready: boolean;
  missing: Array<"idea" | "problem" | "audience" | "successSignal">;
} {
  const missing = (["idea", "problem", "audience", "successSignal"] as const).filter(
    (field) => seed[field].trim().length === 0,
  );
  return { ready: missing.length === 0, missing };
}

/** An app starter in forge/starters, as the studio lists it. */
export const starterSummarySchema = z.object({
  name: z.string().min(1).max(120),
  title: z.string().min(1).max(160),
  description: z.string().min(1).max(1000),
  kind: buildKindSchema,
  platforms: z.array(platformSchema).min(1),
  /** Blueprints in the user's Atlas that scaffold from it. */
  blueprintIds: z.array(blueprintIdSchema).default([]),
});
export type StarterSummary = z.infer<typeof starterSummarySchema>;

/* --------------------------- Matching an idea --------------------------- */

/**
 * Match a Seed (by id) or a bare idea against the user's Atlas. kind and
 * platforms narrow the match when the caller already knows them; otherwise
 * the Atlas infers them from the idea's words.
 */
export const blueprintMatchRequestSchema = z
  .object({
    seedId: seedIdSchema.optional(),
    idea: z.string().trim().min(1, "Say what you want to build").max(4000).optional(),
    kind: buildKindSchema.optional(),
    platforms: z.array(platformSchema).min(1).max(6).optional(),
    limit: z.number().int().min(1).max(20).optional(),
  })
  .refine((request) => request.seedId !== undefined || request.idea !== undefined, {
    message: "Send a seedId or an idea to match",
    path: ["idea"],
  });
export type BlueprintMatchRequest = z.infer<typeof blueprintMatchRequestSchema>;

export const blueprintMatchResponseSchema = z.object({
  matches: z.array(blueprintMatchSchema),
  /** True when no Blueprint scores at or above the match threshold: Prometheus crafts one with the user. */
  needsCrafting: z.boolean().optional(),
  /** What the Atlas read from the idea when the caller did not say. */
  inferred: z
    .object({ kind: buildKindSchema.optional(), platforms: z.array(platformSchema) })
    .optional(),
});
export type BlueprintMatchResponse = z.infer<typeof blueprintMatchResponseSchema>;

/* ---------------------- Prometheus's crafting method --------------------- */

/**
 * The fields Prometheus asks about, in order, when no Blueprint fits. The
 * answers turn into a full Blueprint (zod-validated) and a .bamf plan.
 */
export const CRAFT_FIELDS = [
  "name",
  "kind",
  "platforms",
  "purpose",
  "records",
  "actions",
  "roles",
  "integrations",
  "starter",
  "afterLaunch",
  "look",
] as const;
export const craftFieldSchema = z.enum(CRAFT_FIELDS);
export type CraftField = z.infer<typeof craftFieldSchema>;

export const craftQuestionSchema = z.object({
  field: craftFieldSchema,
  /** What Prometheus asks, in plain words. */
  question: z.string().min(1).max(400),
  /** Why it matters for the build, one line. */
  why: z.string().min(1).max(400),
  input: z.enum(["text", "choice", "multi", "list"]),
  options: z.array(z.object({ value: z.string(), label: z.string() })).default([]),
  required: z.boolean(),
  /** What Prometheus proposes from the idea; the user confirms or changes it. */
  suggestion: z.union([z.string(), z.array(z.string())]).optional(),
});
export type CraftQuestion = z.infer<typeof craftQuestionSchema>;

const craftItem = z.string().trim().min(1).max(120);

/** Complete, validated answers. */
export const craftAnswersSchema = z.object({
  name: z.string().trim().min(3, "Give the Blueprint a name of at least 3 characters").max(120),
  kind: buildKindSchema,
  platforms: z.array(platformSchema).min(1, "Pick at least one platform").max(6),
  purpose: z.string().trim().min(10, "Say in a sentence what it does and for whom").max(1200),
  records: z.array(craftItem).min(1, "Name at least one thing it keeps track of").max(20),
  actions: z.array(craftItem).min(1, "Name at least one thing people do in it").max(20),
  roles: z.array(craftItem).min(1, "Name at least one kind of user").max(8),
  integrations: z.array(craftItem).max(20).default([]),
  starter: z.string().trim().max(120).default("none"),
  afterLaunch: z.array(craftItem).max(10).default([]),
  look: z.string().trim().max(600).default(""),
});
export type CraftAnswers = z.infer<typeof craftAnswersSchema>;

const craftList = z.union([z.string().max(4000), z.array(z.string().max(400)).max(40)]);

/** Answers so far, loosely typed: lists may come as one comma or newline separated string. */
export const craftDraftSchema = z.object({
  name: z.string().max(400).optional(),
  kind: z.string().max(40).optional(),
  platforms: z.array(z.string().max(40)).max(10).optional(),
  purpose: z.string().max(4000).optional(),
  records: craftList.optional(),
  actions: craftList.optional(),
  roles: craftList.optional(),
  integrations: craftList.optional(),
  starter: z.string().max(120).optional(),
  afterLaunch: craftList.optional(),
  look: z.string().max(1200).optional(),
});
export type CraftDraft = z.infer<typeof craftDraftSchema>;

export const craftRequestSchema = z.object({
  /** The user's idea in their own words; Prometheus suggests answers from it. */
  idea: z.string().max(4000).optional(),
  answers: craftDraftSchema.default({}),
  /** Register the finished Blueprint in the user's Atlas (the user confirmed it). */
  register: z.boolean().default(false),
});
export type CraftRequest = z.infer<typeof craftRequestSchema>;

/** One line of the Blueprint's .bamf plan, for showing before the user confirms. */
export const craftStepSchema = z.object({
  index: z.number().int().positive(),
  stage: stageSchema,
  wave: z.number().int().positive(),
  layerKey: z.string().optional(),
  commandId: z.string(),
  line: z.string(),
});
export type CraftStep = z.infer<typeof craftStepSchema>;

export const craftResultSchema = z.object({
  /** True when every required answer is in and the Blueprint validated. */
  complete: z.boolean(),
  /** The question to ask now, when not complete. */
  next: craftQuestionSchema.optional(),
  /** Every unanswered or invalid question, in order. */
  remaining: z.array(craftQuestionSchema),
  issues: z.array(z.object({ field: z.string(), message: z.string() })),
  blueprint: blueprintSchema.optional(),
  steps: z.array(craftStepSchema).default([]),
  /** The .bamf v0.1 commands: craft, register (waits for the user), then the build plan. */
  bamf: z.array(z.looseObject({ version: z.string(), id: z.string(), action: z.string() })).default([]),
  /** Set when register was true and the Blueprint is now in the Atlas. */
  entry: atlasEntrySchema.optional(),
});
export type CraftResult = z.infer<typeof craftResultSchema>;
