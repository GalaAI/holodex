import { readFile, stat } from "node:fs/promises";
import { extname, join } from "node:path";
import { relativePathSchema } from "@4ge/contracts";
import { Orchestra, OrchestraError, routeAtlas, routeDesign, routeSandbox, type InProcessRequest } from "@4ge/orchestra";
import { HttpError, notFound } from "../errors.js";
import { forBuild, forSession } from "../events.js";
import { implement, implementRaw, implementStream } from "../implement.js";
import type { LayerRegistrar } from "../registry.js";

/**
 * Orchestra layer: models, Imagistation, Seeds, builds (sprints,
 * checkpoints, receipts) and sessions with their SSE streams. The work
 * lives in @4ge/orchestra; this file maps the route table onto it.
 *
 * The orchestra reaches the Atlas, design and sandbox layers through their
 * own routes, called in-process on this app, so it works with whichever
 * layers are connected and says plainly when one is not.
 */

function toHttp(error: unknown): unknown {
  if (error instanceof OrchestraError) {
    return new HttpError(error.code, error.message, error.hint ? { hint: error.hint } : {});
  }
  return error;
}

async function guard<T>(work: () => T | Promise<T>): Promise<T> {
  try {
    return await work();
  } catch (error) {
    throw toHttp(error);
  }
}

/** Content types for the files a build shows: previews, artboards, screenshots. */
const FILE_TYPES: Record<string, string> = {
  ".html": "text/html; charset=utf-8",
  ".htm": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".jpg": "image/jpeg",
  ".jpeg": "image/jpeg",
  ".webp": "image/webp",
  ".gif": "image/gif",
  ".ico": "image/x-icon",
};

/** Largest build file the route serves; previews and screenshots are far smaller. */
const MAX_FILE_BYTES = 25 * 1024 * 1024;

