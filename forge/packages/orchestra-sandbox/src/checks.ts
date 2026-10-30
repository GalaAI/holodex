import { existsSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import {
  makeId,
  relativePathSchema,
  testStepSchema,
  type Build,
  type Layer,
  type LayerKind,
  type LayerPlan,
  type Receipt,
  type ReceiptCheck,
  type TestFlight,
  type TestStep,
} from "@4ge/contracts";
import { z } from "zod";

/**
 * Icarus test flight checks. A layer is green only when every check that
 * applies to it passed. The four automated checks come from one sandbox run
 * and the bots' results:
 *
 *   build          the app was copied, prepared (install and build when it has them) and answered its health check
 *   routes         every page the bots opened answered below 400 and no same-origin request failed
 *   ui-flows       every step the staff and customer bots took passed
 *   accessibility  every page had a title, a language, alt text, labels, named controls and an h1
 *
 * Which of them apply depends on the layer kind, plus a deliverables check
 * (each path the layer promised exists in the app folder). The Blueprint's
 * own check sentences are matched to these by their words; a sentence no
 * automated check covers is left to the checkpoint and never claimed passed.
 */

export const CHECK_CATEGORIES = ["build", "routes", "ui-flows", "accessibility"] as const;
export type CheckCategory = (typeof CHECK_CATEGORIES)[number];

export const CHECK_LABELS: Record<CheckCategory, string> = {
  build: "Sandbox build and launch",
  routes: "Routes answer below 400",
  "ui-flows": "Staff and customer flows",
  accessibility: "Accessibility basics",
};

export const LAYER_KIND_CHECKS: Record<LayerKind, CheckCategory[]> = {
  research: ["build"],
  data: ["build"],
  backend: ["build", "routes"],
  orchestration: ["build", "routes"],
  integrations: ["build", "routes"],
  "design-system": ["build", "accessibility"],
  interface: ["build", "routes", "ui-flows", "accessibility"],
  content: ["build", "routes", "accessibility"],
  deploy: ["build", "routes", "ui-flows"],
  "test-flight": ["build", "routes", "ui-flows", "accessibility"],
};

/* ----------------------------- bot results ----------------------------- */

const botStepSchema = z.object({
  id: z.string(),
  personaId: z.string(),
  kind: z.string(),
  action: z.string(),
  expectation: z.string(),
  status: z.enum(["pending", "running", "passed", "failed", "skipped"]),
  screenshot: z.string().optional(),
  error: z.string().optional(),
  at: z.string().optional(),
});

const botPageSchema = z.object({
  path: z.string(),
  status: z.number().int().nullable(),
  title: z.string().default(""),
  problems: z.array(z.string()).default([]),
});

export const botResultsSchema = z.object({
  schemaVersion: z.literal(1),
  flightId: z.string(),
  startedAt: z.string(),
  finishedAt: z.string(),
  personas: z.array(
    z.object({
      personaId: z.string(),
      role: z.string(),
      entryPath: z.string(),
      steps: z.array(botStepSchema),
      pages: z.array(botPageSchema),
      consoleErrors: z.array(z.string()).default([]),
      pageErrors: z.array(z.string()).default([]),
      failedRequests: z.array(z.object({ url: z.string(), status: z.number() })).default([]),
    }),
  ),
  artifacts: z
    .array(z.object({ kind: z.string(), path: z.string(), sha256: z.string(), sizeBytes: z.number() }))
    .default([]),
  issues: z.array(z.string()).default([]),
});
export type BotResults = z.infer<typeof botResultsSchema>;

/* ------------------------------ evaluation ------------------------------ */

export interface CategoryResult {
  passed: boolean;
  detail: string;
}

export interface FlightEvaluationInput {
  flightId: string;
  /** The sandbox reached "running": seed, install, build and health check all passed. */
  appReady: boolean;
  results: BotResults | null;
  /** Why the bots produced no results, when they did not. */
  botsFailure?: string;
  build?: Build | undefined;
  /** Layer plans by key, from the Blueprint, for deliverables and check sentences. */
  plans?: ReadonlyMap<string, LayerPlan>;
  /** The build's app folder, for the deliverables check. */
  appDir: string;
  /** Evidence folder relative to the build root, e.g. evidence/tfl_x. */
  evidenceRel: string;
  now?: () => string;
  newReceiptToken?: () => string;
}

export interface LayerEvaluation {
  layerId: string;
  status: "passed" | "failed" | "not-covered";
  checks: ReceiptCheck[];
  /** Blueprint check sentences no automated check covers. */
  notAutomated: string[];
  receipt?: Receipt;
}

export interface FlightEvaluation {
  categories: Record<CheckCategory, CategoryResult>;
  steps: TestStep[];
  layers: LayerEvaluation[];
  status: Extract<TestFlight["status"], "passed" | "failed">;
  summary: string;
}

const clip = (text: string, max: number) => (text.length > max ? `${text.slice(0, max - 3)}...` : text);

export function evaluateCategories(input: Pick<FlightEvaluationInput, "appReady" | "results" | "botsFailure">): Record<CheckCategory, CategoryResult> {
  const { results } = input;
  const build: CategoryResult = input.appReady
    ? { passed: true, detail: "The app was copied into the sandbox, prepared (install and build when it has them) and answered its health check" }
    : { passed: false, detail: "The sandbox was not running the app when the flight began" };
  if (!results) {
    const why = input.botsFailure ?? "The bots produced no results";
    return {
      build,
      routes: { passed: false, detail: why },
      "ui-flows": { passed: false, detail: why },
      accessibility: { passed: false, detail: why },
    };
  }
  const pages = results.personas.flatMap((persona) => persona.pages);
  const badPages = pages.filter((page) => page.status !== null && page.status >= 400);
  const failedRequests = results.personas.flatMap((persona) => persona.failedRequests);
  const routes: CategoryResult =
    pages.length === 0
      ? { passed: false, detail: "No page was opened, so no route was checked" }
      : badPages.length === 0 && failedRequests.length === 0
        ? { passed: true, detail: `${new Set(pages.map((page) => page.path)).size} route(s) answered below 400` }
        : {
            passed: false,
            detail: clip(
              [
                ...badPages.map((page) => `${page.path} answered ${page.status}`),
                ...failedRequests.map((request) => `${request.url} answered ${request.status}`),
              ].join("; "),
              900,
            ),
          };
  const flowSteps = results.personas.flatMap((persona) => persona.steps.filter((step) => step.kind !== "accessibility"));
  const failedFlow = flowSteps.filter((step) => step.status !== "passed");
  const perPersona = results.personas
    .map((persona) => {
      const steps = persona.steps.filter((step) => step.kind !== "accessibility");
      return `${persona.personaId} ${steps.filter((step) => step.status === "passed").length}/${steps.length}`;
    })
    .join(", ");
  const uiFlows: CategoryResult =
    flowSteps.length === 0
      ? { passed: false, detail: "The bots took no steps" }
      : failedFlow.length === 0
        ? { passed: true, detail: `Every step passed (${perPersona})` }
        : {
            passed: false,
            detail: clip(
              `${failedFlow.length} step(s) failed (${perPersona}): ${failedFlow
                .map((step) => `${step.action}: ${step.error ?? step.status}`)
                .join("; ")}`,
              900,
            ),
          };
  const a11ySteps = results.personas.flatMap((persona) => persona.steps.filter((step) => step.kind === "accessibility"));
  const a11yProblems = [...new Set(pages.flatMap((page) => page.problems.map((problem) => `${page.path}: ${problem}`)))];
  const accessibility: CategoryResult =
    a11ySteps.length === 0
      ? { passed: false, detail: "The accessibility check did not run" }
      : a11yProblems.length === 0 && a11ySteps.every((step) => step.status === "passed")
        ? { passed: true, detail: `${new Set(pages.map((page) => page.path)).size} page(s) passed the basics` }
        : { passed: false, detail: clip(a11yProblems.join("; ") || "The accessibility step failed", 900) };
  return { build, routes, "ui-flows": uiFlows, accessibility };
}

const CATEGORY_WORDS: Array<[CheckCategory, RegExp]> = [
  ["accessibility", /\b(a11y|accessib\w*|alt text|contrast|aria|screen reader|keyboard|label(s|led)?)\b/i],
  ["routes", /\b(route|endpoint|api|http|status|links?|page(s)? (loads?|opens?)|404|200|respond\w*|health)\b/i],
  ["ui-flows", /\b(click\w*|flow|form|book\w*|submit\w*|persona|user can|customer|staff|navigat\w*|sign ?up|log ?in|checkout|send)\b/i],
  ["build", /\b(build\w*|compil\w*|install\w*|start\w*|launch\w*|run(s|ning)?|lint\w*|typecheck\w*|bundl\w*)\b/i],
];

/** Which automated check covers a Blueprint check sentence, if any. */
export function categoryFor(sentence: string): CheckCategory | null {
  for (const [category, pattern] of CATEGORY_WORDS) {
    if (pattern.test(sentence)) return category;
  }
  return null;
}

/** Deliverables that name a file or folder (not a named output like "booking API"). */
export function pathDeliverables(deliverables: readonly string[]): string[] {
  return deliverables
    .map((entry) => entry.trim().replace(/\\/g, "/").replace(/^\.\//, ""))
    .filter((entry) => !entry.includes("{{") && !/\s/.test(entry) && (entry.includes("/") || /\.[a-z0-9]{1,8}$/i.test(entry)))
    .filter((entry) => relativePathSchema.safeParse(entry).success);
}

function fallbackArtifacts(appDir: string): string[] {
  for (const name of ["index.html", "package.json", "dist/index.html", "public/index.html"]) {
    if (existsSync(join(appDir, name))) return [name];
  }
  if (!existsSync(appDir)) return [];
  const first = readdirSync(appDir).find((name) => {
    try {
      return statSync(join(appDir, name)).isFile();
    } catch {
      return false;
    }
  });
  return first ? [first] : [];
}

function evaluateLayer(
  layer: Layer,
  categories: Record<CheckCategory, CategoryResult>,
  input: FlightEvaluationInput,
): LayerEvaluation {
  if (layer.status === "pending" || layer.status === "running" || layer.status === "cancelled") {
    return {
      layerId: layer.id,
      status: "not-covered",
      checks: [],
      notAutomated: [],
    };
  }
  const plan = input.plans?.get(layer.key);
  const checks: ReceiptCheck[] = LAYER_KIND_CHECKS[layer.kind].map((category) => ({
    name: CHECK_LABELS[category],
    passed: categories[category].passed,
    detail: categories[category].detail,
  }));
  const deliverables = pathDeliverables(plan?.deliverables ?? []);
  const present = deliverables.filter((path) => existsSync(join(input.appDir, path)));
  if (deliverables.length > 0) {
    const missing = deliverables.filter((path) => !present.includes(path));
    checks.push({
      name: "Deliverables present",
      passed: missing.length === 0,
      detail: missing.length === 0 ? `${present.length} file(s) found in the app folder` : clip(`Missing: ${missing.join(", ")}`, 900),
    });
  }
  const notAutomated: string[] = [];
  for (const sentence of plan?.checks ?? []) {
    const category = categoryFor(sentence);
    if (!category) {
      notAutomated.push(sentence);
      continue;
    }
    checks.push({
      name: clip(sentence, 160),
      passed: categories[category].passed,
      detail: clip(`Covered by "${CHECK_LABELS[category]}": ${categories[category].detail}`, 1000),
    });
  }
  const status = checks.every((check) => check.passed) ? "passed" : "failed";
  const artifacts = present.length > 0 ? present : fallbackArtifacts(input.appDir);
  const now = input.now ?? (() => new Date().toISOString());
  const token = input.newReceiptToken ?? (() => Math.random().toString(36).slice(2, 12));
  const receipt: Receipt | undefined =
    artifacts.length > 0
      ? {
          id: makeId("receipt", token()),
          buildId: layer.buildId,
          stage: layer.stage,
          layerId: layer.id,
          agentId: "icarus",
          artifacts: artifacts.slice(0, 200),
          checks: [
            ...checks,
            {
              name: "Test flight evidence",
              passed: true,
              detail: clip(`Screenshots and results in ${input.evidenceRel} (flight ${input.flightId})`, 1000),
            },
          ].slice(0, 80),
          recordedAt: now(),
        }
      : undefined;
  return { layerId: layer.id, status, checks, notAutomated, ...(receipt ? { receipt } : {}) };
}

/** Bot steps as contract TestSteps, screenshots made build-relative. */
export function toTestSteps(results: BotResults | null, evidenceRel: string, build?: Build): TestStep[] {
  if (!results) return [];
  const uiLayer = build?.layers.find((layer) => layer.kind === "interface")?.id;
  const steps: TestStep[] = [];
  for (const persona of results.personas) {
    for (const step of persona.steps) {
      const parsed = testStepSchema.safeParse({
        id: clip(step.id, 80),
        personaId: clip(step.personaId, 80),
        action: clip(step.action, 1000),
        expectation: clip(step.expectation, 1000),
        status: step.status === "running" ? "failed" : step.status,
        ...(step.screenshot ? { screenshot: clip(`${evidenceRel}/${step.screenshot}`, 400) } : {}),
        ...(step.error ? { error: clip(step.error, 2000) } : {}),
        ...(uiLayer ? { layerId: uiLayer } : {}),
        ...(step.at ? { at: step.at } : {}),
      });
      if (parsed.success) steps.push(parsed.data);
    }
  }
  return steps;
}

export function evaluateFlight(input: FlightEvaluationInput): FlightEvaluation {
  const categories = evaluateCategories(input);
  const steps = toTestSteps(input.results, input.evidenceRel, input.build);
  const layers = (input.build?.layers ?? []).map((layer) => evaluateLayer(layer, categories, input));
  const categoriesPassed = CHECK_CATEGORIES.every((category) => categories[category].passed);
  const failedLayers = layers.filter((layer) => layer.status === "failed");
  const notCovered = layers.filter((layer) => layer.status === "not-covered");
  const status = categoriesPassed && failedLayers.length === 0 ? "passed" : "failed";
  const passedSteps = steps.filter((step) => step.status === "passed").length;
  const parts = [
    status === "passed" ? "Test flight green." : "Test flight red.",
    `${passedSteps} of ${steps.length} step(s) passed.`,
    ...CHECK_CATEGORIES.map((category) => `${CHECK_LABELS[category]}: ${categories[category].passed ? "passed" : `failed (${clip(categories[category].detail, 300)})`}.`),
  ];
  if (layers.length > 0) {
    parts.push(
      `${layers.filter((layer) => layer.status === "passed").length} of ${layers.length} layer(s) green${
        notCovered.length > 0 ? `, ${notCovered.length} not built yet so not flown` : ""
      }.`,
    );
  }
  const manual = layers.flatMap((layer) => layer.notAutomated);
  if (manual.length > 0) parts.push(`${manual.length} Blueprint check(s) have no automated test and are left to the checkpoint.`);
  return { categories, steps, layers, status, summary: clip(parts.join(" "), 4000) };
}
