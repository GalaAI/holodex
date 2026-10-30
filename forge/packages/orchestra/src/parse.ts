import type { z } from "zod";

/**
 * Reading model output. Agents answer in plain text with marked blocks, not
 * in one big JSON object: code inside a JSON string is where models make
 * escaping mistakes, and a marked block survives them.
 *
 *   <<<FILE src/server.ts>>> ...content... <<<END FILE>>>
 *   <<<CHECKS>>> [json] <<<END CHECKS>>>
 *   reply text <<<STATE>>> {json}
 */

/** Drop reasoning a model leaves inline (<think>...</think>) and surrounding blank lines. */
export function stripThinking(text: string): string {
  return text.replace(/<think>[\s\S]*?<\/think>/gi, "").replace(/^[\s\S]*?<\/think>/i, "").trim();
}

/** Find the JSON value in a model answer: a fenced block, else the outermost braces or brackets. */
export function extractJson(text: string): unknown {
  const cleaned = stripThinking(text);
  const fenced = /```(?:json)?\s*([\s\S]*?)```/i.exec(cleaned);
  const candidates: string[] = [];
  if (fenced?.[1]) candidates.push(fenced[1].trim());
  // The outermost value starts at whichever bracket comes first.
  const spans: Array<[number, string]> = [];
  const firstObject = cleaned.indexOf("{");
  const lastObject = cleaned.lastIndexOf("}");
  if (firstObject >= 0 && lastObject > firstObject) spans.push([firstObject, cleaned.slice(firstObject, lastObject + 1)]);
  const firstArray = cleaned.indexOf("[");
  const lastArray = cleaned.lastIndexOf("]");
  if (firstArray >= 0 && lastArray > firstArray) spans.push([firstArray, cleaned.slice(firstArray, lastArray + 1)]);
  spans.sort((left, right) => left[0] - right[0]);
  candidates.push(...spans.map(([, text]) => text), cleaned);
  for (const candidate of candidates) {
    try {
      return JSON.parse(candidate);
    } catch {
      try {
        return JSON.parse(candidate.replace(/,\s*([}\]])/g, "$1"));
      } catch {
        // try the next candidate
      }
    }
  }
  throw new Error("The model answer held no readable JSON");
}

export type Parsed<T> = { ok: true; value: T } | { ok: false; error: string };

export function parseJsonAs<T>(text: string, schema: z.ZodType<T>): Parsed<T> {
  let raw: unknown;
  try {
    raw = extractJson(text);
  } catch (error) {
    return { ok: false, error: error instanceof Error ? error.message : String(error) };
  }
  const parsed = schema.safeParse(raw);
  if (parsed.success) return { ok: true, value: parsed.data };
  return {
    ok: false,
    error: parsed.error.issues
      .slice(0, 8)
      .map((issue) => `${issue.path.map(String).join(".") || "root"}: ${issue.message}`)
      .join("; "),
  };
}

/** Text between <<<NAME>>> and <<<END NAME>>> (end marker optional at the end of the answer). */
export function block(text: string, name: string): string | undefined {
  // Models sometimes close a marker with two angle brackets (<<<END FILE>>); both count.
  const pattern = new RegExp(`<<<${name}>>>?\\s*([\\s\\S]*?)(?:<<<END ${name}>>>?|$)`, "i");
  const match = pattern.exec(text);
  return match?.[1]?.trim();
}

export interface FileBlock {
  path: string;
  content: string;
}

/** Every <<<FILE path>>> block, in order. A later block for the same path wins. */
export function fileBlocks(text: string, tag = "FILE"): FileBlock[] {
  const pattern = new RegExp(`<<<${tag}\\s+([^>\\n]+?)\\s*>>>?\\r?\\n?([\\s\\S]*?)\\r?\\n?<<<END ${tag}>>>?`, "gi");
  const found = new Map<string, FileBlock>();
  for (const match of text.matchAll(pattern)) {
    const path = (match[1] ?? "").trim().replace(/^["'`]|["'`]$/g, "").replace(/\\/g, "/").replace(/^\.\//, "");
    let content = match[2] ?? "";
    // Some models wrap the content in a code fence inside the block.
    const fence = /^```[a-zA-Z0-9_-]*\r?\n([\s\S]*?)\r?\n```\s*$/.exec(content.trim());
    if (fence?.[1] !== undefined) content = fence[1];
    if (path.length > 0) found.set(path, { path, content: content.endsWith("\n") ? content : `${content}\n` });
  }
  return [...found.values()];
}

/**
 * Splits a streamed answer at a marker: everything before it streams to the
 * person, everything after is machine state. Holds back a partial marker
 * at the end of a chunk so it never leaks into the visible reply.
 */
export class MarkerSplitter {
  private visible = "";
  private pending = "";
  private hidden = "";
  private found = false;

  constructor(private readonly marker: string) {}

  /** Feed a chunk; returns the text that is now safe to show. */
  push(chunk: string): string {
    if (this.found) {
      this.hidden += chunk;
      return "";
    }
    const text = this.pending + chunk;
    const at = text.indexOf(this.marker);
    if (at >= 0) {
      this.found = true;
      this.hidden += text.slice(at + this.marker.length);
      this.pending = "";
      const out = text.slice(0, at);
      this.visible += out;
      return out;
    }
    // Keep back the longest tail that could still become the marker.
    let keep = 0;
    for (let size = Math.min(this.marker.length - 1, text.length); size > 0; size -= 1) {
      if (this.marker.startsWith(text.slice(text.length - size))) {
        keep = size;
        break;
      }
    }
    const out = text.slice(0, text.length - keep);
    this.pending = text.slice(text.length - keep);
    this.visible += out;
    return out;
  }

  /** Flush at the end of the stream. */
  finish(): { visible: string; hidden: string; flushed: string } {
    const flushed = this.found ? "" : this.pending;
    this.visible += flushed;
    this.pending = "";
    return { visible: this.visible, hidden: this.hidden, flushed };
  }
}
