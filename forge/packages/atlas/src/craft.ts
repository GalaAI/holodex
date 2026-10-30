import { bamfCommandV01Schema, describeCommand, planBlueprint, type BamfCommandV01 } from "@4ge/bamf";
import {
  BUILD_KINDS,
  CRAFT_FIELDS,
  PLATFORMS,
  blueprintSchema,
  craftAnswersSchema,
  craftQuestionSchema,
  type AgentId,
  type Blueprint,
  type BlueprintDependency,
  type BuildKind,
  type CraftAnswers,
  type CraftDraft,
  type CraftField,
  type CraftQuestion,
  type CraftStep,
  type LayerPlan,
  type OrchestrationJob,
  type Platform,
} from "@4ge/contracts";
import { inferKind, inferPlatforms } from "./match.js";
import { layer, nameParameters } from "./starters/define.js";
import { slugify } from "./template.js";

/**
 * Prometheus's Blueprint crafting method (SPEC flow step 3): when nothing in
 * the user's Atlas fits, Prometheus asks a short, fixed set of questions,
 * proposes an answer for each from the idea (the user confirms or changes
 * it), and turns the answers into a complete Blueprint. The Blueprint is
 * validated by blueprintSchema and expressed as .bamf v0.1 steps through
 * @4ge/bamf (planBlueprint), so the user sees exactly what the orchestra
 * will run before it joins their Atlas.
 *
 * Question set adapted from GalaAI/ark docs/architecture/SEED_SCROLL.md (the
 * user's framing, who it serves, how success shows) and the layer shape of
 * packages/atlas-core blueprints (records, roles, integrations, after-launch).
 */

const KIND_LABELS: Record<BuildKind, string> = {
  website: "Website",
  "web-app": "Web app",
  "mobile-app": "Mobile app",
  game: "Video game",
  erp: "ERP (stock, orders, invoices)",
  crm: "CRM (customers and sales)",
  "desktop-app": "Desktop app",
  other: "Something else",
};

const PLATFORM_LABELS: Record<Platform, string> = {
  web: "Web",
  ios: "iPhone and iPad",
  android: "Android",
  windows: "Windows",
  linux: "Linux",
  macos: "macOS",
};

/** The starter each kind scaffolds from when the user does not pick one. */
/**
 * The deploy layer every Blueprint uses to bring the app up in the sandbox;
 * test flights depend on it. The orchestra checks this layer by starting the
 * sandbox for real.
 */
export const SANDBOX_LAUNCH_KEY = "sandbox-launch";

export const STARTER_FOR_KIND: Record<BuildKind, string> = {
  website: "website",
  "web-app": "web-app",
  "mobile-app": "mobile-app",
  game: "game",
  erp: "records-app",
  crm: "records-app",
  "desktop-app": "desktop-app",
  other: "web-app",
};

const ROLE_SUGGESTIONS: Record<BuildKind, string[]> = {
  website: ["owner", "visitor"],
  "web-app": ["owner", "member"],
  "mobile-app": ["user"],
  game: ["player"],
  erp: ["owner", "staff", "customer"],
  crm: ["owner", "staff", "customer"],
  "desktop-app": ["user"],
  other: ["owner", "user"],
};

const AFTER_LAUNCH_SUGGESTIONS: Record<BuildKind, string[]> = {
  website: ["bookings", "weekly content", "social posts", "search check", "uptime"],
  "web-app": ["onboarding emails", "error watch", "weekly usage report"],
  "mobile-app": ["store reviews", "crash watch", "release notes"],
  game: ["devlog", "balance check", "uptime"],
  erp: ["reorder watch", "overdue invoices", "monthly report", "daily backups"],
  crm: ["follow-up digest", "weekly pipeline report", "daily backups"],
  "desktop-app": ["releases", "crash reports"],
  other: ["weekly report", "uptime"],
};

