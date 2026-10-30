import type { RouteName } from "@4ge/contracts";
import { dataPaths, ensureDataDirs, type DataPaths } from "@4ge/contracts/node";
import { EventHub } from "./events.js";

export const FORGE_VERSION = "0.1.0";

/**
 * What every layer's register(app, ctx) receives. Layers keep their own
 * state in their own modules; the context carries only what is shared.
 */
export interface ServerContext {
  version: string;
  startedAt: string;
  /** All 4GE data paths, rooted at dataRoot() (or FORGE_DATA_ROOT). */
  paths: DataPaths;
  /** Publish SessionEvents here; SSE routes stream them. */
  events: EventHub;
  /** The environment the server started with. Read keys from here, never log them. */
  env: Readonly<Record<string, string | undefined>>;
  /** Route names a layer implemented; the rest answer 501. */
  implemented: Set<RouteName>;
  heartbeatMs: number;
  /** Register cleanup (stop child processes, close sandboxes) for shutdown. */
  onClose(cleanup: () => void | Promise<void>): void;
  close(): Promise<void>;
}

export interface ContextOptions {
  dataRoot?: string;
  env?: Record<string, string | undefined>;
  heartbeatMs?: number;
}

export function createContext(options: ContextOptions = {}): ServerContext {
  const paths = ensureDataDirs(dataPaths(options.dataRoot));
  const cleanups: Array<() => void | Promise<void>> = [];
  return {
    version: FORGE_VERSION,
    startedAt: new Date().toISOString(),
    paths,
    events: new EventHub(),
    env: options.env ?? process.env,
    implemented: new Set(),
    heartbeatMs: options.heartbeatMs ?? 15_000,
    onClose(cleanup) {
      cleanups.push(cleanup);
    },
    async close() {
      for (const cleanup of cleanups.splice(0).reverse()) {
        try {
          await cleanup();
        } catch {
          // Shutdown continues; each cleanup owns its own reporting.
        }
      }
    },
  };
}
