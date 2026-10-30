import { apiErrorSchema, type ApiErrorCode } from "./common.js";
import {
  API_PREFIX,
  ROUTES,
  buildPath,
  type JsonRouteName,
  type PathParamNames,
  type RouteBody,
  type RouteName,
  type RouteParams,
  type RouteQuery,
  type RouteResponse,
  type Routes,
  type StreamRouteName,
} from "./routes.js";

/**
 * A typed fetch client generated from the route table. The studio, e2e and
 * server tests all call through it. Browser and Node safe (global fetch).
 */

export class ForgeApiError extends Error {
  readonly status: number;
  readonly code: ApiErrorCode | "network" | "invalid_response";
  readonly hint: string | undefined;
  readonly route: string;

  constructor(options: {
    status: number;
    code: ApiErrorCode | "network" | "invalid_response";
    message: string;
    hint?: string | undefined;
    route: string;
  }) {
    super(options.message);
    this.name = "ForgeApiError";
    this.status = options.status;
    this.code = options.code;
    this.hint = options.hint;
    this.route = options.route;
  }
}

type ParamsArg<N extends RouteName> = [PathParamNames<Routes[N]["path"]>] extends [never]
  ? { params?: undefined }
  : { params: RouteParams<N> };
type BodyArg<N extends RouteName> = [RouteBody<N>] extends [never]
  ? { body?: undefined }
  : { body: RouteBody<N> };
type QueryArg<N extends RouteName> = [RouteQuery<N>] extends [never]
  ? { query?: undefined }
  : { query?: RouteQuery<N> };

export type CallArgs<N extends RouteName> = ParamsArg<N> &
  BodyArg<N> &
  QueryArg<N> & { signal?: AbortSignal };

type NeedsArgs<N extends RouteName> = [PathParamNames<Routes[N]["path"]>] extends [never]
  ? [RouteBody<N>] extends [never]
    ? false
    : true
  : true;

export type CallRest<N extends RouteName> = NeedsArgs<N> extends true
  ? [args: CallArgs<N>]
  : [args?: CallArgs<N>];

export interface ForgeClientOptions {
  /** Origin of the 4GE server, e.g. http://127.0.0.1:17400. Empty means same origin. */
  baseUrl?: string;
  fetch?: typeof fetch;
  /** Validate responses against the route table (default true). */
  validate?: boolean;
}

export interface ForgeClient {
  call<N extends JsonRouteName>(name: N, ...rest: CallRest<N>): Promise<RouteResponse<N>>;
  /** Absolute or same-origin URL for an SSE route, for EventSource. */
  streamUrl<N extends StreamRouteName>(name: N, params: RouteParams<N>): string;
  url(name: RouteName, params?: Record<string, string>, query?: Record<string, unknown>): string;
}

function queryString(query: Record<string, unknown> | undefined): string {
  if (!query) return "";
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === null) continue;
    search.set(key, String(value));
  }
  const text = search.toString();
  return text ? `?${text}` : "";
}

export function createForgeClient(options: ForgeClientOptions = {}): ForgeClient {
  const base = (options.baseUrl ?? "").replace(/\/$/, "");
  const doFetch = options.fetch ?? ((...args: Parameters<typeof fetch>) => fetch(...args));
  const validate = options.validate ?? true;

  const url = (name: RouteName, params: Record<string, string> = {}, query?: Record<string, unknown>) =>
    `${base}${API_PREFIX}${buildPath(ROUTES[name].path, params)}${queryString(query)}`;

  async function call<N extends JsonRouteName>(name: N, ...rest: CallRest<N>): Promise<RouteResponse<N>> {
    const def = ROUTES[name];
    const args = (rest[0] ?? {}) as {
      params?: Record<string, string>;
      body?: unknown;
      query?: Record<string, unknown>;
      signal?: AbortSignal;
    };
    const target = url(name, args.params, args.query);
    let response: Response;
    try {
      response = await doFetch(target, {
        method: def.method,
        headers: args.body === undefined ? { accept: "application/json" } : { accept: "application/json", "content-type": "application/json" },
        body: args.body === undefined ? undefined : JSON.stringify(args.body),
        signal: args.signal,
      });
    } catch (error) {
      throw new ForgeApiError({
        status: 0,
        code: "network",
        message: `The 4GE server did not answer (${error instanceof Error ? error.message : String(error)})`,
        hint: "Start the 4GE server, or check that Holodex started it.",
        route: name,
      });
    }

    const text = await response.text();
    let json: unknown = undefined;
    if (text.length > 0) {
      try {
        json = JSON.parse(text);
      } catch {
        throw new ForgeApiError({
          status: response.status,
          code: "invalid_response",
          message: `The server sent something that is not JSON for ${name}`,
          route: name,
        });
      }
    }

    if (!response.ok) {
      const parsed = apiErrorSchema.safeParse(json);
      if (parsed.success) {
        throw new ForgeApiError({
          status: response.status,
          code: parsed.data.error.code,
          message: parsed.data.error.message,
          hint: parsed.data.error.hint,
          route: name,
        });
      }
      throw new ForgeApiError({
        status: response.status,
        code: "invalid_response",
        message: `Request ${name} failed with HTTP ${response.status}`,
        route: name,
      });
    }

    if (!validate) return json as RouteResponse<N>;
    const parsed = def.response.safeParse(json);
    if (!parsed.success) {
      throw new ForgeApiError({
        status: response.status,
        code: "invalid_response",
        message: `The response for ${name} does not match the contract: ${parsed.error.issues
          .slice(0, 3)
          .map((issue) => `${issue.path.join(".") || "root"} ${issue.message}`)
          .join("; ")}`,
        route: name,
      });
    }
    return parsed.data as RouteResponse<N>;
  }

  return {
    call,
    streamUrl: (name, params) => url(name, params as Record<string, string>),
    url,
  };
}