const QUESTIONS: Record<CraftField, Omit<CraftQuestion, "field" | "suggestion">> = {
  name: { question: "What should we call this kind of build?", why: "It names the Blueprint in your Atlas so you can reuse it.", input: "text", options: [], required: true },
  kind: {
    question: "Which is it closest to?",
    why: "It picks the starter, the stages and the test flights.",
    input: "choice",
    options: BUILD_KINDS.map((kind) => ({ value: kind, label: KIND_LABELS[kind] })),
    required: true,
  },
  platforms: {
    question: "Where should it run?",
    why: "It decides the starter and how Icarus tests and ships it.",
    input: "multi",
    options: PLATFORMS.map((platform) => ({ value: platform, label: PLATFORM_LABELS[platform] })),
    required: true,
  },
  purpose: { question: "In a sentence or two, what does it do and for whom?", why: "It becomes the Blueprint's description, in your words.", input: "text", options: [], required: true },
  records: { question: "What are the main things it keeps track of?", why: "Hephaestus builds the data model from these.", input: "list", options: [], required: true },
  actions: { question: "What are the main things people do in it?", why: "Smith builds a screen or flow for each one.", input: "list", options: [], required: true },
  roles: { question: "Who uses it?", why: "Each kind of user gets access rules and a test flight bot.", input: "list", options: [], required: true },
  integrations: { question: "Which outside accounts or services should it connect to?", why: "Athena wires these through your adapters.", input: "list", options: [], required: false },
  starter: { question: "Start from one of these starters?", why: "Starting from a working app is faster than starting from nothing.", input: "choice", options: [], required: false },
  afterLaunch: { question: "After launch, what should agents keep doing for it?", why: "Smith proposes this orchestration when the build is done.", input: "list", options: [], required: false },
  look: { question: "How should it look and feel?", why: "Smith starts the design system from this.", input: "text", options: [], required: false },
};

/** Integration words to the adapter, augment or app refs that cover them. */
const CONNECTION_CUES: Array<[RegExp, string[]]> = [
  [/\b(supabase|postgres|postgresql|database)\b/, ["adapter:supabase", "augment:supabase-mcp"]],
  [/\bvercel\b/, ["adapter:vercel", "augment:vercel-mcp"]],
  [/\b(cloudflare|dns|domain|cdn)\b/, ["adapter:cloudflare", "augment:cloudflare-mcp"]],
  [/\brailway\b/, ["adapter:railway", "augment:railway-mcp"]],
  [/\bnorthflank\b/, ["adapter:northflank"]],
  [/\b(gcp|google cloud|cloud run|firebase)\b/, ["adapter:gcp", "augment:gcloud-mcp"]],
  [/\b(docker|container)\b/, ["adapter:docker", "augment:docker-mcp-gateway"]],
  [/\b(tailscale|vpn|private network)\b/, ["adapter:tailscale"]],
  [/\b(nebius|nemotron|ai model|llm)\b/, ["adapter:nebius"]],
  [/\bhivemind\b/, ["adapter:hivemind"]],
  [/\b(ryozan|bookings?|appointments?|social|instagram|tiktok|facebook)\b/, ["app:ryozan"]],
];

export function connectionsFor(texts: readonly string[]): string[] {
  const found = new Set<string>();
  for (const text of texts) {
    const lower = text.toLowerCase();
    for (const [cue, refs] of CONNECTION_CUES) if (cue.test(lower)) for (const ref of refs) found.add(ref);
  }
  return [...found];
}

/* ------------------------------ parsing ------------------------------ */

const KIND_SYNONYMS: Record<string, BuildKind> = {
  site: "website",
  "web site": "website",
  "landing page": "website",
  "web app": "web-app",
  webapp: "web-app",
  saas: "web-app",
  mobile: "mobile-app",
  "mobile app": "mobile-app",
  "phone app": "mobile-app",
  "video game": "game",
  desktop: "desktop-app",
  "desktop app": "desktop-app",
};

const PLATFORM_SYNONYMS: Record<string, Platform> = {
  browser: "web",
  iphone: "ios",
  ipad: "ios",
  mac: "macos",
  "mac os": "macos",
  pc: "windows",
};

export function splitList(value: string | readonly string[] | undefined): string[] {
  const parts = typeof value === "string" ? value.split(/[\n,;]+/) : [...(value ?? [])];
  const seen = new Set<string>();
  const out: string[] = [];
  for (const part of parts) {
    const item = part.replace(/^\s*(?:[-*]|\d+[.)])\s*/, "").trim();
    if (item.length === 0 || seen.has(item.toLowerCase())) continue;
    seen.add(item.toLowerCase());
    out.push(item);
  }
  return out;
}

