import { readFile } from "node:fs/promises";
import { join } from "node:path";
import {
  AtlasError,
  AtlasRegistry,
  ScaffoldError,
  craftBlueprint,
  craftQuestions,
  listStarterManifests,
  normalizeBlueprintId,
  startersDir,
  type MatchQuery,
} from "@4ge/atlas";
import { seedIdSchema, seedSchema, type Seed } from "@4ge/contracts";
import { HttpError } from "../errors.js";
import { implement } from "../implement.js";
import type { LayerRegistrar } from "../registry.js";

/**
 * Atlas layer slot: the user's Atlas of Blueprints (/atlas/*). Lists, reads
 * and registers Blueprints, matches a Seed or a bare idea against them, lists
 * the app starters, and runs Prometheus's crafting method. The Atlas lives
 * under dataRoot()/atlas/blueprints and starts with the starter Blueprints
 * from @4ge/atlas.
 */
function toHttp(error: unknown): unknown {
  if (error instanceof AtlasError || error instanceof ScaffoldError) {
    const hint = error instanceof AtlasError ? error.hint : undefined;
    return new HttpError(error.code, error.message, hint ? { hint } : {});
  }
  return error;
}

async function guard<T>(work: () => Promise<T>): Promise<T> {
  try {
    return await work();
  } catch (error) {
    throw toHttp(error);
  }
}

async function readSeed(seedsDir: string, seedId: string): Promise<Seed> {
  const id = seedIdSchema.parse(seedId);
  let text: string;
  try {
    text = await readFile(join(seedsDir, `${id}.json`), "utf8");
  } catch {
    throw new HttpError("not_found", `The Seed Scroll ${id} was not found`, {
      hint: "Write the Seed Scroll with Prometheus first, or send the idea itself to match",
    });
  }
  const parsed = seedSchema.safeParse(JSON.parse(text));
  if (!parsed.success) throw new HttpError("internal", `The Seed Scroll ${id} on disk could not be read`);
  return parsed.data;
}

export const register: LayerRegistrar = (app, ctx) => {
  const registry = new AtlasRegistry(ctx.paths.atlasBlueprints);
  const starters = () => startersDir(ctx.env);

  implement(app, ctx, "atlas.blueprints.list", () => guard(async () => ({ entries: await registry.list() })));

  implement(app, ctx, "atlas.blueprints.get", ({ params }) =>
    guard(async () => {
      const id = normalizeBlueprintId(params.id);
      const entry = await registry.get(id);
      if (!entry) {
        throw new HttpError("not_found", `${id} is not in your Atlas`, {
          hint: "List your Atlas to see the Blueprints you have, or craft a new one with Prometheus",
        });
      }
      return { entry };
    }),
  );

  implement(app, ctx, "atlas.blueprints.register", ({ body }) => guard(async () => ({ entry: await registry.register(body) })));

  implement(app, ctx, "atlas.match", ({ body }) =>
    guard(async () => {
      let query: MatchQuery;
      if (body.seedId) {
        const seed = await readSeed(ctx.paths.seeds, body.seedId);
        query = { idea: seed.idea, title: seed.title, kind: seed.kind, platforms: seed.platforms, features: seed.features };
      } else {
        query = { idea: body.idea ?? "" };
      }
      if (body.kind) query.kind = body.kind;
      if (body.platforms) query.platforms = body.platforms;
      const outcome = await registry.match(query, body.limit ?? 5);
      return { matches: outcome.matches, needsCrafting: outcome.needsCrafting, inferred: outcome.inferred };
    }),
  );

  implement(app, ctx, "atlas.starters.list", () =>
    guard(async () => {
      const { starters: manifests } = listStarterManifests(starters());
      const entries = await registry.list();
      return {
        starters: manifests.map((manifest) => ({
          name: manifest.name,
          title: manifest.title,
          description: manifest.description,
          kind: manifest.kind,
          platforms: manifest.platforms,
          blueprintIds: entries.filter((entry) => entry.blueprint.starter === manifest.name).map((entry) => entry.blueprint.blueprintId),
        })),
      };
    }),
  );

  implement(app, ctx, "atlas.craft.questions", ({ query }) => {
    const names = listStarterManifests(starters()).starters.map((manifest) => manifest.name);
    return { questions: craftQuestions({ ...(query.idea ? { idea: query.idea } : {}), starters: names }) };
  });

  implement(app, ctx, "atlas.craft", ({ body }) =>
    guard(async () => {
      const names = listStarterManifests(starters()).starters.map((manifest) => manifest.name);
      const outcome = craftBlueprint(body.answers ?? {}, {
        ...(body.idea ? { idea: body.idea } : {}),
        starters: names,
        takenIds: await registry.ids(),
      });
      const entry = body.register && outcome.blueprint ? await registry.register(outcome.blueprint) : undefined;
      return { ...outcome, ...(entry ? { entry } : {}) };
    }),
  );
};
