import { spawn } from "node:child_process";
import { existsSync, statSync } from "node:fs";
import { mkdir, readFile, readdir, rename, writeFile } from "node:fs/promises";
import { delimiter, dirname, join, relative, sep } from "node:path";
import {
  annotationDraftSchema,
  annotationSchema,
  appliedTweakSchema,
  componentCandidateSchema,
  componentInventoryItemSchema,
  designSystemSchema,
  makeId,
  tweakOpSchema,
  type Annotation,
  type AppliedTweak,
  type ComponentCandidate,
  type ComponentInventoryItem,
  type DesignSystem,
  type TweakOp,
} from "@4ge/contracts";
import { resolveInBuild, type BuildPaths } from "@4ge/contracts/node";
import {
  DesignInputError,
  MEDIA_TAGS,
  SOURCE_EXTENSIONS,
  TOKENS_BLOCK_ID,
  TWEAK_SHEET_NAME,
  backgroundAssetCss,
  componentNameFromPath,
  ensureImport,
  ensureStylesheetLink,
  filterCandidates,
  findElements,
  guessCategory,
  insertAfter,
  jsxProps,
  kitCandidates,
  registryInstallCommand,
  registrySnapshotCandidates,
  relativeSpecifier,
  removeBlock,
  removeElement,
  replaceElement,
  replaceText,
  revertPatch,
  setAttribute,
  targetAttr,
  tokensToCss,
  tweakToCss,
  upsertBlock,
  type ElementSpan,
  type Patch,
} from "@4ge/kit/design";
import { nanoid } from "nanoid";
import { z } from "zod";
import type { ServerContext } from "../context.js";
import { HttpError, badRequest, notFound } from "../errors.js";
import { implement } from "../implement.js";
import type { LayerRegistrar } from "../registry.js";

/**
 * Design layer (/design/*): design systems, the component inventory and its
 * candidates, tweaks and annotations for one build.
 *
 * Records live in dataRoot()/builds/<id>/design as JSON. Tweaks are real
 * edits to the build's source in builds/<id>/app:
 *
 * - CSS tweaks (color, placement, layout, animation, motion, transition) and
 *   the design system's tokens go into one managed sheet, 4ge-tweaks.css,
 *   linked from the app's HTML entry just before </body> so its rules win.
 *   Each tweak owns one marked block; undo removes that block only.
 * - Source tweaks (text, asset, component) edit the element named by its
 *   data-4ge-id (or id) in the app's source files, and keep an undo patch in
 *   design/undo/<tweakId>.json. Undo refuses, in plain words, when the edited
 *   region changed since.
 *
 * The pure compilers live in @4ge/kit/design so the studio shares them.
 */

/* ------------------------------------------------------------------ */
/* Storage                                                            */
/* ------------------------------------------------------------------ */

const FILES = {
  systems: "systems.json",
  inventory: "inventory.json",
  tweaks: "tweaks.json",
  annotations: "annotations.json",
} as const;

const undoRecordSchema = z.object({
  tweakId: z.string(),
  /** Set for CSS tweaks: the sheet (build-relative) and the block id to remove. */
  sheet: z.string().optional(),
  patches: z.array(z.object({ file: z.string(), before: z.string(), after: z.string() })).default([]),
});
type UndoRecord = z.infer<typeof undoRecordSchema>;

/** One write queue per build, so two tweaks never interleave on the same files. */
const queues = new Map<string, Promise<unknown>>();
function serialize<T>(key: string, task: () => Promise<T>): Promise<T> {
  const previous = queues.get(key) ?? Promise.resolve();
  const next = previous.then(task, task);
  queues.set(
    key,
    next.catch(() => undefined),
  );
  return next;
}

async function writeAtomic(path: string, content: string): Promise<void> {
  await mkdir(dirname(path), { recursive: true });
  const temp = `${path}.${nanoid(6)}.tmp`;
  await writeFile(temp, content, "utf8");
  await rename(temp, path);
}

