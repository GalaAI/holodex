import type { DesignTarget } from "@4ge/contracts";
import { useCallback, useState } from "react";
import { ForgeApiError } from "../../lib/api.js";

/** Plain words for a failed call: the server's message, then its hint. */
export function describeError(error: unknown): string {
  if (error instanceof ForgeApiError) {
    const message = error.message.replace(/\.?\s*$/, ".");
    return error.hint ? `${message} ${error.hint}` : message;
  }
  return error instanceof Error ? error.message : String(error);
}

/** Run one server action with busy and error state. */
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const run = useCallback(async <T,>(task: () => Promise<T>): Promise<T | undefined> => {
    setBusy(true);
    setError(null);
    try {
      return await task();
    } catch (failure) {
      setError(describeError(failure));
      return undefined;
    } finally {
      setBusy(false);
    }
  }, []);
  return { busy, error, setError, run };
}

/** A url-safe token for client-made ids (ds_, cmp_). */
export function token(length = 12): string {
  const bytes = new Uint8Array(length);
  crypto.getRandomValues(bytes);
  const alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
  return Array.from(bytes, (byte) => alphabet[byte % alphabet.length]).join("");
}

/** CSS attribute selector for a data-4ge-id. */
export function selectorFor4geId(id: string): string {
  return `[data-4ge-id="${id.replace(/"/g, "")}"]`;
}

/**
 * The tweak target for a DOM element: its closest data-4ge-id, else its
 * id, else a short tag.class path (CSS tweaks only). Used by the artboard
 * picker; works on any same-origin document, including an iframe's.
 */
export function targetForElement(element: Element, page = "/"): DesignTarget {
  const named = element.closest("[data-4ge-id]");
  const value = named?.getAttribute("data-4ge-id");
  if (named && value) return { selector: selectorFor4geId(value), page, label: value };
  const withId = element.closest("[id]");
  if (withId && withId.id && /^[A-Za-z_][\w-]*$/.test(withId.id)) {
    return { selector: `#${withId.id}`, page, label: `#${withId.id}` };
  }
  const parts: string[] = [];
  let current: Element | null = element;
  while (current && parts.length < 3 && current.tagName.toLowerCase() !== "body") {
    const classes = [...current.classList].filter((name) => /^[A-Za-z_][\w-]*$/.test(name)).slice(0, 2);
    parts.unshift(`${current.tagName.toLowerCase()}${classes.map((name) => `.${name}`).join("")}`);
    current = current.parentElement;
  }
  const selector = parts.join(" > ") || "body";
  return { selector, page, label: selector };
}

/**
 * The target under a point of an iframe artboard, when its document is
 * readable (same origin). Cross-origin previews return null: the person
 * types the element instead.
 */
export function targetFromIframe(frame: HTMLIFrameElement | null, xRatio: number, yRatio: number, page = "/"): DesignTarget | null {
  if (!frame) return null;
  try {
    const doc = frame.contentDocument;
    if (!doc) return null;
    const rect = frame.getBoundingClientRect();
    const element = doc.elementFromPoint(xRatio * rect.width, yRatio * rect.height);
    return element ? targetForElement(element, page) : null;
  } catch {
    return null;
  }
}

/** True when the selector names one element, so text, asset and component tweaks can edit its source. */
export function namesOneElement(selector: string): boolean {
  const s = selector.trim();
  return /^\[data-4ge-id\s*=\s*["']?[^"'\]]+["']?\s*\]$/.test(s) || /^#[A-Za-z_][\w-]*$/.test(s);
}