function parseKind(value: string): BuildKind | undefined {
  const text = value.trim().toLowerCase().replace(/-/g, " ");
  const direct = BUILD_KINDS.find((kind) => kind.replace(/-/g, " ") === text);
  return direct ?? KIND_SYNONYMS[text];
}

function parsePlatform(value: string): Platform | undefined {
  const text = value.trim().toLowerCase();
  return (PLATFORMS as readonly string[]).includes(text) ? (text as Platform) : PLATFORM_SYNONYMS[text];
}

export interface CraftIssue {
  field: string;
  message: string;
}

const isAnswered = (draft: CraftDraft, field: CraftField): boolean => {
  const value = draft[field];
  if (value === undefined) return false;
  if (typeof value === "string") return value.trim().length > 0;
  return value.length > 0;
};

/** Read a loose draft into typed answers, collecting a plain-words issue per bad field. */
export function readDraft(draft: CraftDraft, starters: readonly string[]): { candidate: Partial<CraftAnswers>; issues: CraftIssue[] } {
  const issues: CraftIssue[] = [];
  const candidate: Partial<CraftAnswers> = {};
  if (draft.name !== undefined) candidate.name = draft.name.trim();
  if (draft.purpose !== undefined) candidate.purpose = draft.purpose.trim();
  if (draft.look !== undefined) candidate.look = draft.look.trim();
  if (draft.kind !== undefined && draft.kind.trim()) {
    const kind = parseKind(draft.kind);
    if (kind) candidate.kind = kind;
    else issues.push({ field: "kind", message: `Pick one of: ${BUILD_KINDS.join(", ")}` });
  }
  if (draft.platforms !== undefined && draft.platforms.length > 0) {
    const parsed = draft.platforms.map((platform) => ({ platform, value: parsePlatform(platform) }));
    const unknown = parsed.filter((entry) => !entry.value).map((entry) => entry.platform);
    if (unknown.length > 0) issues.push({ field: "platforms", message: `Unknown platform ${unknown.join(", ")}; pick from ${PLATFORMS.join(", ")}` });
    candidate.platforms = [...new Set(parsed.flatMap((entry) => (entry.value ? [entry.value] : [])))];
  }
  for (const field of ["records", "actions", "roles", "integrations", "afterLaunch"] as const) {
    if (draft[field] !== undefined) candidate[field] = splitList(draft[field]);
  }
  if (draft.starter !== undefined && draft.starter.trim()) {
    const starter = draft.starter.trim().toLowerCase();
    if (starter === "none" || starters.includes(starter)) candidate.starter = starter;
    else issues.push({ field: "starter", message: `There is no starter called ${draft.starter}; pick one of: ${[...starters, "none"].join(", ")}` });
  }
  return { candidate, issues };
}

/* ----------------------------- questions ----------------------------- */

function suggestName(idea: string): string | undefined {
  const cleaned = idea
    .trim()
    .replace(/^(i\s+(?:want|need|would like)\s+(?:to\s+(?:build|make|create)\s+)?|(?:build|make|create)\s+(?:me\s+)?|please\s+)/i, "")
    .replace(/^(a|an|the)\s+/i, "")
    .replace(/[.!?]+$/, "");
  if (cleaned.length < 3) return undefined;
  const short = cleaned.length > 60 ? `${cleaned.slice(0, 60).replace(/\s+\S*$/, "")}` : cleaned;
  return short.charAt(0).toUpperCase() + short.slice(1);
}

/** Proposed answers from the idea. Prometheus offers them; nothing is applied until the user answers. */
export function suggestAnswers(idea: string | undefined, known: Partial<CraftAnswers> = {}): Partial<Record<CraftField, string | string[]>> {
  const text = (idea ?? "").trim();
  const kind = known.kind ?? (text ? inferKind(text) : undefined) ?? (text ? "other" : undefined);
  const out: Partial<Record<CraftField, string | string[]>> = {};
  const name = text ? suggestName(text) : undefined;
  if (name) out.name = name;
  if (kind) {
    out.kind = kind;
    out.platforms = known.platforms ?? inferPlatforms(text, kind);
    out.roles = ROLE_SUGGESTIONS[kind];
    out.starter = STARTER_FOR_KIND[kind];
    out.afterLaunch = AFTER_LAUNCH_SUGGESTIONS[kind];
  }
  if (text) out.purpose = text;
  const connections = connectionsFor([text]);
  if (connections.length > 0) out.integrations = connections.filter((ref) => ref.startsWith("adapter:") || ref.startsWith("app:")).map((ref) => ref.slice(ref.indexOf(":") + 1));
  return out;
}

