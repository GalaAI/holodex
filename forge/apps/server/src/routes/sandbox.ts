import { SandboxError, sandboxManagerFor, type SandboxManager } from "@4ge/orchestra-sandbox";
import type { Hono } from "hono";
import type { ServerContext } from "../context.js";
import { HttpError } from "../errors.js";
import { implement } from "../implement.js";
import type { LayerRegistrar } from "../registry.js";

/**
 * Sandbox layer: sandbox runs and test flights (/sandbox/*), served by
 * @4ge/orchestra-sandbox (Icarus). Start and test flight return at once with
 * a starting run or a running flight; progress arrives as sandbox.updated,
 * testflight.updated and receipt.recorded events on the build stream, and
 * GET /sandbox/runs/:id and /sandbox/test-flight/:id read the latest state.
 */

function toHttp(error: unknown): unknown {
  if (!(error instanceof SandboxError)) return error;
  const options = {
    ...(error.hint ? { hint: error.hint } : {}),
    ...(error.port !== undefined || error.detail !== undefined
      ? { detail: { ...(error.port !== undefined ? { port: error.port } : {}), ...((error.detail as object | undefined) ?? {}) } }
      : {}),
  };
  return new HttpError(error.code, error.message, options);
}

async function guarded<T>(work: () => T | Promise<T>): Promise<T> {
  try {
    return await work();
  } catch (error) {
    throw toHttp(error);
  }
}

/** Register the sandbox routes against a given manager (tests pass one with a fake Docker). */
export function registerSandbox(app: Hono, ctx: ServerContext, manager: SandboxManager): void {
  const unsubscribe = manager.on((event) => {
    ctx.events.publish(event);
  });
  ctx.onClose(async () => {
    unsubscribe();
    await manager.close();
  });

  implement(app, ctx, "sandbox.status", () => guarded(() => manager.status()));
  implement(app, ctx, "sandbox.start", ({ body }) => guarded(async () => ({ run: await manager.start(body.buildId) })));
  implement(app, ctx, "sandbox.stop", ({ body }) => guarded(async () => ({ run: await manager.stop(body.runId) })));
  implement(app, ctx, "sandbox.runs.list", ({ query }) => guarded(() => ({ runs: manager.listRuns(query.buildId) })));
  implement(app, ctx, "sandbox.runs.get", ({ params }) => guarded(() => ({ run: manager.getRun(params.id) })));
  implement(app, ctx, "sandbox.testFlight.start", ({ body }) =>
    guarded(async () => ({ testFlight: await manager.startTestFlight(body) })),
  );
  implement(app, ctx, "sandbox.testFlight.get", ({ params }) =>
    guarded(() => ({ testFlight: manager.getTestFlight(params.id) })),
  );
}

export const register: LayerRegistrar = (app, ctx) => {
  registerSandbox(app, ctx, sandboxManagerFor(ctx.paths, { env: ctx.env }));
};
