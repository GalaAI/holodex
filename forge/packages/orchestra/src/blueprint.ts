import { craftBlueprint as craftWithAtlas, craftQuestions, suggestAnswers } from "@4ge/atlas";
import { BUILD_KINDS, PLATFORMS, blueprintSchema, craftDraftSchema, type Blueprint, type CraftDraft, type Seed } from "@4ge/contracts";
import { z } from "zod";
import { AGENT_DEFINITIONS } from "./agents.js";
import { askJson } from "./ask.js";
import type { ModelClient } from "./model.js";

/**
 * Blueprint helpers for the orchestra: fill {{param}} placeholders from the
 * Seed, ask the model for parameter values, and craft a new Blueprint with
 * the Atlas crafting method when nothing in their Atlas fits (SPEC flow
 * step 3).
 */

/** Replace {{key}} in every string of the Blueprint. Unknown keys stay visible so nothing is silently guessed. */
export function applyParameters(blueprint: Blueprint, values: Record<string, string>): Blueprint {
  const merged: Record<string, string> = {};
  for (const parameter of blueprint.parameters) {
    const value = values[parameter.key] ?? parameter.defaultValue;
    if (value !== undefined && value.trim().length > 0) merged[parameter.key] = value.trim();
  }
  const walk = (node: unknown): unknown => {
    if (typeof node === "string") {
      return node.replace(/\{\{\s*([a-zA-Z][a-zA-Z0-9]*)\s*\}\}/g, (whole, key: string) => merged[key] ?? whole);
    }
    if (Array.isArray(node)) return node.map(walk);
    if (node && typeof node === "object") {
      return Object.fromEntries(Object.entries(node).map(([key, value]) => [key, key === "blueprintId" ? value : walk(value)]));
    }
    return node;
  };
  return blueprintSchema.parse(walk(blueprint));
}

/** Parameters the Seed still lacks (required, no value, no default). */
export function missingParameters(blueprint: Blueprint, values: Record<string, string>): string[] {
  return blueprint.parameters
    .filter((parameter) => parameter.required && !values[parameter.key]?.trim() && !parameter.defaultValue)
    .map((parameter) => parameter.key);
}

function seedBrief(seed: Pick<Seed, "title" | "idea" | "problem" | "audience" | "successSignal" | "kind" | "platforms" | "features" | "constraints" | "answers">): string {
  return JSON.stringify(
    {
      title: seed.title,
      idea: seed.idea,
      problem: seed.problem,
      audience: seed.audience,
      successSignal: seed.successSignal,
      kind: seed.kind,
      platforms: seed.platforms,
      features: seed.features,
      constraints: seed.constraints,
      answers: seed.answers,
    },
    null,
    2,
  );
}

/** Ask the model to fill the Blueprint's parameters from the Seed. Values it cannot ground stay empty. */
export async function fillParameters(
  models: ModelClient,
  blueprint: Blueprint,
  seed: Seed,
  signal?: AbortSignal,
): Promise<Record<string, string>> {
  if (blueprint.parameters.length === 0) return {};
  const keys = blueprint.parameters.map((parameter) => parameter.key);
  const shape = z.record(z.string(), z.union([z.string(), z.number(), z.boolean(), z.null()]));
  const { value } = await askJson(models, {
    tier: "lead",
    purpose: "blueprint.parameters",
    system: `${AGENT_DEFINITIONS.prometheus.system}\n\nYou fill Blueprint parameters from a Seed Scroll. Use only what the person said. When the Seed does not say, use an empty string. Answer with one JSON object and nothing else.`,
    messages: [
      {
        role: "user",
        content: `Blueprint parameters:\n${JSON.stringify(blueprint.parameters, null, 2)}\n\nSeed Scroll:\n${seedBrief(seed)}\n\nAnswer with {${keys.map((key) => `"${key}": "..."`).join(", ")}}.`,
      },
    ],
    schema: shape,
    maxTokens: 1200,
    ...(signal ? { signal } : {}),
  });
  const out: Record<string, string> = {};
  for (const key of keys) {
    const raw = value[key];
    if (raw !== null && raw !== undefined && String(raw).trim().length > 0) out[key] = String(raw).trim().slice(0, 400);
  }
  return out;
}