export function craftQuestions(options: { idea?: string; starters?: readonly string[]; known?: Partial<CraftAnswers> } = {}): CraftQuestion[] {
  const starters = options.starters ?? [...new Set(Object.values(STARTER_FOR_KIND))];
  const suggestions = suggestAnswers(options.idea, options.known);
  return CRAFT_FIELDS.map((field) => {
    const base = QUESTIONS[field];
    const question = {
      field,
      ...base,
      options: field === "starter" ? [...starters.map((name) => ({ value: name, label: name })), { value: "none", label: "No starter" }] : base.options,
      ...(suggestions[field] !== undefined ? { suggestion: suggestions[field] } : {}),
    };
    return craftQuestionSchema.parse(question);
  });
}

/* ---------------------------- the Blueprint ---------------------------- */

function sentence(items: readonly string[]): string {
  if (items.length <= 1) return items[0] ?? "";
  return `${items.slice(0, -1).join(", ")} and ${items[items.length - 1]}`;
}

const SERVERLESS_KINDS = new Set<BuildKind>(["website", "game"]);

function agentFor(item: string): AgentId {
  const text = item.toLowerCase();
  if (/(uptime|deploy|backup|release|crash|monitor|error|health)/.test(text)) return "icarus";
  if (/(social|email|booking|notif|sync|reminder|digest|onboard|reorder|invoice|follow)/.test(text)) return "athena";
  if (/(content|blog|post|seo|search|report|research|review|devlog|summary)/.test(text)) return "metis";
  return "smith";
}

function cadenceFor(item: string): OrchestrationJob["cadence"] {
  const text = item.toLowerCase();
  if (/(daily|every day|each day|morning|nightly)/.test(text)) return "daily";
  if (/(monthly|month)/.test(text)) return "monthly";
  if (/(watch|uptime|monitor|continuous|always)/.test(text)) return "continuous";
  if (/(when|each new|every new|on new|booking|onboard|release)/.test(text)) return "on-event";
  return "weekly";
}

function testFlightLayers(roles: readonly string[], actions: readonly string[]): LayerPlan[] {
  const picked = roles.slice(0, 2);
  const used = new Set<string>();
  return picked.map((role, index) => {
    let key = `test-flight-${slugify(role, `role-${index + 1}`)}`.slice(0, 80);
    while (used.has(key)) key = `${key}-${index + 1}`.slice(0, 80);
    used.add(key);
    return layer(
      key,
      "test-flight",
      "icarus",
      `Test flight: ${role}`,
      `A ${role} bot does what a ${role} does in {{appName}}: ${sentence(actions.slice(0, 4))}.`,
      [`Every ${role} step completes`, "Errors along the way show plain words"],
      { dependsOn: [SANDBOX_LAUNCH_KEY] },
    );
  });
}

