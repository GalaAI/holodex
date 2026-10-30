import { execFile } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { promisify } from "node:util";
import {
  relativePathSchema,
  type Blueprint,
  type LayerPlan,
  type ModelUsage,
  type ReceiptCheck,
  type Seed,
  type StagePlan,
} from "@4ge/contracts";
import { resolveInBuild, type BuildPaths } from "@4ge/contracts/node";
import { AGENT_DEFINITIONS } from "./agents.js";
import type { ModelClient } from "./model.js";
import { block, extractJson, fileBlocks } from "./parse.js";
import { clip, plainText } from "./util.js";

const run = promisify(execFile);

/**
 * One layer's work: the layer's agent reads the build so far and writes
 * real files into dataRoot()/builds/<id>/app; then the orchestra checks the
 * result itself (files written, deliverables present, JSON and JavaScript
 * that parse, every Blueprint check reported and passed). Problems go back
 * to the agent on the next try; the receipt records what was verified.
 */

export interface LayerJob {
  blueprint: Blueprint;
  seed: Seed;
  stagePlan: StagePlan;
  plan: LayerPlan;
  paths: BuildPaths;
  /** The .bamf envelope this step runs, as JSON. */
  command?: unknown;
  /** The person's revise note and the problems from earlier tries. */
  feedback: string[];
  attempt: number;
  signal: AbortSignal;
}

export interface LayerOutcome {
  summary: string;
  files: string[];
  checks: ReceiptCheck[];
  /** Empty when the layer is good. */
  problems: string[];
  usage?: ModelUsage;
  model?: string;
}

const SKIP_DIRS = new Set(["node_modules", "dist", ".git", ".next", "coverage", ".turbo"]);

export function listAppFiles(appDir: string, limit = 300): string[] {
  const out: string[] = [];
  const walk = (dir: string) => {
    if (out.length >= limit) return;
    let names: string[] = [];
    try {
      names = readdirSync(dir).sort();
    } catch {
      return;
    }
    for (const name of names) {
      if (out.length >= limit) return;
      if (SKIP_DIRS.has(name)) continue;
      const full = join(dir, name);
      let isDir = false;
      try {
        isDir = statSync(full).isDirectory();
      } catch {
        continue;
      }
      if (isDir) walk(full);
      else out.push(relative(appDir, full).replace(/\\/g, "/"));
    }
  };
  walk(appDir);
  return out;
}

