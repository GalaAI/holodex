import type { ServerLayer } from "@4ge/contracts";
import type { Hono } from "hono";
import type { ServerContext } from "./context.js";
import { register as atlas } from "./routes/atlas.js";
import { register as design } from "./routes/design.js";
import { register as library } from "./routes/library.js";
import { register as mods } from "./routes/mods.js";
import { register as orchestra } from "./routes/orchestra.js";
import { register as sandbox } from "./routes/sandbox.js";

/**
 * Layer slots. Each layer owns apps/server/src/routes/<layer>.ts and exports
 * `register(app, ctx)`, which adds that layer's routes with implement() and
 * implementStream(). Routes in the @4ge/contracts table that no layer has
 * implemented answer 501 not_implemented, so the studio can say plainly what
 * is not connected yet.
 */
export type LayerRegistrar = (app: Hono, ctx: ServerContext) => void | Promise<void>;

export const LAYER_REGISTRARS: Record<Exclude<ServerLayer, "core">, LayerRegistrar> = {
  atlas,
  orchestra,
  design,
  mods,
  sandbox,
  library,
};