/** Turn complete answers into a Blueprint. Throws a ZodError when the result does not validate. */
export function blueprintFromAnswers(input: CraftAnswers, options: { blueprintId?: string; now?: Date } = {}): Blueprint {
  const answers = craftAnswersSchema.parse(input);
  const slug = slugify(answers.name, "crafted");
  const roles = answers.roles;
  const needsAccounts = roles.length > 1 || ["web-app", "crm", "erp"].includes(answers.kind);
  const needsServer = !SERVERLESS_KINDS.has(answers.kind) || answers.integrations.length > 0;
  const connections = connectionsFor([...answers.integrations, ...answers.afterLaunch, answers.purpose]);
  const adapterRefs = connections.filter((ref) => ref.startsWith("adapter:"));
  if (answers.kind === "website" && !connections.includes("app:ryozan") && /\b(book|appointment|tattoo|artist|studio|salon)/i.test(answers.purpose)) {
    connections.push("app:ryozan");
  }
  const suggestedConnections = [...new Set(["mod:launch", ...connections])];

  const architecture: LayerPlan[] = [
    layer(
      "research-brief",
      "research",
      "metis",
      "Research brief",
      `Who {{appName}} serves (${sentence(roles)}), what it does, and two or three comparable products to learn from.`,
      ["The brief names the audience and the jobs they hire it for", "Every reference has a source"],
      { deliverables: ["docs/brief.md"] },
    ),
    layer(
      "data-model",
      "data",
      "hephaestus",
      "Data model",
      `Records for ${sentence(answers.records)}, with owners, timestamps and the links between them.`,
      ["Every record type has an owner and timestamps", "Storage or migrations run twice without error"],
      { dependsOn: ["research-brief"] },
    ),
  ];
  if (needsAccounts) {
    architecture.push(
      layer(
        "access",
        "backend",
        "hephaestus",
        "Accounts and access",
        `Sign in and what each of ${sentence(roles)} may see and change.`,
        ["Each role is refused what it cannot do, in plain words", "Passwords are hashed and sessions expire"],
        { dependsOn: ["data-model"] },
      ),
    );
  }
  if (needsServer) {
    architecture.push(
      layer(
        "api",
        "backend",
        "hephaestus",
        "API",
        `Create, read, update and delete for ${sentence(answers.records)}, with one plain-words error shape.`,
        ["Every route has a test", "Bad input is refused in plain words"],
        { dependsOn: [needsAccounts ? "access" : "data-model"] },
      ),
    );
  }
  const dataSource = needsServer ? "api" : "data-model";

  const orchestrationLayers: LayerPlan[] = [
    layer(
      "data-flow",
      "orchestration",
      "athena",
      "Data flow",
      `Connect each action (${sentence(answers.actions.slice(0, 6))}) to the data it reads and writes, with loading, empty and error states.`,
      ["Every action shows a plain-words error when it fails"],
      { dependsOn: [dataSource] },
    ),
  ];
  if (answers.integrations.length > 0) {
    orchestrationLayers.push(
      layer(
        "integrations",
        "integrations",
        "athena",
        "Integrations",
        `Connect ${sentence(answers.integrations)} through the user's own adapters; anything not connected says so plainly.`,
        ["Each integration works when its adapter is connected", "Each one says it is not connected when it is not"],
        { dependsOn: [dataSource] },
      ),
    );
  }

  const interfaceLayers: LayerPlan[] = [
    layer(
      "design-system",
      "design-system",
      "smith",
      "Design system",
      answers.look ? `Tokens and components for {{appName}}: ${answers.look}` : "Tokens and components for {{appName}}, starting from the 4GE kit.",
      ["Text and controls meet WCAG AA contrast", "Every color comes from a token"],
    ),
    layer(
      "screens",
      "interface",
      "smith",
      "Screens",
      `A screen or flow for each thing people do: ${sentence(answers.actions)}.`,
      ["Keyboard and touch reach every control", "Each screen has an empty state"],
      { dependsOn: ["design-system", "data-flow"] },
    ),
    layer("copy", "content", "metis", "Copy and media", "Labels, help, empty states and any images, in plain words.", ["No placeholder text is left"], { dependsOn: ["research-brief"] }),
  ];

  const flights = testFlightLayers(roles, answers.actions);
  const deployLayers: LayerPlan[] = [
    layer(SANDBOX_LAUNCH_KEY, "deploy", "icarus", "Sandbox launch", "Build {{appName}} and run it in the sandbox on the app port.", ["The build passes", "The app answers in the sandbox"], { dependsOn: ["screens"] }),
    ...flights,
    layer(
      "go-live",
      "deploy",
      "icarus",
      "Go live",
      "Ship to the user's own host or store through the Launch Mod once every test flight is green.",
      ["The live app answers", "Every test flight is green first"],
      { dependsOn: flights.map((flight) => flight.key) },
    ),
  ];

  const dependencies: BlueprintDependency[] = answers.integrations.map((integration) => {
    const refs = connectionsFor([integration]).filter((ref) => ref.startsWith("adapter:"));
    return refs[0]
      ? { name: refs[0], kind: "adapter" as const, scope: "runtime" as const, required: false, role: `Connects ${integration}` }
      : { name: integration.slice(0, 200), kind: "service" as const, scope: "runtime" as const, required: false, role: `Connects ${integration}` };
  });
  const uniqueDependencies = dependencies.filter((dependency, index) => dependencies.findIndex((other) => other.name === dependency.name) === index);

  const usedKeys = new Set<string>();
  const orchestration: OrchestrationJob[] = answers.afterLaunch.map((item, index) => {
    let key = slugify(item, `job-${index + 1}`).slice(0, 80);
    while (usedKeys.has(key) || key.length < 2) key = `${key}-${index + 1}`;
    usedKeys.add(key);
    const agent = agentFor(item);
    const jobConnections = connectionsFor([item]).filter((ref) => !ref.startsWith("augment:"));
    if (agent === "icarus") jobConnections.push("mod:launch");
    return {
      key,
      title: item.charAt(0).toUpperCase() + item.slice(1),
      agent,
      cadence: cadenceFor(item),
      description: `Keeps ${item} going for {{appName}} after launch, and asks before anything goes out.`,
      connections: [...new Set(jobConnections)],
    };
  });

  const starter = answers.starter && answers.starter !== "none" ? answers.starter : undefined;
  const at = (options.now ?? new Date()).toISOString();
  const tagline = answers.purpose.split(/(?<=[.!?])\s/)[0]?.slice(0, 240) ?? answers.purpose.slice(0, 240);

  return blueprintSchema.parse({
    blueprintId: options.blueprintId ?? `blueprint:${slug}`,
    name: answers.name,
    version: "0.1.0",
    state: "draft",
    origin: "crafted",
    kind: answers.kind,
    platforms: answers.platforms,
    description: `${answers.purpose} It keeps track of ${sentence(answers.records)}; people can ${sentence(answers.actions)}.`.slice(0, 2000),
    ...(starter ? { starter } : {}),
    parameters: nameParameters(tagline),
    dependencies: uniqueDependencies,
    stages: [
      { stage: "architecture", goal: "Know who it serves, then define its records and rules.", layers: architecture, checkpoint: { title: "Model", question: "Do the records and roles match how it will be used?" } },
      { stage: "orchestration", goal: "Wire the data and the outside services.", layers: orchestrationLayers, checkpoint: { title: "Wiring", question: "Which services should it connect to now, and which later?" } },
      { stage: "interface", goal: "Agree on the look, then build the screens.", layers: interfaceLayers, checkpoint: { title: "Screens", question: "Walk the screens: what should change?" } },
      { stage: "deploy", goal: "Test it in the sandbox as each kind of user, then ship.", layers: deployLayers, checkpoint: { title: "Launch", question: "Test flights are green. Ship it now?" } },
    ],
    suggestedConnections,
    orchestration,
    orchestrationSuggestions: orchestration.map((job) => `${job.title} (${job.agent}, ${job.cadence})`),
    designSystem: {
      name: `${answers.name} look`.slice(0, 160),
      source: "generated",
      summary: answers.look || "Dark Phosphor surfaces with one accent color, from the 4GE kit.",
      principles: ["One primary action per screen", "Plain words where people act", "Every list has an empty state"],
    },
    sourceRefs: ["Crafted with Prometheus in 4GE Imagistation", ...(starter ? [`forge/starters/${starter}`] : [])],
    tags: [answers.kind, ...answers.records, ...answers.roles].map((tag) => tag.toLowerCase().slice(0, 60)).filter((tag, index, all) => all.indexOf(tag) === index).slice(0, 30),
    createdAt: at,
    updatedAt: at,
  });
}