async function readJson<T>(path: string, schema: z.ZodType<T>, fallback: T): Promise<T> {
  let raw: string;
  try {
    raw = await readFile(path, "utf8");
  } catch {
    return fallback;
  }
  try {
    return schema.parse(JSON.parse(raw));
  } catch {
    throw new HttpError("internal", `The design record ${path.split(sep).slice(-2).join("/")} is damaged and could not be read`, {
      hint: "Fix or remove that file, then try again.",
    });
  }
}

function buildPaths(ctx: ServerContext, buildId: string): BuildPaths {
  const paths = ctx.paths.build(buildId);
  if (!existsSync(paths.root)) throw notFound(`Build ${buildId}`);
  return paths;
}

function requireApp(paths: BuildPaths, buildId: string): string {
  if (!existsSync(paths.app) || !statSync(paths.app).isDirectory()) {
    throw new HttpError("conflict", `Build ${buildId} has no app source yet, so there is nothing to tweak`, {
      hint: "Wait for the interface layer to write the app, then try again.",
    });
  }
  return paths.app;
}

const store = {
  systems: (p: BuildPaths) => readJson(join(p.design, FILES.systems), z.array(designSystemSchema), []),
  inventory: (p: BuildPaths) => readJson(join(p.design, FILES.inventory), z.array(componentInventoryItemSchema), []),
  tweaks: (p: BuildPaths) => readJson(join(p.design, FILES.tweaks), z.array(appliedTweakSchema), []),
  annotations: (p: BuildPaths) => readJson(join(p.design, FILES.annotations), z.array(annotationSchema), []),
  save: (p: BuildPaths, file: keyof typeof FILES, records: unknown[]) =>
    writeAtomic(join(p.design, FILES[file]), `${JSON.stringify(records, null, 2)}\n`),
  undo: (p: BuildPaths, tweakId: string) =>
    readJson<UndoRecord | null>(join(p.design, "undo", `${tweakId}.json`), undoRecordSchema.nullable(), null),
  saveUndo: (p: BuildPaths, record: UndoRecord) =>
    writeAtomic(join(p.design, "undo", `${record.tweakId}.json`), `${JSON.stringify(record, null, 2)}\n`),
};

/** Find which build holds a record by id (tweaks and annotations are addressed by id alone). */
async function findOwner(
  ctx: ServerContext,
  file: "tweaks" | "annotations",
  id: string,
): Promise<{ buildId: string; paths: BuildPaths }> {
  let entries: string[] = [];
  try {
    entries = await readdir(ctx.paths.builds);
  } catch {
    entries = [];
  }
  for (const buildId of entries) {
    if (!buildId.startsWith("bld_")) continue;
    let paths: BuildPaths;
    try {
      paths = ctx.paths.build(buildId);
    } catch {
      continue;
    }
    const records: Array<{ id: string }> = await store[file](paths);
    if (records.some((record) => record.id === id)) return { buildId, paths };
  }
  throw notFound(file === "tweaks" ? `Tweak ${id}` : `Annotation ${id}`);
}

const now = () => new Date().toISOString();
const toPosix = (path: string) => path.split(sep).join("/");

function log(ctx: ServerContext, buildId: string, message: string, level: "info" | "warn" = "info") {
  ctx.events.publish({ type: "log", buildId, data: { level, message: message.slice(0, 4000) } });
}

/* ------------------------------------------------------------------ */
/* The build's files                                                  */
/* ------------------------------------------------------------------ */

const SKIP_DIRS = new Set(["node_modules", ".git", "dist", "build", ".next", "out", "coverage", ".turbo", ".vite", ".cache"]);
const MAX_FILES = 5000;
const MAX_FILE_BYTES = 1_000_000;

