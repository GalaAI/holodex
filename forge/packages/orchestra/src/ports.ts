import {
  API_PREFIX,
  ROUTES,
  apiErrorSchema,
  buildPath,
  type AtlasEntry,
  type Blueprint,
  type BlueprintMatch,
  type ComponentInventoryItem,
  type DesignSystem,
  type RouteName,
  type SandboxRun,
  type StartTestFlightRequest,
  type TestFlight,
} from "@4ge/contracts";
import { OrchestraError } from "./errors.js";

/**
 * What the orchestra needs from the other layers. It never imports their
 * packages: the server hands it ports that call the other layers' routes
 * in-process (the same route table the studio uses), so the orchestra works
 * with whatever is connected and says plainly what is not.
 */
export interface AtlasPort {
  list(): Promise<AtlasEntry[]>;
  get(blueprintId: string): Promise<AtlasEntry | undefined>;
  register(blueprint: Blueprint): Promise<AtlasEntry>;
  /** Matches, best first, and the Atlas's own verdict on whether any fits. */
  match(seedId: string): Promise<{ matches: BlueprintMatch[]; needsCrafting?: boolean }>;
}

export interface DesignPort {
  saveSystem(system: DesignSystem): Promise<void>;
  saveInventoryItem(item: ComponentInventoryItem): Promise<void>;
}

export interface SandboxPort {
  /** Starts (or returns this build's open run); the run comes back "starting" while it comes up. */
  start(buildId: string): Promise<SandboxRun>;
  getRun(runId: string): Promise<SandboxRun>;
  stop(runId: string): Promise<SandboxRun>;
  startTestFlight(request: StartTestFlightRequest): Promise<TestFlight>;
  getTestFlight(id: string): Promise<TestFlight>;
}

/** A layer that has not registered its routes yet. */
export class LayerNotConnected extends OrchestraError {
  readonly layer: string;
  constructor(layer: string, message: string) {
    super("not_implemented", message);
    this.layer = layer;
  }
}

export type InProcessRequest = (path: string, init?: RequestInit) => Response | Promise<Response>;

async function call<T>(
  request: InProcessRequest,
  name: RouteName,
  options: { params?: Record<string, string>; body?: unknown; query?: Record<string, string> } = {},
): Promise<T> {
  const def = ROUTES[name];
  let path = `${API_PREFIX}${buildPath(def.path, options.params ?? {})}`;
  if (options.query && Object.keys(options.query).length > 0) path += `?${new URLSearchParams(options.query)}`;
  const response = await request(path, {
    method: def.method,
    headers: { "content-type": "application/json" },
    ...(options.body !== undefined ? { body: JSON.stringify(options.body) } : {}),
  });
  const text = await response.text();
  let json: unknown = undefined;
  try {
    json = text ? JSON.parse(text) : undefined;
  } catch {
    // handled below
  }
  if (!response.ok) {
    const parsed = apiErrorSchema.safeParse(json);
    const code = parsed.success ? parsed.data.error.code : "upstream_error";
    const message = parsed.success ? parsed.data.error.message : `${name} answered ${response.status}`;
    if (code === "not_implemented") throw new LayerNotConnected(def.layer, message);
    throw new OrchestraError(code, message, parsed.success ? parsed.data.error.hint : undefined);
  }
  const checked = def.response.safeParse(json);
  if (!checked.success) throw new OrchestraError("upstream_error", `The ${def.layer} layer answered ${name} with data that does not match the contract`);
  return checked.data as T;
}

export function routeAtlas(request: InProcessRequest): AtlasPort {
  return {
    async list() {
      return (await call<{ entries: AtlasEntry[] }>(request, "atlas.blueprints.list")).entries;
    },
    async get(blueprintId) {
      try {
        return (await call<{ entry: AtlasEntry }>(request, "atlas.blueprints.get", { params: { id: blueprintId } })).entry;
      } catch (error) {
        if (error instanceof OrchestraError && error.code === "not_found") return undefined;
        throw error;
      }
    },
    async register(blueprint) {
      return (await call<{ entry: AtlasEntry }>(request, "atlas.blueprints.register", { body: blueprint })).entry;
    },
    async match(seedId) {
      return call<{ matches: BlueprintMatch[]; needsCrafting?: boolean }>(request, "atlas.match", { body: { seedId } });
    },
  };
}

export function routeDesign(request: InProcessRequest): DesignPort {
  return {
    async saveSystem(system) {
      await call(request, "design.systems.save", { params: { id: system.id }, body: system });
    },
    async saveInventoryItem(item) {
      await call(request, "design.inventory.save", { params: { id: item.id }, body: item });
    },
  };
}

export function routeSandbox(request: InProcessRequest): SandboxPort {
  return {
    async start(buildId) {
      return (await call<{ run: SandboxRun }>(request, "sandbox.start", { body: { buildId } })).run;
    },
    async stop(runId) {
      return (await call<{ run: SandboxRun }>(request, "sandbox.stop", { body: { runId } })).run;
    },
    async getRun(runId) {
      return (await call<{ run: SandboxRun }>(request, "sandbox.runs.get", { params: { id: runId } })).run;
    },
    async startTestFlight(body) {
      return (await call<{ testFlight: TestFlight }>(request, "sandbox.testFlight.start", { body })).testFlight;
    },
    async getTestFlight(id) {
      return (await call<{ testFlight: TestFlight }>(request, "sandbox.testFlight.get", { params: { id } })).testFlight;
    },
  };
}
