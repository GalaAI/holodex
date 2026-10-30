import { existsSync } from "node:fs";
import { augmentDraftSchema, buildIdSchema, modDraftSchema } from "@4ge/contracts";
import {
  ActionError,
  CredentialError,
  LibraryError,
  ModsError,
  connectAdapter,
  createModsRuntime,
  disconnectAdapter,
  runAdapterAction,
  type ModsRuntime,
  type ModsRuntimeOptions,
} from "@4ge/mods";
import type { ServerContext } from "../context.js";
import { HttpError, notFound } from "../errors.js";
import { implement } from "../implement.js";
import type { LayerRegistrar } from "../registry.js";

/**
 * Mods layer: mods, adapters and augments (/mods, /adapters, /augments).
 * The work lives in @4ge/mods; this file maps routes onto it and turns its
 * errors into the one JSON error shape. One runtime per server context, so
 * the Library layer reads the same detection cache.
 */

const runtimes = new WeakMap<ServerContext, ModsRuntime>();

export function modsRuntime(
  ctx: ServerContext,
  overrides: Omit<Partial<ModsRuntimeOptions>, "paths" | "env"> = {},
): ModsRuntime {
  let runtime = runtimes.get(ctx);
  if (!runtime) {
    runtime = createModsRuntime({ paths: ctx.paths, env: ctx.env, ...overrides });
    runtimes.set(ctx, runtime);
  }
  return runtime;
}

/** Map @4ge/mods errors to HTTP errors with plain words. */
export function toModsHttpError(error: unknown): unknown {
  if (error instanceof ModsError || error instanceof ActionError || error instanceof LibraryError) {
    return new HttpError(error.code, error.message);
  }
  if (error instanceof CredentialError) return new HttpError("credential_missing", error.message);
  return error;
}

async function guard<T>(work: () => Promise<T>): Promise<T> {
  try {
    return await work();
  } catch (error) {
    throw toModsHttpError(error);
  }
}

function decodeRef(raw: string, kind: "mod" | "adapter"): string {
  const value = decodeURIComponent(raw);
  return value.startsWith(`${kind}:`) ? value : `${kind}:${value}`;
}

/** Build a registrar with injected parts (tests pass a memory store and a fake runner). */
export function createModsLayer(overrides: Omit<Partial<ModsRuntimeOptions>, "paths" | "env"> = {}): LayerRegistrar {
  return (app, ctx) => {
    const runtime = () => modsRuntime(ctx, overrides);

    implement(app, ctx, "mods.list", () => guard(async () => ({ mods: await runtime().service.listMods() })));

    implement(app, ctx, "mods.create", ({ body }) =>
      guard(async () => {
        const { mod } = await runtime().service.createMod(modDraftSchema.parse(body));
        return { mod };
      }),
    );

    implement(app, ctx, "mods.get", ({ params }) =>
      guard(async () => ({ mod: await runtime().service.getMod(decodeRef(params.id, "mod")) })),
    );

    implement(app, ctx, "adapters.list", ({ query }) =>
      guard(async () => ({ adapters: await runtime().detector.list(query.refresh === "true") })),
    );

    implement(app, ctx, "adapters.connect", ({ params, body }) =>
      guard(async () => connectAdapter(runtime().detector, decodeRef(params.id, "adapter"), body.token)),
    );

    implement(app, ctx, "adapters.disconnect", ({ params }) =>
      guard(async () => ({ adapter: await disconnectAdapter(runtime().detector, decodeRef(params.id, "adapter")) })),
    );

    implement(app, ctx, "adapters.actions.run", ({ params, body }) =>
      guard(async () => {
        const { buildId, ...rest } = body.params ?? {};
        let cwd: string | undefined;
        if (buildId) {
          const parsed = buildIdSchema.safeParse(buildId);
          if (!parsed.success) throw new HttpError("bad_request", "buildId is not a build id");
          cwd = ctx.paths.build(parsed.data).app;
          if (!existsSync(cwd)) throw notFound(`The app folder for ${parsed.data}`);
        }
        const run = await runAdapterAction(runtime().detector, decodeRef(params.id, "adapter"), decodeURIComponent(params.action), {
          params: rest,
          ...(body.confirm !== undefined ? { confirm: body.confirm } : {}),
          ...(cwd ? { cwd } : {}),
        });
        return { run };
      }),
    );

    implement(app, ctx, "augments.list", () => guard(async () => ({ augments: await runtime().service.listAugments() })));

    implement(app, ctx, "augments.create", ({ body }) =>
      guard(async () => ({ augment: await runtime().service.createAugment(augmentDraftSchema.parse(body)) })),
    );
  };
}

export const register: LayerRegistrar = createModsLayer();
