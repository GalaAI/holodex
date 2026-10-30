import { addRepo, buildLibrary, removeRepo, type ModsRuntimeOptions } from "@4ge/mods";
import { implement } from "../implement.js";
import type { LayerRegistrar } from "../registry.js";
import { modsRuntime, toModsHttpError } from "./mods.js";

/**
 * Library layer: every item grouped by origin (My projects, Repos, Mods,
 * Augments, Adapters) at GET /library, plus repo shortcuts. Shares the mods
 * runtime, so adapter detection runs once for both layers.
 */
export function createLibraryLayer(overrides: Omit<Partial<ModsRuntimeOptions>, "paths" | "env"> = {}): LayerRegistrar {
  return (app, ctx) => {
    implement(app, ctx, "library.list", async () => {
      try {
        const runtime = modsRuntime(ctx, overrides);
        const adapters = await runtime.detector.snapshot();
        const [mods, augments] = await Promise.all([runtime.service.listMods(), runtime.service.listAugments(adapters)]);
        return buildLibrary({ paths: ctx.paths, env: ctx.env, mods, augments, adapters });
      } catch (error) {
        throw toModsHttpError(error);
      }
    });

    implement(app, ctx, "library.repos.add", ({ body }) => {
      try {
        return { item: addRepo(ctx.paths, body) };
      } catch (error) {
        throw toModsHttpError(error);
      }
    });

    implement(app, ctx, "library.repos.remove", ({ params }) => ({
      removed: removeRepo(ctx.paths, decodeURIComponent(params.id)),
    }));
  };
}

export const register: LayerRegistrar = createLibraryLayer();
