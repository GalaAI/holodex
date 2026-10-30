import { randomUUID } from "node:crypto";
import { appendFileSync, existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { dirname } from "node:path";
import { makeId, type IdKind, type SessionEvent } from "@4ge/contracts";
import { customAlphabet } from "nanoid";
import type { z } from "zod";

/** Record ids: the contracts prefix plus a url-safe nanoid tail. */
const token = customAlphabet("0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ", 14);
export function newId(kind: IdKind): string {
  return makeId(kind, token());
}

export const now = (): string => new Date().toISOString();

/** Write JSON atomically (temp file, then rename), so a reader never sees half a file. */
export function writeJson(path: string, value: unknown): void {
  mkdirSync(dirname(path), { recursive: true });
  const temp = `${path}.${randomUUID().slice(0, 8)}.tmp`;
  writeFileSync(temp, `${JSON.stringify(value, null, 2)}\n`, "utf8");
  renameSync(temp, path);
}

/** Read JSON, or undefined when the file is absent, unreadable or does not match the schema. */
export function readJson<T>(path: string, schema?: z.ZodType<T>): T | undefined {
  if (!existsSync(path)) return undefined;
  try {
    const raw: unknown = JSON.parse(readFileSync(path, "utf8"));
    if (!schema) return raw as T;
    const parsed = schema.safeParse(raw);
    return parsed.success ? parsed.data : undefined;
  } catch {
    return undefined;
  }
}

export function appendJsonl(path: string, value: unknown): void {
  try {
    mkdirSync(dirname(path), { recursive: true });
    appendFileSync(path, `${JSON.stringify(value)}\n`, "utf8");
  } catch {
    // The journal is a record, not a dependency; a failed append never stops work.
  }
}

/**
 * Plain words where people read: 4GE writes no em or en dashes in UI text.
 * Applied to chat replies and summaries, never to generated source files.
 */
// Built from code points so this file holds no dash characters itself.
const EM_DASH = new RegExp(`\\s*${String.fromCodePoint(0x2014)}\\s*`, "g");
const HYPHENS = new RegExp(`[${[0x2010, 0x2011, 0x2012, 0x2013].map((code) => String.fromCodePoint(code)).join("")}]`, "g");

export function plainText(text: string): string {
  return text.replace(EM_DASH, ", ").replace(HYPHENS, "-").replace(/,\s*,/g, ",");
}

export function clip(text: string, max: number): string {
  return text.length > max ? `${text.slice(0, Math.max(0, max - 3))}...` : text;
}

export function slugify(text: string, fallback = "item"): string {
  const slug = text
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 60);
  return slug.length > 0 ? slug : fallback;
}

/**
 * What a layer publishes. Structurally the same as the server's EventHub
 * draft, declared here so the orchestra does not import the server.
 */
export type EventDraft = SessionEvent extends infer E
  ? E extends SessionEvent
    ? Omit<E, "id" | "at"> & { at?: string }
    : never
  : never;

export interface EventSink {
  publish(draft: EventDraft): unknown;
}

/** Sleep that wakes early (and rejects) when the signal aborts. */
export function sleep(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(signal.reason ?? new Error("aborted"));
      return;
    }
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, ms);
    const onAbort = () => {
      clearTimeout(timer);
      reject(signal?.reason ?? new Error("aborted"));
    };
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}
