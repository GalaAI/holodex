import { cpSync, existsSync, readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { buildKindSchema, platformSchema, relativePathSchema } from "@4ge/contracts";
import { z } from "zod";
import { slugify, substitute } from "./template.js";

/**
 * Starters: small, real app templates in forge/starters/<name> that a
 * Blueprint scaffolds from. Each starter is a working app with default
 * values (it installs and builds as is); its starter.json says which JSON
 * fields and which literal strings to set from the build's values, so a
 * scaffolded app carries the user's name without any placeholder ever
 * reaching code. JSON fields are set structurally and HTML values are
 * escaped, so a name like Ink & Iron's is safe everywhere.
 */
export const STARTERS_ENV = "FORGE_STARTERS_DIR";
export const STARTER_MANIFEST = "starter.json";

const SKIP = new Set(["node_modules", "dist", "release", ".git", ".turbo", ".vite", ".expo", "coverage", STARTER_MANIFEST]);

export const starterManifestSchema = z.object({
  name: z.string().regex(/^[a-z0-9][a-z0-9-]*$/),
  title: z.string().min(1).max(160),
  description: z.string().min(1).max(1000),
  kind: buildKindSchema,
  platforms: z.array(platformSchema).min(1),
  install: z.string().min(1).max(200),
  build: z.string().min(1).max(200),
  dev: z.string().min(1).max(200).optional(),
  test: z.string().min(1).max(200).optional(),
  output: z.string().min(1).max(200),
  placeholders: z.array(z.object({ key: z.string().regex(/^[a-z][a-zA-Z0-9]*$/), description: z.string(), default: z.string() })).min(1),
  fields: z.array(z.object({ file: relativePathSchema, path: z.string().min(1).max(200), value: z.string().max(400) })).default([]),
  replace: z
    .array(z.object({ file: relativePathSchema, find: z.string().min(1).max(400), value: z.string().max(400), escape: z.enum(["none", "html"]).default("none") }))
    .default([]),
  sourceRefs: z.array(z.string()).default([]),
});
export type StarterManifest = z.infer<typeof starterManifestSchema>;

export class ScaffoldError extends Error {
  readonly code: "not_found" | "conflict" | "bad_request";
  constructor(code: ScaffoldError["code"], message: string) {
    super(message);
    this.name = "ScaffoldError";
    this.code = code;
  }
}

/** forge/starters, or FORGE_STARTERS_DIR. src/ and dist/ both sit three levels under forge/. */
export function startersDir(env: Readonly<Record<string, string | undefined>> = process.env): string {
  const override = env[STARTERS_ENV];
  if (override && override.trim()) return resolve(override);
  return resolve(dirname(fileURLToPath(import.meta.url)), "..", "..", "..", "starters");
}

export function readStarterManifest(dir: string, name: string): StarterManifest {
  if (!/^[a-z0-9][a-z0-9-]*$/.test(name)) throw new ScaffoldError("bad_request", `${name} is not a starter name`);
  const file = join(dir, name, STARTER_MANIFEST);
  if (!existsSync(file)) throw new ScaffoldError("not_found", `There is no starter called ${name}`);
  const manifest = starterManifestSchema.parse(JSON.parse(readFileSync(file, "utf8")));
  if (manifest.name !== name) throw new ScaffoldError("bad_request", `The starter in ${name} calls itself ${manifest.name}`);
  return manifest;
}

/** Every starter with a valid manifest; broken ones are reported, not thrown. */
export function listStarterManifests(dir: string = startersDir()): { starters: StarterManifest[]; problems: Array<{ name: string; message: string }> } {
  const starters: StarterManifest[] = [];
  const problems: Array<{ name: string; message: string }> = [];
  let names: string[] = [];
  try {
    names = readdirSync(dir).filter((name) => !name.startsWith(".") && statSync(join(dir, name)).isDirectory());
  } catch {
    return { starters, problems: [{ name: dir, message: "The starters folder is missing" }] };
  }
  for (const name of names.sort()) {
    try {
      starters.push(readStarterManifest(dir, name));
    } catch (error) {
      problems.push({ name, message: error instanceof Error ? error.message : String(error) });
    }
  }
  return { starters, problems };
}

/** The values a scaffold uses: manifest defaults, then given values; appSlug follows appName. */
export function starterValues(manifest: StarterManifest, given: Readonly<Record<string, string>> = {}): Record<string, string> {
  const values: Record<string, string> = Object.fromEntries(manifest.placeholders.map((placeholder) => [placeholder.key, placeholder.default]));
  const declared = new Set(manifest.placeholders.map((placeholder) => placeholder.key));
  for (const [key, value] of Object.entries(given)) {
    if (declared.has(key) && value.trim().length > 0) values[key] = value.trim();
  }
  if (declared.has("appSlug")) {
    const slugGiven = given.appSlug?.trim();
    const fromName = given.appName?.trim() ? slugify(given.appName) : undefined;
    values.appSlug = slugify(slugGiven || fromName || values.appSlug || "app");
  }
  return values;
}

const escapeHtml = (text: string) =>
  text.replace(/[&<>"']/g, (char) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[char] as string);

function setPath(target: Record<string, unknown>, path: string, value: string): boolean {
  const parts = path.split(".");
  let node: Record<string, unknown> = target;
  for (const part of parts.slice(0, -1)) {
    const next = node[part];
    if (!next || typeof next !== "object" || Array.isArray(next)) return false;
    node = next as Record<string, unknown>;
  }
  const last = parts[parts.length - 1] as string;
  if (!(last in node)) return false;
  node[last] = value;
  return true;
}

export interface ScaffoldResult {
  starter: StarterManifest;
  target: string;
  values: Record<string, string>;
  /** Files copied, relative to the target, forward slashes. */
  files: string[];
  /** Edits the manifest asked for that did not apply (a moved field, a changed string). */
  warnings: string[];
}

export interface ScaffoldOptions {
  starter: string;
  /** Folder to create; it must not exist or must be empty. */
  target: string;
  values?: Readonly<Record<string, string>>;
  startersDir?: string;
}

function listFiles(root: string, base = root): string[] {
  const out: string[] = [];
  for (const name of readdirSync(root)) {
    const path = join(root, name);
    if (statSync(path).isDirectory()) out.push(...listFiles(path, base));
    else out.push(path.slice(base.length + 1).replace(/\\/g, "/"));
  }
  return out.sort();
}

/** Copy a starter into `target` and set the build's values in it. */
export function scaffoldStarter(options: ScaffoldOptions): ScaffoldResult {
  const dir = options.startersDir ?? startersDir();
  const manifest = readStarterManifest(dir, options.starter);
  const target = resolve(options.target);
  if (existsSync(target) && readdirSync(target).length > 0) {
    throw new ScaffoldError("conflict", `${target} already has files; scaffold into an empty folder`);
  }
  const source = join(dir, manifest.name);
  cpSync(source, target, {
    recursive: true,
    filter: (path) => !SKIP.has(path.split(/[\\/]/).pop() ?? ""),
  });

  const values = starterValues(manifest, options.values);
  const warnings: string[] = [];

  const jsonFiles = new Map<string, Record<string, unknown>>();
  for (const field of manifest.fields) {
    const file = join(target, field.file);
    if (!jsonFiles.has(field.file)) {
      if (!existsSync(file)) {
        warnings.push(`${field.file} is missing, so ${field.path} was not set`);
        continue;
      }
      jsonFiles.set(field.file, JSON.parse(readFileSync(file, "utf8")) as Record<string, unknown>);
    }
    const document = jsonFiles.get(field.file) as Record<string, unknown>;
    if (!setPath(document, field.path, substitute(field.value, values))) warnings.push(`${field.file} has no ${field.path} to set`);
  }
  for (const [file, document] of jsonFiles) writeFileSync(join(target, file), `${JSON.stringify(document, null, 2)}\n`, "utf8");

  for (const edit of manifest.replace) {
    const file = join(target, edit.file);
    if (!existsSync(file)) {
      warnings.push(`${edit.file} is missing, so its text was not set`);
      continue;
    }
    const text = readFileSync(file, "utf8");
    if (!text.includes(edit.find)) {
      warnings.push(`${edit.file} no longer contains the text to replace`);
      continue;
    }
    const value = substitute(edit.value, values, edit.escape === "html" ? escapeHtml : (plain) => plain);
    writeFileSync(file, text.split(edit.find).join(value), "utf8");
  }

  return { starter: manifest, target, values, files: listFiles(target), warnings };
}
