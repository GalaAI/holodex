import { DesignInputError } from "./tokens.js";

/**
 * Source edits for tweaks CSS cannot express: change an element's text,
 * swap an image, insert, swap or remove a component. They work on the
 * build's real source files (HTML, JSX, TSX, Vue, Svelte, Astro) and find
 * the element by its data-4ge-id (or id) attribute, so a tweak always lands
 * on one named element, never on a guess.
 *
 * Every edit returns a Patch carrying a little surrounding context, so undo
 * can put the original back even after other edits changed the same file,
 * and refuses plainly when the region itself was changed since.
 *
 * Pure string functions: the server reads and writes the files.
 */

export const SOURCE_EXTENSIONS = [".html", ".htm", ".jsx", ".tsx", ".vue", ".svelte", ".astro"] as const;

export interface TargetAttr {
  attr: "data-4ge-id" | "id";
  value: string;
}

/**
 * The element a selector names, when it names exactly one by attribute:
 * [data-4ge-id="hero"], #hero, or a bare name (taken as a data-4ge-id).
 * Anything else (classes, descendant selectors) is CSS-only: null.
 */
export function targetAttr(selector: string): TargetAttr | null {
  const s = selector.trim();
  const data = /^\[data-4ge-id\s*=\s*(?:"([^"]+)"|'([^']+)'|([A-Za-z0-9_-]+))\s*\]$/.exec(s);
  if (data) return { attr: "data-4ge-id", value: (data[1] ?? data[2] ?? data[3]) as string };
  const id = /^#([A-Za-z_][A-Za-z0-9_-]*)$/.exec(s);
  if (id) return { attr: "id", value: id[1] as string };
  if (/^[A-Za-z][A-Za-z0-9_-]*$/.test(s) && !HTML_TAGS.has(s.toLowerCase())) return { attr: "data-4ge-id", value: s };
  return null;
}

const HTML_TAGS = new Set(
  "a abbr address article aside audio b body button canvas caption code div dl dt dd em footer form h1 h2 h3 h4 h5 h6 header html i img input label li main nav ol p picture pre section select small source span strong sub sup svg table tbody td textarea tfoot th thead tr ul video".split(
    " ",
  ),
);

export interface ElementSpan {
  tag: string;
  /** Index of "<". */
  start: number;
  /** Index just after the opening tag's ">". */
  openEnd: number;
  /** Index of the closing "</tag>" (equals openEnd for a self-closing tag). */
  closeStart: number;
  /** Index just after the closing tag. */
  end: number;
  selfClosing: boolean;
}

/** Index just after the ">" that ends the tag opening at `start`, honoring quotes and JSX braces. */
function endOfTag(source: string, start: number): number {
  let quote: string | null = null;
  let depth = 0;
  for (let i = start + 1; i < source.length; i += 1) {
    const ch = source[i];
    if (quote) {
      if (ch === quote) quote = null;
      continue;
    }
    if (ch === '"' || ch === "'" || ch === "`") quote = ch;
    else if (ch === "{") depth += 1;
    else if (ch === "}") depth = Math.max(0, depth - 1);
    else if (ch === ">" && depth === 0) return i + 1;
  }
  return -1;
}

function attrPattern(target: TargetAttr): RegExp {
  const value = target.value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return new RegExp(`\\s${target.attr}\\s*=\\s*(?:"${value}"|'${value}'|\\{\\s*["'\`]${value}["'\`]\\s*\\})`);
}

/** Every element in `source` carrying the target attribute. */
export function findElements(source: string, target: TargetAttr): ElementSpan[] {
  const found: ElementSpan[] = [];
  const pattern = attrPattern(target);
  const opener = /<([A-Za-z][\w.:-]*)/g;
  for (let match = opener.exec(source); match; match = opener.exec(source)) {
    const start = match.index;
    const openEnd = endOfTag(source, start);
    if (openEnd === -1) break;
    const openTag = source.slice(start, openEnd);
    if (!pattern.test(openTag)) continue;
    const tag = match[1] as string;
    const selfClosing = /\/\s*>$/.test(openTag) || VOID_TAGS.has(tag.toLowerCase());
    if (selfClosing) {
      found.push({ tag, start, openEnd, closeStart: openEnd, end: openEnd, selfClosing: true });
      continue;
    }
    const close = matchingClose(source, tag, openEnd);
    if (!close) continue;
    found.push({ tag, start, openEnd, closeStart: close.start, end: close.end, selfClosing: false });
  }
  return found;
}

