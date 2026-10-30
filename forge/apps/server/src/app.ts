import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
  API_PREFIX,
  PORTS,
  ROUTES,
  SERVER_LAYERS,
  listRoutes,
  serverOrigin,
  type Health,
  type RouteName,
  type ServerLayer,
} from "@4ge/contracts";
import { serveStatic } from "@hono/node-server/serve-static";
import { Hono } from "hono";
import { cors } from "hono/cors";
import { createContext, type ContextOptions, type ServerContext } from "./context.js";
import { HttpError, toHttpError } from "./errors.js";
import { apiPath, implement } from "./implement.js";
import { LAYER_REGISTRARS, type LayerRegistrar } from "./registry.js";

export interface AppOptions extends ContextOptions {
  /** Folder of the built studio. Defaults to apps/studio/dist or FORGE_STUDIO_DIST. */
  studioDist?: string;
  /** The port the server listens on, reported by /health. */
  port?: number;
  /** Replace layer registrars (tests). */
  layers?: Partial<Record<Exclude<ServerLayer, "core">, LayerRegistrar>>;
}

export interface ForgeApp {
  app: Hono;
  ctx: ServerContext;
  studioDist: string;
}

/** Origins allowed to call the API from a browser: the studio dev server. */
export function allowedOrigins(): string[] {
  return [`http://127.0.0.1:${PORTS.studioDev}`, `http://localhost:${PORTS.studioDev}`, serverOrigin(PORTS.server)];
}

function defaultStudioDist(env: Record<string, string | undefined>): string {
  if (env.FORGE_STUDIO_DIST) return resolve(env.FORGE_STUDIO_DIST);
  // src/app.ts and dist/app.js both sit two levels under apps/server.
  const here = dirname(fileURLToPath(import.meta.url));
  return resolve(here, "..", "..", "studio", "dist");
}

function layerStatus(ctx: ServerContext): Record<string, boolean> {
  const status: Record<string, boolean> = {};
  for (const layer of SERVER_LAYERS) {
    status[layer] = listRoutes().some((route) => route.layer === layer && ctx.implemented.has(route.name));
  }
  return status;
}

const NOT_BUILT_PAGE = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>4GE</title>
<style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#050607;color:#DAD1B9;font:15px/1.5 system-ui,sans-serif}main{max-width:36rem;padding:24px}code{font-family:ui-monospace,monospace;color:#79C4B7}small{display:block;font-family:ui-monospace,monospace;letter-spacing:.16em;text-transform:uppercase;color:#6B6A62;margin-bottom:8px}</style>
</head><body><main><small>4GE // studio</small><h1>The studio is not built yet</h1>
<p>The 4GE server is running, but there is no built studio to show. Run <code>pnpm -C forge build</code>, then reload this page. During development, open the studio dev server on port ${PORTS.studioDev} instead.</p></main></body></html>`;

export async function createApp(options: AppOptions = {}): Promise<ForgeApp> {
  const ctx = createContext(options);
  const studioDist = options.studioDist ?? defaultStudioDist(ctx.env);
  const app = new Hono();

  app.use(`${API_PREFIX}/*`, cors({ origin: allowedOrigins(), allowHeaders: ["content-type", "last-event-id"], maxAge: 600 }));

  app.onError((error, c) => {
    const failure = toHttpError(error);
    return c.json(failure.toBody(), failure.status as 400);
  });

  // Core: health, also answered at /health for Holodex's probe.
  const studioIndex = join(studioDist, "index.html");
  const health = (): Health => ({
    ok: true,
    name: "4ge",
    version: ctx.version,
    startedAt: ctx.startedAt,
    ports: { ...PORTS, server: options.port ?? PORTS.server },
    layers: layerStatus(ctx),
    studio: existsSync(studioIndex)
      ? { served: true }
      : { served: false, message: "The studio is not built yet. Run pnpm -C forge build." },
  });
  implement(app, ctx, "health", () => health());
  app.get("/health", (c) => c.json(health()));

  // Layers, in slot order. A layer that throws while registering is
  // reported and skipped; its routes fall through to 501.
  const registrars = { ...LAYER_REGISTRARS, ...(options.layers ?? {}) };
  for (const layer of SERVER_LAYERS) {
    if (layer === "core") continue;
    try {
      await registrars[layer](app, ctx);
    } catch (error) {
      ctx.events.publish({
        type: "log",
        data: {
          level: "error",
          message: `The ${layer} layer failed to register: ${toHttpError(error).message}`,
        },
      });
    }
  }

  // Every route a layer has not implemented answers 501 in plain words.
  // Registered after the layers, so a real handler always wins.
  for (const route of listRoutes()) {
    app.on(route.method, apiPath(route.name), () => {
      throw new HttpError(
        "not_implemented",
        `${route.summary}: the ${route.layer} layer is not connected yet`,
        { hint: `The 4GE server has no ${route.layer} layer in this build.` },
      );
    });
  }

  app.all(`${API_PREFIX}/*`, (c) => {
    throw new HttpError("not_found", `There is no 4GE route ${c.req.method} ${c.req.path}`);
  });

  // The built studio, with single-page fallback to index.html.
  if (existsSync(studioIndex)) {
    app.use("/*", serveStatic({ root: studioDist }));
    const indexHtml = () => readFileSync(studioIndex, "utf8");
    app.get("*", (c) => c.html(indexHtml()));
  } else {
    app.get("*", (c) => c.html(NOT_BUILT_PAGE, 200));
  }

  app.notFound((c) => c.json(new HttpError("not_found", `Nothing at ${c.req.path}`).toBody(), 404));

  return { app, ctx, studioDist };
}

/** The route names a layer owns, for docs and tests. */
export function routesOf(layer: ServerLayer): RouteName[] {
  return (Object.keys(ROUTES) as RouteName[]).filter((name) => ROUTES[name].layer === layer);
}