/* ------------------------------ the method ----------------------------- */

export interface CraftOptions {
  idea?: string;
  /** Starter names on disk; the starter question offers these. */
  starters?: readonly string[];
  /** Ids already in the Atlas, so a crafted Blueprint gets a free one. */
  takenIds?: ReadonlySet<string>;
  now?: Date;
}

export interface CraftOutcome {
  complete: boolean;
  next?: CraftQuestion;
  remaining: CraftQuestion[];
  issues: CraftIssue[];
  blueprint?: Blueprint;
  steps: CraftStep[];
  bamf: BamfCommandV01[];
}

/** A Blueprint id not already in the Atlas: blueprint:<slug>, then -2, -3, ... */
export function freeBlueprintId(slug: string, taken: ReadonlySet<string>): string {
  const base = `blueprint:${slug}`.slice(0, 130);
  if (!taken.has(base)) return base;
  for (let suffix = 2; suffix < 1000; suffix += 1) {
    const candidate = `${base}-${suffix}`;
    if (!taken.has(candidate)) return candidate;
  }
  return `${base}-${Date.now().toString(36)}`;
}

/** The .bamf commands for crafting: validate the draft, register it (waits for the user), then the build plan. */
export function craftCommands(blueprint: Blueprint): { commands: BamfCommandV01[]; steps: CraftStep[] } {
  const slug = blueprint.blueprintId.replace(/^blueprint:/, "");
  const plan = planBlueprint(blueprint);
  const head = [
    bamfCommandV01Schema.parse({
      version: "0.1",
      id: `${slug}.craft`.slice(0, 120),
      intent: "validate",
      targetLayer: "atlas",
      target: blueprint.blueprintId,
      action: "blueprint.craft",
      args: { name: blueprint.name, kind: blueprint.kind, platforms: blueprint.platforms, stages: blueprint.stages.length },
      risk: "low",
      metadata: { source: "agent:prometheus" },
    }),
    bamfCommandV01Schema.parse({
      version: "0.1",
      id: `${slug}.register`.slice(0, 120),
      intent: "execute",
      targetLayer: "atlas",
      target: blueprint.blueprintId,
      action: "blueprint.register",
      args: { version: blueprint.version, origin: blueprint.origin },
      risk: "low",
      operatorGate: { required: true, reason: "The user confirms the Blueprint before it joins their Atlas" },
      metadata: { source: "agent:prometheus" },
    }),
  ];
  const steps: CraftStep[] = plan.steps.map((step) => ({
    index: step.index,
    stage: step.stage,
    wave: step.wave,
    ...(step.layerKey ? { layerKey: step.layerKey } : {}),
    commandId: step.command.id,
    line: describeCommand(step.command),
  }));
  return { commands: [...head, ...plan.steps.map((step) => step.command)], steps };
}

