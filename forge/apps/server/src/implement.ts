import {
  API_PREFIX,
  ROUTES,
  type JsonRouteName,
  type RawRouteName,
  type RouteBody,
  type RouteName,
  type RouteParams,
  type RouteQuery,
  type RouteResponse,
  type SessionEvent,
  type StreamRouteName,
} from "@4ge/contracts";
import type { Context, Hono } from "hono";
import { streamSSE } from "hono/streaming";
import type { ServerContext } from "./context.js";
import { HttpError, toHttpError } from "./errors.js";
import type { EventFilter } from "./events.js";

/**
 * implement(): the one way a layer adds a route. It registers the method
 * and path from the @4ge/contracts route table, parses the body and query
 * with the table's schemas, and checks the response against the table, so a
 * handler cannot drift from what the studio's client expects.
 *
 *   implement(app, ctx, "seeds.get", async ({ params }) => ({ seed: await load(params.id) }));
 */
export interface HandlerInput<N extends RouteName> {
  params: RouteParams<N>;
  query: [RouteQuery<N>] extends [never] ? Record<string, never> : RouteQuery<N>;
  body: [RouteBody<N>] extends [never] ? undefined : RouteBody<N>;
  c: Context;
  ctx: ServerContext;
}

export type Handler<N extends JsonRouteName> = (
  input: HandlerInput<N>,
) => RouteResponse<N> | Promise<RouteResponse<N>>;

export function apiPath(name: RouteName): string {
  return `${API_PREFIX}${ROUTES[name].path}`;
}

async function readInput(name: RouteName, c: Context): Promise<{ query: unknown; body: unknown }> {
  const def = ROUTES[name];
  const query = "query" in def && def.query ? def.query.parse(c.req.query()) : {};
  let body: unknown = undefined;
  if ("body" in def && def.body) {
    let raw: unknown = {};
    const text = await c.req.text();
    if (text.trim().length > 0) {
      try {
        raw = JSON.parse(text);
      } catch {
        throw new HttpError("bad_request", "The request body is not valid JSON");
      }
    }
    body = def.body.parse(raw);
  }
  return { query, body };
}

export function implement<N extends JsonRouteName>(
  app: Hono,
  ctx: ServerContext,
  name: N,
  handler: Handler<N>,
): void {
  const def = ROUTES[name];
  ctx.implemented.add(name);
  app.on(def.method, apiPath(name), async (c) => {
    const { query, body } = await readInput(name, c);
    const result = await handler({
      params: c.req.param() as RouteParams<N>,
      query: query as HandlerInput<N>["query"],
      body: body as HandlerInput<N>["body"],
      c,
      ctx,
    });
    const checked = def.response.safeParse(result);
    if (!checked.success) {
      throw new HttpError(
        "internal",
        `The ${name} handler returned data that does not match the contract`,
        {
          detail: checked.error.issues
            .slice(0, 10)
            .map((issue) => ({ path: issue.path.map(String), message: issue.message })),
        },
      );
    }
    return c.json(checked.data as object, name.endsWith(".create") || name.endsWith(".register") ? 201 : 200);
  });
}

export interface RawInput<N extends RawRouteName> {
  params: RouteParams<N>;
  query: [RouteQuery<N>] extends [never] ? Record<string, never> : RouteQuery<N>;
  c: Context;
  ctx: ServerContext;
}

/**
 * implementRaw(): register a raw route from the table (RouteDef.raw). The
 * query is parsed with the table's schema like any route; the handler
 * answers with the file itself (a Response), and failures still use the
 * one JSON error shape.
 */
export function implementRaw<N extends RawRouteName>(
  app: Hono,
  ctx: ServerContext,
  name: N,
  handler: (input: RawInput<N>) => Response | Promise<Response>,
): void {
  const def = ROUTES[name];
  ctx.implemented.add(name);
  app.on(def.method, apiPath(name), async (c) => {
    const { query } = await readInput(name, c);
    return handler({ params: c.req.param() as RouteParams<N>, query: query as RawInput<N>["query"], c, ctx });
  });
}

export interface StreamInput<N extends StreamRouteName> {
  params: RouteParams<N>;
  c: Context;
  ctx: ServerContext;
}

/**
 * implementStream(): register an SSE route from the table. The handler
 * returns the event filter for this stream (and may throw HttpError, e.g.
 * not_found, before streaming starts). Replays from Last-Event-ID, sends a
 * heartbeat every 15 seconds, and unsubscribes when the client goes away.
 */
export function implementStream<N extends StreamRouteName>(
  app: Hono,
  ctx: ServerContext,
  name: N,
  handler: (input: StreamInput<N>) => EventFilter | Promise<EventFilter>,
): void {
  const def = ROUTES[name];
  ctx.implemented.add(name);
  app.on(def.method, apiPath(name), async (c) => {
    const filter = await handler({ params: c.req.param() as RouteParams<N>, c, ctx });
    const lastId = c.req.header("last-event-id") ?? c.req.query("lastEventId");
    return sseResponse(c, ctx, filter, lastId);
  });
}

export function sseResponse(c: Context, ctx: ServerContext, filter: EventFilter, lastId?: string): Response {
  return streamSSE(
    c,
    async (stream) => {
      const queue: SessionEvent[] = [...ctx.events.replay(lastId, filter)];
      let wake: (() => void) | undefined;
      const unsubscribe = ctx.events.subscribe(filter, (event) => {
        queue.push(event);
        wake?.();
      });
      let open = true;
      stream.onAbort(() => {
        open = false;
        unsubscribe();
        wake?.();
      });
      const heartbeat = setInterval(() => {
        queue.push({ id: "0", at: new Date().toISOString(), type: "heartbeat", data: {} });
        wake?.();
      }, ctx.heartbeatMs);
      try {
        await stream.writeSSE({ event: "ready", data: JSON.stringify({ at: new Date().toISOString() }), retry: 3000 });
        while (open) {
          while (queue.length > 0 && open) {
            const event = queue.shift() as SessionEvent;
            await stream.writeSSE({
              event: event.type,
              data: JSON.stringify(event),
              ...(event.type === "heartbeat" ? {} : { id: event.id }),
            });
          }
          if (!open) break;
          await new Promise<void>((resolve) => {
            wake = resolve;
          });
          wake = undefined;
        }
      } finally {
        clearInterval(heartbeat);
        unsubscribe();
      }
    },
    async (error, stream) => {
      const failure = toHttpError(error);
      await stream.writeSSE({
        event: "error",
        data: JSON.stringify({
          id: "0",
          at: new Date().toISOString(),
          type: "error",
          data: { code: failure.code, message: failure.message },
        }),
      });
    },
  );
}