const VOID_TAGS = new Set(["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"]);

function matchingClose(source: string, tag: string, from: number): { start: number; end: number } | null {
  const name = tag.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const pattern = new RegExp(`<(/?)${name}(?=[\\s>/])`, "g");
  pattern.lastIndex = from;
  let depth = 1;
  for (let match = pattern.exec(source); match; match = pattern.exec(source)) {
    const tagEnd = endOfTag(source, match.index);
    if (tagEnd === -1) return null;
    if (match[1] === "/") {
      depth -= 1;
      if (depth === 0) return { start: match.index, end: tagEnd };
    } else if (!/\/\s*>$/.test(source.slice(match.index, tagEnd))) {
      depth += 1;
    }
    pattern.lastIndex = tagEnd;
  }
  return null;
}

/* ------------------------------------------------------------------ */
/* Patches                                                            */
/* ------------------------------------------------------------------ */

export interface Patch {
  /** Build-relative path of the file. */
  file: string;
  /** The region before the edit, with context on both sides. */
  before: string;
  /** The same region after the edit. */
  after: string;
}

const CONTEXT = 48;

/** Replace source[from, to) with `text`, returning the new source and an undo patch. */
export function splice(file: string, source: string, from: number, to: number, text: string): { source: string; patch: Patch } {
  const lead = source.slice(Math.max(0, from - CONTEXT), from);
  const tail = source.slice(to, Math.min(source.length, to + CONTEXT));
  const next = source.slice(0, from) + text + source.slice(to);
  return { source: next, patch: { file, before: lead + source.slice(from, to) + tail, after: lead + text + tail } };
}

export type RevertResult = { ok: true; source: string } | { ok: false; reason: string };

/** Undo one patch on the current file content. */
export function revertPatch(source: string, patch: Patch): RevertResult {
  const first = source.indexOf(patch.after);
  if (first === -1) {
    return { ok: false, reason: `${patch.file} changed where this tweak edited it, so 4GE cannot undo it safely` };
  }
  if (source.indexOf(patch.after, first + 1) !== -1) {
    return { ok: false, reason: `${patch.file} has the edited region more than once, so 4GE cannot tell which to undo` };
  }
  return { ok: true, source: source.slice(0, first) + patch.before + source.slice(first + patch.after.length) };
}

/* ------------------------------------------------------------------ */
/* Edits                                                              */
/* ------------------------------------------------------------------ */

const isJsx = (file: string) => /\.(jsx|tsx)$/i.test(file);