async function sourceFiles(app: string): Promise<string[]> {
  const found: string[] = [];
  const walk = async (dir: string) => {
    if (found.length >= MAX_FILES) return;
    let entries;
    try {
      entries = await readdir(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const entry of entries) {
      if (found.length >= MAX_FILES) return;
      const full = join(dir, entry.name);
      if (entry.isDirectory()) {
        if (!SKIP_DIRS.has(entry.name) && !entry.name.startsWith(".")) await walk(full);
      } else if (SOURCE_EXTENSIONS.some((ext) => entry.name.toLowerCase().endsWith(ext))) {
        try {
          if (statSync(full).size <= MAX_FILE_BYTES) found.push(full);
        } catch {
          // A file that vanished mid-walk is skipped.
        }
      }
    }
  };
  await walk(app);
  return found;
}

/** A pending set of file writes, so a tweak lands all at once or not at all. */
class FileEdits {
  private readonly contents = new Map<string, string>();
  readonly patches: Patch[] = [];
  constructor(private readonly app: string) {}

  abs(rel: string): string {
    return resolveInBuild({ app: this.app } as BuildPaths, rel);
  }

  async read(rel: string): Promise<string> {
    const cached = this.contents.get(rel);
    if (cached !== undefined) return cached;
    try {
      return await readFile(this.abs(rel), "utf8");
    } catch {
      return "";
    }
  }

  set(rel: string, content: string, patch?: Patch): void {
    this.contents.set(rel, content);
    if (patch) this.patches.push(patch);
  }

  get files(): string[] {
    return [...this.contents.keys()];
  }

  async commit(): Promise<void> {
    for (const [rel, content] of this.contents) await writeAtomic(this.abs(rel), content);
  }
}

const HTML_ENTRIES = ["index.html", "public/index.html", "src/index.html"];
const GLOBAL_STYLES = [
  "src/index.css",
  "src/styles.css",
  "src/globals.css",
  "src/app/globals.css",
  "app/globals.css",
  "styles/globals.css",
  "src/styles/globals.css",
];

interface SheetLocation {
  /** Build-relative sheet file. */
  sheet: string;
  /** True when the sheet is the app's own global stylesheet (blocks append at its end). */
  shared: boolean;
}

/**
 * Where this build's managed sheet lives, linking it into the HTML entry the
 * first time. Vite and static apps: public/4ge-tweaks.css (served at /) or a
 * sheet beside index.html. Apps without an HTML entry: the global stylesheet.
 */
async function locateSheet(app: string, edits: FileEdits): Promise<SheetLocation> {
  const entry = HTML_ENTRIES.find((rel) => existsSync(join(app, rel)));
  if (entry) {
    const usePublic = existsSync(join(app, "public")) && entry !== "src/index.html";
    const sheet = usePublic ? `public/${TWEAK_SHEET_NAME}` : toPosix(join(dirname(entry), TWEAK_SHEET_NAME)).replace(/^\.\//, "");
    const href = usePublic ? `/${TWEAK_SHEET_NAME}` : TWEAK_SHEET_NAME;
    const html = await edits.read(entry);
    const linked = ensureStylesheetLink(entry, html, href);
    if (linked) edits.set(entry, linked.source);
    return { sheet, shared: false };
  }
  const global = GLOBAL_STYLES.find((rel) => existsSync(join(app, rel)));
  if (global) return { sheet: global, shared: true };
  throw new HttpError("conflict", "4GE could not find where this build loads its styles", {
    hint: "The app needs an index.html or a global stylesheet such as src/index.css.",
  });
}

async function writeTokens(app: string, system: DesignSystem): Promise<string[]> {
  const edits = new FileEdits(app);
  const location = await locateSheet(app, edits);
  const sheet = await edits.read(location.sheet);
  const css = tokensToCss(system.tokens);
  const next = css
    ? upsertBlock(sheet, TOKENS_BLOCK_ID, `/* ${system.name.replace(/\*\//g, "")} */\n${css}`, { first: !location.shared })
    : removeBlock(sheet, TOKENS_BLOCK_ID);
  edits.set(location.sheet, next);
  await edits.commit();
  return edits.files;
}

/** Find the one element a target names across the build's source. */
async function locateElement(
  app: string,
  edits: FileEdits,
  selector: string,
): Promise<{ file: string; source: string; span: ElementSpan }> {
  const attr = targetAttr(selector);
  if (!attr) {
    throw badRequest(
      `This tweak edits source, so its target must name one element: [data-4ge-id="..."] or #id. "${selector.slice(0, 80)}" does not`,
    );
  }
  const hits: Array<{ file: string; source: string; span: ElementSpan }> = [];
  for (const abs of await sourceFiles(app)) {
    const file = toPosix(relative(app, abs));
    const source = await edits.read(file);
    for (const span of findElements(source, attr)) hits.push({ file, source, span });
  }
  const label = `${attr.attr}="${attr.value}"`;
  if (hits.length === 0) throw notFound(`An element with ${label} in the build's source`);
  if (hits.length > 1) {
    throw new HttpError(
      "conflict",
      `${hits.length} elements carry ${label} (${[...new Set(hits.map((hit) => hit.file))].slice(0, 4).join(", ")}), so 4GE cannot tell which one to change`,
      { hint: "Give the element a unique data-4ge-id." },
    );
  }
  return hits[0] as { file: string; source: string; span: ElementSpan };
}

/** URL the app uses for an asset: URLs and absolute paths as given, public/ files from the site root. */
function assetUrl(app: string, ref: string): string {
  if (/^(https?:)?\/\//i.test(ref) || ref.startsWith("/") || ref.startsWith("data:")) return ref;
  const abs = resolveInBuild({ app } as BuildPaths, ref);
  if (!existsSync(abs)) throw notFound(`The asset ${ref} in the build`);
  const rel = toPosix(relative(app, abs));
  return rel.startsWith("public/") ? `/${rel.slice("public/".length)}` : `/${rel}`;
}

async function applySourceTweak(
  paths: BuildPaths,
  app: string,
  op: Extract<TweakOp, { kind: "text" | "asset" | "component" }>,
  edits: FileEdits,
): Promise<{ css?: string }> {
  if (op.kind === "asset") {
    const url = assetUrl(app, op.assetRef);
    const attr = targetAttr(op.target.selector);
    if (attr) {
      const hit = await locateElement(app, edits, op.target.selector);
      if (MEDIA_TAGS.has(hit.span.tag)) {
        const withSrc = setAttribute(hit.file, hit.source, hit.span, "src", url);
        edits.set(hit.file, withSrc.source, withSrc.patch);
        if (op.alt !== undefined) {
          const again = findElements(withSrc.source, attr)[0];
          if (again) {
            const withAlt = setAttribute(hit.file, withSrc.source, again, "alt", op.alt);
            edits.set(hit.file, withAlt.source, withAlt.patch);
          }
        }
        return {};
      }
    }
    // Not a media element (or a CSS selector): the asset becomes its background.
    return { css: backgroundAssetCss(op.target.selector, url) };
  }

  const hit = await locateElement(app, edits, op.target.selector);
  if (op.kind === "text") {
    const result = replaceText(hit.file, hit.source, hit.span, op.text);
    edits.set(hit.file, result.source, result.patch);
    return {};
  }

  // component
  if (op.action === "remove") {
    const result = removeElement(hit.file, hit.source, hit.span);
    edits.set(hit.file, result.source, result.patch);
    return {};
  }
  const markup = await componentMarkup(paths, hit.file, op);
  const placed =
    op.action === "insert"
      ? insertAfter(hit.file, hit.source, hit.span, markup.markup)
      : replaceElement(hit.file, hit.source, hit.span, markup.markup);
  edits.set(hit.file, placed.source, placed.patch);
  if (markup.importName && markup.importSpec) {
    const withImport = ensureImport(hit.file, placed.source, markup.importName, markup.importSpec);
    if (withImport) edits.set(hit.file, withImport.source, withImport.patch);
  }
  return {};
}

/** The markup a component insert or swap places, from props.html or an inventory item built into the app. */
async function componentMarkup(
  paths: BuildPaths,
  file: string,
  op: Extract<TweakOp, { kind: "component" }>,
): Promise<{ markup: string; importName?: string; importSpec?: string }> {
  const html = op.props?.html;
  if (typeof html === "string" && html.trim().length > 0) return { markup: html };
  if (!op.componentId) {
    throw badRequest("A component insert or swap needs a componentId from the inventory, or props.html with the markup");
  }
  const item = (await store.inventory(paths)).find((candidate) => candidate.id === op.componentId);
  if (!item) throw notFound(`Inventory component ${op.componentId}`);
  const ref = item.sourceRef ?? "";
  if (/\.(tsx|jsx)$/i.test(ref) && /\.(tsx|jsx)$/i.test(file) && existsSync(resolveInBuild(paths, ref))) {
    const name = componentNameFromPath(ref);
    return { markup: `<${name}${jsxProps(op.props)} />`, importName: name, importSpec: relativeSpecifier(file, ref) };
  }
  if (item.source === "21st") {
    const install = /^[^/\s]+\/[^/\s]+$/.test(ref) ? registryInstallCommand(ref.split("/")[0] as string, ref.split("/")[1] as string) : undefined;
    throw new HttpError("conflict", `${item.name} is a 21st registry component that is not in the build yet`, {
      hint: install
        ? `Add it to the app first (${install}), then set the inventory item's sourceRef to its file path.`
        : "Add it to the app first, then set the inventory item's sourceRef to its file path.",
    });
  }
  throw new HttpError("conflict", `${item.name} has no component file in the build that 4GE can insert into ${file}`, {
    hint: "Set the inventory item's sourceRef to its .tsx or .jsx file, or pass props.html with the markup.",
  });
}

/* ------------------------------------------------------------------ */
/* 21st registry, live                                                */
/* ------------------------------------------------------------------ */

function findOnPath(env: Readonly<Record<string, string | undefined>>, name: string): string | null {
  const dirs = (env.PATH ?? env.Path ?? "").split(delimiter).filter(Boolean);
  const exts = process.platform === "win32" ? [".cmd", ".exe", ".bat", ""] : [""];
  for (const dir of dirs) {
    for (const ext of exts) {
      const candidate = join(dir, `${name}${ext}`);
      if (existsSync(candidate)) return candidate;
    }
  }
  return null;
}

function runCli(command: string, args: string[], env: Readonly<Record<string, string | undefined>>, timeoutMs: number) {
  return new Promise<{ code: number | null; stdout: string }>((resolvePromise, reject) => {
    // Windows .cmd shims need a shell; every argument is sanitized before it gets here.
    const child = spawn(command, args, {
      env: env as NodeJS.ProcessEnv,
      shell: process.platform === "win32",
      windowsHide: true,
    });
    let stdout = "";
    const timer = setTimeout(() => child.kill(), timeoutMs);
    child.stdout.on("data", (chunk: Buffer) => {
      if (stdout.length < 2_000_000) stdout += chunk.toString("utf8");
    });
    child.on("error", (error) => {
      clearTimeout(timer);
      reject(error);
    });
    child.on("close", (code) => {
      clearTimeout(timer);
      resolvePromise({ code, stdout });
    });
  });
}

const liveResultSchema = z
  .object({
    name: z.string().min(1),
    description: z.string().optional().nullable(),
    author: z.string().optional().nullable(),
    url: z.string().optional().nullable(),
    previewUrl: z.string().optional().nullable(),
    installCommand: z.string().optional().nullable(),
    type: z.string().optional().nullable(),
  })
  .passthrough();

async function searchRegistryLive(
  ctx: ServerContext,
  query: string,
): Promise<{ candidates: ComponentCandidate[]; live: boolean; message: string }> {
  const snapshotNote = "21st entries come from the registry catalog snapshot of 2026-10-30";
  const cli = findOnPath(ctx.env, "21st");
  if (!cli) {
    return {
      candidates: [],
      live: false,
      message: `The 21st CLI is not installed, so ${snapshotNote}. Install it with npm i -g @21st-dev/cli and run 21st login to search the registry live.`,
    };
  }
  const safe = query.replace(/[^A-Za-z0-9 _-]/g, " ").replace(/\s+/g, " ").trim().slice(0, 80);
  if (!safe) return { candidates: [], live: false, message: `Type what you need to search the 21st registry live. Until then ${snapshotNote}.` };
  try {
    const term = process.platform === "win32" ? `"${safe}"` : safe;
    const result = await runCli(cli, ["search", term, "--json"], ctx.env, 20_000);
    const parsed: unknown = JSON.parse(result.stdout.trim() || "null");
    const rows = Array.isArray(parsed)
      ? parsed
      : parsed && typeof parsed === "object" && Array.isArray((parsed as { results?: unknown }).results)
        ? (parsed as { results: unknown[] }).results
        : [];
    const candidates: ComponentCandidate[] = [];
    for (const row of rows) {
      const item = liveResultSchema.safeParse(row);
      if (!item.success || (item.data.type && item.data.type !== "component")) continue;
      const pageMatch = /21st\.dev\/@([^/]+)\/components\/([^/?#]+)/.exec(item.data.url ?? "");
      const author = pageMatch?.[1] ?? item.data.author ?? "unknown";
      const slug = pageMatch?.[2] ?? item.data.name.toLowerCase().replace(/[^a-z0-9]+/g, "-");
      const candidate = componentCandidateSchema.safeParse({
        key: `21st:${author}/${slug}`,
        name: item.data.name.slice(0, 120),
        category: guessCategory(`${item.data.name} ${item.data.description ?? ""}`),
        description: (item.data.description ?? "").slice(0, 1200),
        source: "21st",
        sourceRef: `${author}/${slug}`,
        sourceUrl: item.data.url ?? undefined,
        author,
        previewUrl: item.data.previewUrl ?? undefined,
        installCommand: (item.data.installCommand ?? registryInstallCommand(author, slug)).slice(0, 600),
        via: "21st-live",
        fetchedAt: now(),
      });
      if (candidate.success) candidates.push(candidate.data);
    }
    if (result.code !== 0 && candidates.length === 0) {
      return {
        candidates: [],
        live: false,
        message: `The 21st CLI did not answer the search (exit ${result.code}); run 21st login and try again. Until then ${snapshotNote}.`,
      };
    }
    return { candidates, live: true, message: `Searched the 21st registry live for "${safe}": ${candidates.length} components.` };
  } catch {
    return { candidates: [], live: false, message: `The 21st CLI search failed, so ${snapshotNote}.` };
  }
}

/* ------------------------------------------------------------------ */
/* Routes                                                             */
/* ------------------------------------------------------------------ */

export const register: LayerRegistrar = (app, ctx) => {
  /* design systems */
  implement(app, ctx, "design.systems.list", async ({ query }) => {
    const paths = buildPaths(ctx, query.buildId);
    const systems = await store.systems(paths);
    return { systems: [...systems].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)) };
  });

  implement(app, ctx, "design.systems.save", async ({ params, body: input }) => {
    const body = designSystemSchema.parse(input);
    if (params.id !== body.id) throw badRequest(`The URL names ${params.id} but the body is ${body.id}`);
    const paths = buildPaths(ctx, body.buildId);
    // Refuse unsafe token values before anything is written.
    try {
      tokensToCss(body.tokens);
    } catch (error) {
      if (error instanceof DesignInputError) throw badRequest(error.message);
      throw error;
    }
    return serialize(body.buildId, async () => {
      const system: DesignSystem = { ...body, updatedAt: now() };
      const systems = (await store.systems(paths)).filter((candidate) => candidate.id !== system.id);
      await store.save(paths, "systems", [...systems, system]);
      if (existsSync(paths.app)) {
        const files = await writeTokens(paths.app, system);
        log(ctx, body.buildId, `Design system "${system.name}" applied to the build (${files.join(", ")})`);
      } else {
        log(ctx, body.buildId, `Design system "${system.name}" saved; the build has no app source yet, so its tokens apply when it does`);
      }
      return { system };
    });
  });

  /* component inventory */
  implement(app, ctx, "design.inventory.list", async ({ query }) => {
    const paths = buildPaths(ctx, query.buildId);
    const items = await store.inventory(paths);
    return { items: [...items].sort((a, b) => a.name.localeCompare(b.name)) };
  });

  implement(app, ctx, "design.inventory.candidates", async ({ query }) => {
    if (query.buildId) buildPaths(ctx, query.buildId);
    const q = query.q ?? "";
    const live = await searchRegistryLive(ctx, q);
    const liveKeys = new Set(live.candidates.map((candidate) => candidate.key));
    const pool = [
      ...live.candidates,
      ...filterCandidates([...kitCandidates(), ...registrySnapshotCandidates()], q).filter((candidate) => !liveKeys.has(candidate.key)),
    ];
    return { candidates: pool, registry: { live: live.live, message: live.message } };
  });

  implement(app, ctx, "design.inventory.save", async ({ params, body: input }) => {
    const body = componentInventoryItemSchema.parse(input);
    if (params.id !== body.id) throw badRequest(`The URL names ${params.id} but the body is ${body.id}`);
    const paths = buildPaths(ctx, body.buildId);
    return serialize(body.buildId, async () => {
      const item: ComponentInventoryItem = { ...body, updatedAt: now() };
      const items = (await store.inventory(paths)).filter((candidate) => candidate.id !== item.id);
      await store.save(paths, "inventory", [...items, item]);
      return { item };
    });
  });

  /* tweaks */
  implement(app, ctx, "design.tweaks.list", async ({ query }) => {
    const paths = buildPaths(ctx, query.buildId);
    const tweaks = await store.tweaks(paths);
    return { tweaks: [...tweaks].sort((a, b) => b.createdAt.localeCompare(a.createdAt)) };
  });

  implement(app, ctx, "design.tweaks.apply", async ({ body }) => {
    const op = tweakOpSchema.parse(body);
    const paths = buildPaths(ctx, op.buildId);
    const app = requireApp(paths, op.buildId);
    return serialize(op.buildId, async () => {
      const id = makeId("tweak", nanoid(12));
      const createdAt = now();
      const edits = new FileEdits(app);
      const undo: UndoRecord = { tweakId: id, patches: [] };
      try {
        let css: string | null = null;
        if (op.kind === "text" || op.kind === "asset" || op.kind === "component") {
          css = (await applySourceTweak(paths, app, op, edits)).css ?? null;
        } else {
          css = tweakToCss(op);
        }
        if (css) {
          const location = await locateSheet(app, edits);
          edits.set(location.sheet, upsertBlock(await edits.read(location.sheet), id, css));
          undo.sheet = location.sheet;
        }
      } catch (error) {
        if (error instanceof DesignInputError) throw badRequest(error.message);
        throw error;
      }
      undo.patches = edits.patches;
      await edits.commit();
      await store.saveUndo(paths, undo);
      const tweak: AppliedTweak = {
        id,
        op,
        status: "applied",
        by: "user",
        changedFiles: edits.files,
        createdAt,
        appliedAt: now(),
      };
      await store.save(paths, "tweaks", [...(await store.tweaks(paths)), tweak]);
      log(ctx, op.buildId, `Tweak ${id} (${op.kind}) applied to ${op.target.label ?? op.target.selector}: ${edits.files.join(", ")}`);
      return { tweak };
    });
  });

  implement(app, ctx, "design.tweaks.revert", async ({ params }) => {
    const { buildId, paths } = await findOwner(ctx, "tweaks", params.id);
    const app = requireApp(paths, buildId);
    return serialize(buildId, async () => {
      const tweaks = await store.tweaks(paths);
      const tweak = tweaks.find((candidate) => candidate.id === params.id) as AppliedTweak;
      if (tweak.status === "reverted") throw new HttpError("conflict", `Tweak ${tweak.id} is already undone`);
      const record = await store.undo(paths, tweak.id);
      if (!record) throw new HttpError("conflict", `Tweak ${tweak.id} has no undo record, so 4GE cannot undo it`);
      const edits = new FileEdits(app);
      for (const patch of [...record.patches].reverse()) {
        const result = revertPatch(await edits.read(patch.file), patch);
        if (!result.ok) throw new HttpError("conflict", result.reason, { hint: "Undo the later tweaks on that element first." });
        edits.set(patch.file, result.source);
      }
      if (record.sheet) edits.set(record.sheet, removeBlock(await edits.read(record.sheet), tweak.id));
      await edits.commit();
      const reverted: AppliedTweak = { ...tweak, status: "reverted" };
      await store.save(
        paths,
        "tweaks",
        tweaks.map((candidate) => (candidate.id === tweak.id ? reverted : candidate)),
      );
      log(ctx, buildId, `Tweak ${tweak.id} undone: ${edits.files.join(", ")}`);
      return { tweak: reverted };
    });
  });

  /* annotations */
  implement(app, ctx, "design.annotations.list", async ({ query }) => {
    const paths = buildPaths(ctx, query.buildId);
    const annotations = await store.annotations(paths);
    return { annotations: [...annotations].sort((a, b) => a.createdAt.localeCompare(b.createdAt)) };
  });

  implement(app, ctx, "design.annotations.create", async ({ body: input }) => {
    const body = annotationDraftSchema.parse(input);
    const paths = buildPaths(ctx, body.buildId);
    return serialize(body.buildId, async () => {
      const annotation: Annotation = {
        ...body,
        id: makeId("annotation", nanoid(12)),
        author: "user",
        status: "open",
        replies: [],
        createdAt: now(),
      };
      await store.save(paths, "annotations", [...(await store.annotations(paths)), annotation]);
      log(ctx, body.buildId, `Annotation ${annotation.id} on ${body.target.label ?? body.target.selector} (${body.target.page}): ${body.body}`);
      return { annotation };
    });
  });

  implement(app, ctx, "design.annotations.update", async ({ params, body }) => {
    if (body.status === undefined && body.reply === undefined) throw badRequest("Send a reply, a status, or both");
    const { buildId, paths } = await findOwner(ctx, "annotations", params.id);
    return serialize(buildId, async () => {
      const annotations = await store.annotations(paths);
      const current = annotations.find((candidate) => candidate.id === params.id) as Annotation;
      const at = now();
      const next: Annotation = {
        ...current,
        replies: body.reply ? [...current.replies, { author: body.author ?? "user", body: body.reply, at }] : current.replies,
      };
      if (body.status === "resolved" && current.status !== "resolved") {
        next.status = "resolved";
        next.resolvedAt = at;
      } else if (body.status === "open") {
        next.status = "open";
        delete next.resolvedAt;
      }
      await store.save(
        paths,
        "annotations",
        annotations.map((candidate) => (candidate.id === next.id ? next : candidate)),
      );
      log(ctx, buildId, `Annotation ${next.id} ${body.reply ? `reply from ${body.author ?? "user"}` : ""}${body.status ? ` status ${next.status}` : ""}`.trim());
      return { annotation: next };
    });
  });
};