/**
 * One turn of the method. Give the idea and the answers so far; get back the
 * next question (with Prometheus's suggestion), any issues, and once every
 * required answer is in, the validated Blueprint with its .bamf plan.
 */
export function craftBlueprint(draft: CraftDraft, options: CraftOptions = {}): CraftOutcome {
  const starters = options.starters ?? [...new Set(Object.values(STARTER_FOR_KIND))];
  const { candidate, issues } = readDraft(draft, starters);
  const questions = craftQuestions({ ...(options.idea !== undefined ? { idea: options.idea } : {}), starters, known: candidate });
  const issueFields = new Set(issues.map((issue) => issue.field));

  // Check each answered field on its own, so one bad answer does not hide the rest.
  const shape = craftAnswersSchema.shape;
  for (const field of CRAFT_FIELDS) {
    if (!isAnswered(draft, field) || issueFields.has(field)) continue;
    const checked = shape[field].safeParse(candidate[field]);
    if (!checked.success) {
      issues.push({ field, message: checked.error.issues[0]?.message ?? `Check the ${field} answer` });
      issueFields.add(field);
    }
  }

  const remaining = questions.filter((question) => !isAnswered(draft, question.field) || issueFields.has(question.field));
  // Required and unanswered, or answered but invalid: these block the Blueprint.
  const blocking = remaining.filter((question) => question.required || issueFields.has(question.field));
  const next = blocking[0] ?? remaining[0];

  if (blocking.length > 0) {
    return { complete: false, ...(next ? { next } : {}), remaining, issues, steps: [], bamf: [] };
  }

  const answers = craftAnswersSchema.parse({
    ...candidate,
    starter: candidate.starter ?? (candidate.kind ? STARTER_FOR_KIND[candidate.kind] : "none"),
  });
  const slug = slugify(answers.name, "crafted");
  const blueprintId = freeBlueprintId(slug, options.takenIds ?? new Set());
  const blueprint = blueprintFromAnswers(answers, { blueprintId, ...(options.now ? { now: options.now } : {}) });
  const { commands, steps } = craftCommands(blueprint);
  return {
    complete: true,
    ...(next ? { next } : {}),
    remaining,
    issues,
    blueprint,
    steps,
    bamf: commands,
  };
}