/** Deliverables that name a file (a path with an extension), as opposed to a named output. */
export function fileDeliverables(plan: LayerPlan): string[] {
  return plan.deliverables
    .map((item) => item.trim().replace(/\\/g, "/").replace(/^\.\//, ""))
    .filter((item) => !/\s/.test(item) && /\.[A-Za-z0-9]{1,8}$/.test(item) && relativePathSchema.safeParse(item).success);
}

function readSome(appDir: string, files: string[], budget: number): string {
  const parts: string[] = [];
  let used = 0;
  for (const file of files) {
    if (used >= budget) break;
    const full = join(appDir, file);
    if (!existsSync(full)) continue;
    try {
      const text = readFileSync(full, "utf8");
      const piece = clip(text, Math.min(6000, budget - used));
      parts.push(`<<<EXISTING ${file}>>>\n${piece}\n<<<END EXISTING>>>`);
      used += piece.length;
    } catch {
      // unreadable files are listed but not shown
    }
  }
  return parts.join("\n\n");
}

function context(job: LayerJob): string {
  const appFiles = listAppFiles(job.paths.app);
  const keyFiles = appFiles.filter((file) => /^(package\.json|readme\.md|index\.html|src\/(index|main|app|server)\.[a-z]+)$/i.test(file));
  const dependencyDeliverables = job.blueprint.stages
    .flatMap((stage) => stage.layers)
    .filter((layer) => job.plan.dependsOn.includes(layer.key))
    .flatMap((layer) => fileDeliverables(layer));
  const ownDeliverables = fileDeliverables(job.plan).filter((file) => appFiles.includes(file));
  const shown = [...new Set([...ownDeliverables, ...dependencyDeliverables, ...keyFiles])];
  return [
    `Build: ${job.seed.title} (${job.blueprint.name}, ${job.blueprint.kind} for ${job.blueprint.platforms.join(", ")})`,
    `The idea, in the person's words: ${job.seed.idea}`,
    job.seed.problem ? `Problem: ${job.seed.problem}` : "",
    job.seed.audience ? `Who it serves: ${job.seed.audience}` : "",
    job.seed.features.length ? `Must have: ${job.seed.features.join("; ")}` : "",
    job.seed.constraints.length ? `Constraints: ${job.seed.constraints.join("; ")}` : "",
    `Blueprint dependencies: ${job.blueprint.dependencies.map((dependency) => `${dependency.name} (${dependency.role})`).join("; ") || "none listed"}`,
    "",
    `Stage: ${job.stagePlan.stage}. Goal: ${job.stagePlan.goal}`,
    `Your layer: ${job.plan.title} [${job.plan.kind}]`,
    job.plan.description,
    `Deliverables: ${job.plan.deliverables.join("; ") || "your choice of files"}`,
    `Checks your files must pass:\n${job.plan.checks.map((check, index) => `${index + 1}. ${check}`).join("\n")}`,
    job.command ? `\nThe .bamf step you are running:\n${JSON.stringify(job.command)}` : "",
    "",
    `Files already in the app (${appFiles.length}${appFiles.length >= 300 ? "+" : ""}):\n${appFiles.join("\n") || "(empty)"}`,
    "",
    readSome(job.paths.app, shown, 24_000),
    job.feedback.length ? `\nFix these before anything else:\n${job.feedback.map((item) => `- ${item}`).join("\n")}` : "",
  ]
    .filter((line) => line !== "")
    .join("\n");
}

const OUTPUT_FORMAT = `Answer in exactly this format and nothing else:

<<<SUMMARY>>>
Two or three plain sentences: what you built and how it connects to the rest.
<<<END SUMMARY>>>
<<<FILE relative/path/from/app/root.ext>>>
the complete file content
<<<END FILE>>>
(one FILE block per file you create or replace; write whole files, never diffs or excerpts)
<<<CHECKS>>>
[{"name": "<each check, exactly as listed>", "passed": true or false, "detail": "which file and what in it meets the check"}]
<<<END CHECKS>>>

Rules: paths are relative to the app root, use forward slashes, no leading slash, no "..". Keep the files you write focused on your layer. The app's tsconfig.json, vite config and lockfile come from its starter and are kept as they are: make your source files build with them, following the import style the existing files already use. Never write secrets; read them from environment variables.`;

const checksSchema = (value: unknown): Array<{ name: string; passed: boolean; detail?: string }> => {
  if (!Array.isArray(value)) return [];
  return value
    .filter((item): item is Record<string, unknown> => !!item && typeof item === "object")
    .map((item) => ({
      name: String(item.name ?? "").trim(),
      passed: item.passed === true,
      ...(typeof item.detail === "string" ? { detail: item.detail } : {}),
    }))
    .filter((item) => item.name.length > 0);
};

/**
 * The compiler, bundler and lockfile setup a starter ships at the app root.
 * Seen live: agents "fixing" a build rewrote tsconfig.json with settings the
 * starter's TypeScript rejects, so the sandbox build broke on every try. A
 * layer may replace one only when its deliverables name it.
 */
const STARTER_TOOLING = /^(?:tsconfig(?:\.[\w-]+)?\.json|vite\.config\.[cm]?[jt]s|pnpm-lock\.yaml|package-lock\.json|yarn\.lock)$/;
export const isStarterTooling = (path: string): boolean => STARTER_TOOLING.test(path);

const normalize = (text: string) => text.toLowerCase().replace(/[^a-z0-9]+/g, " ").trim();

/** Run the layer's agent once and verify what it wrote. */
export async function runLayerAgent(models: ModelClient, job: LayerJob): Promise<LayerOutcome> {
  const agent = AGENT_DEFINITIONS[job.plan.agent];
  const result = await models.chat({
    tier: agent.profile.tier,
    purpose: `layer:${job.plan.key}`,
    messages: [
      { role: "system", content: `${agent.system}\n\n${OUTPUT_FORMAT}` },
      { role: "user", content: context(job) },
    ],
    maxTokens: 16_000,
    temperature: 0.2,
    signal: job.signal,
  });
  const problems: string[] = [];
  const summary = plainText(block(result.text, "SUMMARY") ?? "").slice(0, 2000);
  const blocks = fileBlocks(result.text);
  const written: string[] = [];
  const kept: string[] = [];
  for (const file of blocks) {
    const checked = relativePathSchema.safeParse(file.path);
    if (!checked.success) {
      problems.push(`The path ${file.path} is not relative to the app root.`);
      continue;
    }
    if (file.content.trim().length === 0) {
      problems.push(`${file.path} was empty.`);
      continue;
    }
    const target = resolveInBuild(job.paths, checked.data);
    if (isStarterTooling(checked.data) && existsSync(target) && !job.plan.deliverables.includes(checked.data)) {
      kept.push(checked.data);
      continue;
    }
    mkdirSync(dirname(target), { recursive: true });
    writeFileSync(target, file.content, "utf8");
    written.push(checked.data);
  }
  if (written.length === 0) problems.push("No files were written. Answer with FILE blocks holding complete files.");

  const checks: ReceiptCheck[] = [];
  checks.push({ name: "Files written", passed: written.length > 0, detail: `${written.length} file(s): ${clip(written.join(", "), 800)}` });
  if (kept.length > 0) {
    checks.push({
      name: "Starter build setup kept",
      passed: true,
      detail: `Not replaced: ${kept.join(", ")}. Source files are fixed to build with the starter's settings.`,
    });
  }

  const missing = fileDeliverables(job.plan).filter((file) => !existsSync(resolveInBuild(job.paths, file)));
  checks.push({
    name: "Deliverables present",
    passed: missing.length === 0,
    detail: missing.length === 0 ? "Every file deliverable exists in the app" : `Missing: ${missing.join(", ")}`,
  });
  if (missing.length > 0) problems.push(`These deliverables are missing: ${missing.join(", ")}.`);

  const syntax: string[] = [];
  for (const file of written) {
    const full = resolveInBuild(job.paths, file);
    if (/\.json$/i.test(file)) {
      try {
        JSON.parse(readFileSync(full, "utf8"));
      } catch (error) {
        syntax.push(`${file} is not valid JSON (${error instanceof Error ? error.message.slice(0, 120) : "parse error"})`);
      }
    } else if (/\.(c|m)?js$/i.test(file)) {
      try {
        await run(process.execPath, ["--check", full], { timeout: 15_000, windowsHide: true });
      } catch (error) {
        const stderr = (error as { stderr?: string }).stderr ?? "";
        syntax.push(`${file} does not parse: ${clip(stderr.split(/\r?\n/).find((line) => /Error/.test(line)) ?? "syntax error", 200)}`);
      }
    } else if (/\.html?$/i.test(file)) {
      const html = readFileSync(full, "utf8");
      if (!/<(html|body|main|div)[\s>]/i.test(html)) syntax.push(`${file} has no HTML document structure`);
    }
  }
  checks.push({
    name: "Files parse",
    passed: syntax.length === 0,
    detail: syntax.length === 0 ? "JSON, JavaScript and HTML files parse" : clip(syntax.join("; "), 900),
  });
  problems.push(...syntax);

  let reported: Array<{ name: string; passed: boolean; detail?: string }> = [];
  try {
    reported = checksSchema(extractJson(block(result.text, "CHECKS") ?? "[]"));
  } catch {
    reported = [];
  }
  for (const check of job.plan.checks) {
    const wanted = normalize(check);
    const found = reported.find((item) => {
      const name = normalize(item.name);
      return name === wanted || name.includes(wanted) || wanted.includes(name);
    });
    if (!found) {
      checks.push({ name: clip(check, 160), passed: false, detail: "The agent did not report this check" });
      problems.push(`Report the check "${check}" in the CHECKS block, with the file that meets it.`);
    } else {
      checks.push({
        name: clip(check, 160),
        passed: found.passed,
        detail: clip(`Reported by ${agent.profile.name}: ${found.detail ?? (found.passed ? "met" : "not met")}`, 1000),
      });
      if (!found.passed) problems.push(`The check "${check}" is not met yet: ${found.detail ?? "no detail"}.`);
    }
  }

  return {
    summary: summary || `${agent.profile.name} wrote ${written.length} file(s) for ${job.plan.title}.`,
    files: written,
    checks,
    problems,
    usage: result.usage,
    model: result.model,
  };
}
