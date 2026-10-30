import {
  blueprintSchema,
  type AgentId,
  type Blueprint,
  type BlueprintInput,
  type BlueprintParameter,
  type LayerKind,
  type LayerPlan,
} from "@4ge/contracts";

/**
 * Small helpers for writing starter Blueprints. Every starter is parsed by
 * blueprintSchema at module load, so a broken starter fails the test run,
 * never a user's build.
 */
export function defineBlueprint(input: BlueprintInput): Blueprint {
  const orchestration = input.orchestration ?? [];
  return blueprintSchema.parse({
    ...input,
    orchestrationSuggestions:
      input.orchestrationSuggestions ??
      orchestration.map((job) => `${job.title} (${job.agent}, ${job.cadence}): ${job.description}`.slice(0, 400)),
  });
}

export interface LayerOptions {
  dependsOn?: string[];
  deliverables?: string[];
}

export function layer(
  key: string,
  kind: LayerKind,
  agent: AgentId,
  title: string,
  description: string,
  checks: string[],
  options: LayerOptions = {},
): LayerPlan {
  return {
    key,
    kind,
    agent,
    title,
    description,
    checks,
    dependsOn: options.dependsOn ?? [],
    deliverables: options.deliverables ?? [],
  };
}

/** The two parameters every starter Blueprint takes. */
export function nameParameters(taglineDefault: string): BlueprintParameter[] {
  return [
    { key: "appName", description: "What the build is called, as people will see it", required: true },
    { key: "appTagline", description: "One line that says what it is for", required: false, defaultValue: taglineDefault },
  ];
}

/** Starters ship reviewed; a crafted Blueprint starts as a draft. */
export const STARTER_STATE = "promoted" as const;
