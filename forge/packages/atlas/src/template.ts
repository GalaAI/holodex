import { blueprintSchema, type Blueprint } from "@4ge/contracts";

/**
 * {{param}} placeholders. Adapted from GalaAI/ark
 * packages/atlas-core/src/blueprint.ts (substitute, substituteDeep and the
 * instantiateBlueprint checks): only declared keys are filled, unknown keys
 * stay visible instead of being guessed, appName is required by every
 * starter, and the output must still satisfy the Blueprint schema.
 */
export const PLACEHOLDER_PATTERN = /\{\{([a-zA-Z][a-zA-Z0-9]*)\}\}/g;

export function substitute(value: string, values: Readonly<Record<string, string>>, escape: (text: string) => string = (text) => text): string {
  return value.replace(PLACEHOLDER_PATTERN, (match, key: string) =>
    Object.hasOwn(values, key) ? escape(values[key] as string) : match,
  );
}

export function substituteDeep<T>(input: T, values: Readonly<Record<string, string>>, skipKeys: ReadonlySet<string> = new Set()): T {
  if (typeof input === "string") return substitute(input, values) as T;
  if (Array.isArray(input)) return input.map((item) => substituteDeep(item, values, skipKeys)) as T;
  if (input && typeof input === "object") {
    const out: Record<string, unknown> = {};
    for (const [key, value] of Object.entries(input as Record<string, unknown>)) {
      out[key] = skipKeys.has(key) ? value : substituteDeep(value, values, skipKeys);
    }
    return out as T;
  }
  return input;
}

export function placeholdersIn(input: unknown): string[] {
  const found = new Set<string>();
  const text = JSON.stringify(input);
  for (const match of text.matchAll(PLACEHOLDER_PATTERN)) found.add(match[1] as string);
  return [...found];
}

export interface InstantiationIssue {
  field: string;
  message: string;
}

export interface InstantiationResult {
  ok: boolean;
  blueprint?: Blueprint;
  values: Record<string, string>;
  issues: InstantiationIssue[];
}

/** Lowercase kebab-case slug from any text, for package names and ids. */
export function slugify(text: string, fallback = "app"): string {
  const slug = text
    .normalize("NFKD")
    .replace(/[̀-ͯ]/g, "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 60)
    .replace(/-+$/g, "");
  return slug.length > 0 ? slug : fallback;
}

/**
 * Fill a Blueprint's parameters for one build. Defaults apply first, then
 * the given values; only declared keys are accepted; appSlug is derived from
 * appName when not given. The result is parsed again, so a value that breaks
 * the schema is reported, not shipped.
 */
export function instantiateBlueprint(input: Blueprint, given: Readonly<Record<string, string>> = {}): InstantiationResult {
  const parsed = blueprintSchema.safeParse(input);
  if (!parsed.success) {
    return {
      ok: false,
      values: {},
      issues: parsed.error.issues.map((issue) => ({ field: issue.path.join(".") || "blueprint", message: issue.message })),
    };
  }
  const blueprint = parsed.data;
  const issues: InstantiationIssue[] = [];
  const declared = new Set(blueprint.parameters.map((parameter) => parameter.key));
  if (blueprint.state === "deprecated") {
    issues.push({ field: "state", message: `${blueprint.name} is deprecated; pick a newer Blueprint` });
  }
  for (const key of Object.keys(given)) {
    if (!declared.has(key) && key !== "appSlug") {
      issues.push({ field: `parameters.${key}`, message: `'${key}' is not a parameter of ${blueprint.name}` });
    }
  }
  const values: Record<string, string> = {};
  for (const parameter of blueprint.parameters) {
    if (parameter.defaultValue !== undefined) values[parameter.key] = parameter.defaultValue;
  }
  for (const [key, value] of Object.entries(given)) {
    if ((declared.has(key) || key === "appSlug") && value.trim().length > 0) values[key] = value.trim();
  }
  if (values.appName && !values.appSlug) values.appSlug = slugify(values.appName);
  for (const parameter of blueprint.parameters) {
    if (parameter.required && !values[parameter.key]) {
      issues.push({ field: `parameters.${parameter.key}`, message: `${parameter.key} needs a value: ${parameter.description}` });
    }
  }
  if (issues.length > 0) return { ok: false, values, issues };

  const filled = blueprintSchema.safeParse(substituteDeep(blueprint, values, new Set(["blueprintId", "parameters"])));
  if (!filled.success) {
    return {
      ok: false,
      values,
      issues: filled.error.issues.map((issue) => ({ field: issue.path.join(".") || "blueprint", message: issue.message })),
    };
  }
  const left = placeholdersIn({ ...filled.data, parameters: [] });
  if (left.length > 0) {
    return { ok: false, values, issues: [{ field: "blueprint", message: `unfilled placeholders: ${left.join(", ")}` }] };
  }
  return { ok: true, blueprint: filled.data, values, issues: [] };
}