export const register: LayerRegistrar = (app, ctx) => {
  const request: InProcessRequest = (path, init) => app.request(path, init);
  const orchestra = new Orchestra({
    paths: ctx.paths,
    env: ctx.env,
    events: ctx.events,
    atlas: routeAtlas(request),
    design: routeDesign(request),
    sandbox: routeSandbox(request),
  });
  ctx.onClose(() => orchestra.close());

  /* models */
  implement(app, ctx, "models.list", () =>
    guard(async () => {
      const route = await orchestra.models.route({ refresh: true });
      return { route, models: await orchestra.models.models() };
    }),
  );

  /* imagistation */
  implement(app, ctx, "imagistation.sessions.list", () => ({ sessions: orchestra.imagistation.list() }));
  implement(app, ctx, "imagistation.sessions.create", ({ body }) => guard(() => orchestra.imagistation.create(body.idea)));
  implement(app, ctx, "imagistation.sessions.get", ({ params }) => guard(() => orchestra.imagistation.get(params.id)));
  implement(app, ctx, "imagistation.messages.send", ({ params, body }) =>
    guard(() => orchestra.imagistation.send(params.id, body.content)),
  );
  implement(app, ctx, "imagistation.seed", ({ params }) =>
    guard(async () => ({ seed: await orchestra.imagistation.writeSeed(params.id) })),
  );

  /* seeds */
  implement(app, ctx, "seeds.list", () => ({ seeds: orchestra.seeds.list() }));
  implement(app, ctx, "seeds.create", ({ body }) => guard(() => ({ seed: orchestra.createSeed(body) })));
  implement(app, ctx, "seeds.get", ({ params }) => guard(() => ({ seed: orchestra.seeds.get(params.id) })));
  implement(app, ctx, "seeds.decide", ({ params, body }) =>
    guard(() => orchestra.decideSeed(params.id, body.when, body.reminderNote)),
  );

  /* builds */
  implement(app, ctx, "builds.list", () => ({ builds: orchestra.builds.list() }));
  implement(app, ctx, "builds.create", ({ body }) => guard(async () => ({ build: await orchestra.builds.create(body) })));
  implement(app, ctx, "builds.get", ({ params }) => guard(() => ({ build: orchestra.builds.get(params.id) })));
  implement(app, ctx, "builds.checkpoints.list", ({ params }) =>
    guard(() => ({ checkpoints: orchestra.builds.checkpoints(params.id) })),
  );
  implement(app, ctx, "builds.checkpoints.decide", ({ params, body }) =>
    guard(() => orchestra.builds.decide(params.id, params.checkpointId, body)),
  );
  implement(app, ctx, "builds.cancel", ({ params }) => guard(() => ({ build: orchestra.builds.cancel(params.id) })));
  implement(app, ctx, "builds.receipts", ({ params }) =>
    guard(() => {
      const { receipts, gate } = orchestra.builds.receipts(params.id);
      return { receipts, gate };
    }),
  );
  // A build's own files by build-relative path (previews/<key>.md,
  // app/<file>, design/smith/mockups/<slug>.html, evidence/... screenshots).
  implementRaw(app, ctx, "builds.files.get", async ({ params, query }) => {
    if (!orchestra.builds.has(params.id)) throw notFound(`Build ${params.id}`);
    const relative = relativePathSchema.parse(query.path);
    const file = join(ctx.paths.build(params.id).root, relative);
    const info = await stat(file).catch(() => undefined);
    if (!info?.isFile()) throw notFound(`The file ${relative} in build ${params.id}`);
    if (info.size > MAX_FILE_BYTES) {
      throw new HttpError("bad_request", `The file ${relative} is too large to preview (${Math.round(info.size / 1048576)} MB)`);
    }
    const type = FILE_TYPES[extname(file).toLowerCase()] ?? "text/plain; charset=utf-8";
    const headers: Record<string, string> = {
      "content-type": type,
      "cache-control": "no-store",
      "x-content-type-options": "nosniff",
    };
    // Built HTML runs in an opaque origin even when opened in its own tab.
    if (type.startsWith("text/html")) headers["content-security-policy"] = "sandbox allow-scripts allow-forms";
    return new Response(new Uint8Array(await readFile(file)), { status: 200, headers });
  });
  implementStream(app, ctx, "builds.stream", ({ params }) => {
    if (!orchestra.builds.has(params.id)) throw notFound(`Build ${params.id}`);
    return forBuild(params.id);
  });

  /* sessions */
  implement(app, ctx, "sessions.list", ({ query }) => ({ sessions: orchestra.sessions.list({ buildId: query.buildId }) }));
  implement(app, ctx, "sessions.create", ({ body }) =>
    guard(() => ({ session: orchestra.conversations.create(body, (work) => orchestra.run(work)) })),
  );
  implement(app, ctx, "sessions.get", ({ params }) =>
    guard(() => ({ session: orchestra.sessions.get(params.id), messages: orchestra.sessions.messages(params.id) })),
  );
  implement(app, ctx, "sessions.tree", ({ params }) => guard(() => ({ tree: orchestra.sessions.tree(params.id) })));
  implement(app, ctx, "sessions.messages.send", ({ params, body }) =>
    guard(() => orchestra.conversations.send(params.id, body.content)),
  );
  implement(app, ctx, "sessions.cancel", ({ params }) =>
    guard(() => {
      const session = orchestra.sessions.get(params.id);
      // Cancelling a build's root session cancels the build, so its record agrees.
      if (session.kind === "build" && session.buildId && orchestra.builds.has(session.buildId)) {
        orchestra.builds.cancel(session.buildId);
        return { session: orchestra.sessions.get(params.id) };
      }
      return { session: orchestra.sessions.cancel(params.id) };
    }),
  );
  implementStream(app, ctx, "sessions.stream", ({ params }) => {
    if (!orchestra.sessions.has(params.id)) throw notFound(`Session ${params.id}`);
    return forSession(params.id, () => orchestra.sessions.descendants(params.id));
  });
};