/**
 * Craft a Blueprint for a Seed nothing in the Atlas fits, with the Atlas's
 * own crafting method (@4ge/atlas craftBlueprint): Prometheus answers the
 * method's questions from the Seed, unanswered ones take the method's own
 * suggestions from the idea, and the method builds and validates the
 * Blueprint and its .bamf plan. An answer the method refuses goes back to
 * the model with the method's plain-words issue.
 */
export async function craftBlueprint(
  models: ModelClient,
  seed: Seed,
  options: { starters?: string[]; takenIds?: ReadonlySet<string>; signal?: AbortSignal | undefined } = {},
): Promise<Blueprint> {
  const starters = options.starters?.length ? options.starters : undefined;
  const questions = craftQuestions({ idea: seed.idea, ...(starters ? { starters } : {}) });
  const schema = craftDraftSchema.transform((draft, context) => {
    // Fill what Prometheus left open with the method's suggestions from the idea.
    const suggested = suggestAnswers(seed.idea, { kind: seed.kind, platforms: seed.platforms });
    const merged: Record<string, unknown> = { ...draft };
    for (const [field, value] of Object.entries(suggested)) {
      const current = merged[field];
      const empty =
        current === undefined || (typeof current === "string" ? !current.trim() : Array.isArray(current) && current.length === 0);
      if (empty && value !== undefined) merged[field] = value;
    }
    const outcome = craftWithAtlas(merged as CraftDraft, {
      idea: seed.idea,
      ...(starters ? { starters } : {}),
      ...(options.takenIds ? { takenIds: options.takenIds } : {}),
    });
    if (!outcome.complete || !outcome.blueprint) {
      for (const issue of outcome.issues.slice(0, 8)) context.addIssue({ code: "custom", path: [issue.field], message: issue.message });
      for (const question of outcome.remaining.filter((item) => item.required).slice(0, 8)) {
        context.addIssue({ code: "custom", path: [question.field], message: `Missing answer: ${question.question}` });
      }
      if (outcome.issues.length === 0 && outcome.remaining.every((item) => !item.required)) {
        context.addIssue({ code: "custom", path: [], message: "The answers did not make a complete Blueprint" });
      }
      return z.NEVER;
    }
    return blueprintSchema.parse({ ...outcome.blueprint, sourceRefs: [...outcome.blueprint.sourceRefs, `seed:${seed.id}`].slice(0, 40) });
  });
  const questionText = questions
    .map(
      (question) =>
        `- ${question.field}${question.required ? " (required)" : ""}: ${question.question}${
          question.options.length ? ` Options: ${question.options.map((option) => option.value).join(", ")}.` : ""
        }`,
    )
    .join("\n");
  const { value } = await askJson(models, {
    tier: "lead",
    purpose: "blueprint.craft",
    system: `${AGENT_DEFINITIONS.prometheus.system}\n\nNothing in the person's Atlas fits this Seed, so you craft a Blueprint with the Atlas crafting method. Answer the method's questions from the Seed Scroll only; do not invent what the person did not say (leave a field out and the method proposes it from the idea). Lists are arrays of short items. Kinds of build: ${BUILD_KINDS.join(", ")}. Platforms: ${PLATFORMS.join(", ")}.\nAnswer with one JSON object whose keys are the field names below, and nothing else.\n${questionText}`,
    messages: [{ role: "user", content: `Seed Scroll:\n${seedBrief(seed)}` }],
    schema,
    maxTokens: 2400,
    attempts: 3,
    ...(options.signal ? { signal: options.signal } : {}),
  });
  return value;
}
