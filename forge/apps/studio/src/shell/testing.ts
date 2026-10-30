import { PORTS, type Health, type SessionEvent } from "@4ge/contracts";
import { vi } from "vitest";

/**
 * Test helpers for studio component tests: a fake 4GE server behind
 * fetch (route by method and path, every response still validated by the
 * real contract client) and a fake EventSource that tests push events into.
 * Test-only; never imported by app code.
 */
export const HEALTH: Health = {
  ok: true,
  name: "4ge",
  version: "0.1.0",
  startedAt: "2026-10-30T12:00:00.000Z",
  ports: { ...PORTS },
  layers: { core: true, atlas: true, orchestra: true, design: false, mods: false, sandbox: true, library: false },
  studio: { served: true },
};

export interface FakeRequest {
  method: string;
  path: string;
  query: URLSearchParams;
  body: unknown;
}

export type FakeHandler = (request: FakeRequest) => unknown;

export interface FakeRoute {
  method: string;
  /** Path under /api, with :param segments. */
  path: string;
  handler: FakeHandler;
}

export function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

export function apiError(status: number, code: string, message: string, hint?: string): Response {
  return json(status, { error: { code, message, ...(hint ? { hint } : {}) } });
}

/** Install a fake server; returns the list of requests it saw. */
export function fakeServer(routes: FakeRoute[]): FakeRequest[] {
  const seen: FakeRequest[] = [];
  const all: FakeRoute[] = [
    ...routes,
    { method: "GET", path: "/health", handler: () => HEALTH },
  ];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(typeof input === "string" ? input : input instanceof URL ? input.href : input.url, "http://studio.test");
      const method = (init?.method ?? "GET").toUpperCase();
      const path = url.pathname.replace(/^\/api/, "");
      const body = typeof init?.body === "string" ? JSON.parse(init.body) : undefined;
      const request = { method, path, query: url.searchParams, body };
      seen.push(request);
      for (const route of all) {
        if (route.method !== method) continue;
        const pattern = new RegExp(`^${route.path.replace(/:[A-Za-z]+/g, "[^/]+")}$`);
        if (!pattern.test(path)) continue;
        const result = route.handler(request);
        return result instanceof Response ? result : json(200, result);
      }
      return apiError(501, "not_implemented", `${method} ${path} is not connected yet`);
    }),
  );
  return seen;
}

/** A controllable EventSource: tests call FakeEventSource.emit(event). */
export class FakeEventSource {
  static instances: FakeEventSource[] = [];
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSED = 2;
  readonly url: string;
  readyState = 1;
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  private listeners = new Map<string, Array<(event: MessageEvent<string>) => void>>();

  constructor(url: string) {
    this.url = url;
    FakeEventSource.instances.push(this);
  }

  addEventListener(type: string, listener: (event: MessageEvent<string>) => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
  }

  close() {
    this.readyState = 2;
  }

  dispatch(event: SessionEvent) {
    for (const listener of this.listeners.get(event.type) ?? []) {
      listener(new MessageEvent(event.type, { data: JSON.stringify(event) }));
    }
  }

  /** Send an event to every open stream whose URL contains `match`. */
  static emit(match: string, event: SessionEvent) {
    for (const source of FakeEventSource.instances) {
      if (source.readyState !== 2 && source.url.includes(match)) source.dispatch(event);
    }
  }

  static install() {
    FakeEventSource.instances = [];
    vi.stubGlobal("EventSource", FakeEventSource);
  }
}