function escapeText(file: string, text: string): string {
  if (isJsx(file)) {
    return /[{}<>]/.test(text) ? `{${JSON.stringify(text)}}` : text;
  }
  return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

function escapeAttr(value: string): string {
  return value.replace(/&/g, "&amp;").replace(/"/g, "&quot;").replace(/</g, "&lt;");
}

/** Change the text inside one element. Refuses elements whose content is markup or code. */
export function replaceText(file: string, source: string, span: ElementSpan, text: string) {
  if (span.selfClosing) throw new DesignInputError(`The <${span.tag}> element has no text to change`);
  const inner = source.slice(span.openEnd, span.closeStart);
  if (/[<{]/.test(inner)) {
    throw new DesignInputError(
      `The <${span.tag}> element holds markup or code, not plain text. Give the text its own element with a data-4ge-id.`,
    );
  }
  const lead = /^\s*/.exec(inner)?.[0] ?? "";
  const trail = /\s*$/.exec(inner)?.[0] ?? "";
  return splice(file, source, span.openEnd, span.closeStart, `${lead}${escapeText(file, text)}${trail}`);
}

/** Set (or add) one attribute on an element's opening tag. */
export function setAttribute(file: string, source: string, span: ElementSpan, name: string, value: string) {
  const openTag = source.slice(span.start, span.openEnd);
  const existing = new RegExp(`(\\s${name}\\s*=\\s*)(?:"[^"]*"|'[^']*'|\\{[^}]*\\})`).exec(openTag);
  if (existing) {
    const from = span.start + existing.index + (existing[1] as string).length;
    const to = span.start + existing.index + existing[0].length;
    return splice(file, source, from, to, `"${escapeAttr(value)}"`);
  }
  const insertAt = span.start + 1 + span.tag.length;
  return splice(file, source, insertAt, insertAt, ` ${name}="${escapeAttr(value)}"`);
}

export const MEDIA_TAGS = new Set(["img", "source", "video", "audio", "image", "Image"]);

/** Remove the element (and the newline it sat on, when it had its own line). */
export function removeElement(file: string, source: string, span: ElementSpan) {
  let from = span.start;
  let to = span.end;
  const lineStart = source.lastIndexOf("\n", from - 1) + 1;
  if (/^\s*$/.test(source.slice(lineStart, from)) && source[to] === "\n") {
    from = lineStart;
    to += 1;
  }
  return splice(file, source, from, to, "");
}

/** Insert markup right after the element, matching its indentation. */
export function insertAfter(file: string, source: string, span: ElementSpan, markup: string) {
  const lineStart = source.lastIndexOf("\n", span.start - 1) + 1;
  const indent = /^\s*/.exec(source.slice(lineStart, span.start))?.[0] ?? "";
  return splice(file, source, span.end, span.end, `\n${indent}${markup.trim()}`);
}

/** Replace the whole element with markup. */
export function replaceElement(file: string, source: string, span: ElementSpan, markup: string) {
  return splice(file, source, span.start, span.end, markup.trim());
}

/** Add `import Name from "spec";` after the last import, unless it is there. */
export function ensureImport(file: string, source: string, name: string, spec: string) {
  if (new RegExp(`import\\s+${name}\\b`).test(source) || new RegExp(`import\\s*\\{[^}]*\\b${name}\\b`).test(source)) {
    return null;
  }
  const imports = [...source.matchAll(/^import\s[^;]*?from\s*["'][^"'\n]+["'];?|^import\s*["'][^"'\n]+["'];?/gm)];
  const lastImport = imports.at(-1);
  const at = lastImport ? (lastImport.index ?? 0) + lastImport[0].length : 0;
  const line = `${at === 0 ? "" : "\n"}import ${name} from "${spec}";${at === 0 ? "\n" : ""}`;
  return splice(file, source, at, at, line);
}

/** Relative module specifier from one build file to another, without extension. */
export function relativeSpecifier(fromFile: string, toFile: string): string {
  const from = fromFile.split("/").slice(0, -1);
  const to = toFile.replace(/\.(tsx|jsx|ts|js)$/i, "").split("/");
  let shared = 0;
  while (shared < from.length && shared < to.length - 1 && from[shared] === to[shared]) shared += 1;
  const up = from.length - shared;
  const rest = to.slice(shared).join("/");
  return up === 0 ? `./${rest}` : `${"../".repeat(up)}${rest}`;
}

/** PascalCase component name from a file path, e.g. src/components/hero-banner.tsx -> HeroBanner. */
export function componentNameFromPath(path: string): string {
  const base = path.split("/").at(-1)?.replace(/\.[^.]+$/, "") ?? "Component";
  const name = base
    .split(/[^A-Za-z0-9]+/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join("");
  return /^[A-Z]/.test(name) ? name : `C${name}`;
}

/** Render JSX props from a plain object (strings, numbers, booleans only). */
export function jsxProps(props: Record<string, unknown> | undefined): string {
  if (!props) return "";
  const parts: string[] = [];
  for (const [key, value] of Object.entries(props)) {
    if (key === "html" || !/^[A-Za-z_][A-Za-z0-9_]*$/.test(key)) continue;
    if (typeof value === "string") parts.push(`${key}=${JSON.stringify(value)}`);
    else if (typeof value === "number" || typeof value === "boolean") parts.push(`${key}={${String(value)}}`);
  }
  return parts.length ? ` ${parts.join(" ")}` : "";
}

/** Add a stylesheet link right before </body> (later in the document, so its rules win). */
export function ensureStylesheetLink(file: string, html: string, href: string) {
  if (html.includes("data-4ge-tweaks")) return null;
  const at = html.search(/<\/body\s*>/i);
  if (at === -1) throw new DesignInputError(`${file} has no </body> tag to add the 4GE tweak sheet to`);
  return splice(file, html, at, at, `  <link rel="stylesheet" href="${escapeAttr(href)}" data-4ge-tweaks />\n`);
}
