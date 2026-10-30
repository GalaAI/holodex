/**
 * @4ge/mods: mods, adapters and augments for 4GE.
 *
 * Catalogs (catalog/), the Windows Credential Manager store (credentials.ts),
 * CLI detection (detect.ts), connect and action runners (actions.ts), mod
 * manifests in the 4GE, MCP client and Claude Code plugin formats
 * (manifest.ts), mods and augments on disk (service.ts) and the Library
 * grouped by origin (library.ts). README.md documents the formats and the
 * docs each one follows.
 */
import { join } from "node:path";
import type { DataPaths } from "@4ge/contracts/node";
import { ExecaRunner, type CommandRunner } from "./cli.js";
import { defaultCredentialStore, type CredentialStore } from "./credentials.js";
import { AdapterDetector } from "./detect.js";
import { ModService } from "./service.js";

export * from "./catalog/adapters.js";
export * from "./catalog/augments.js";
export * from "./catalog/mods.js";
export * from "./cli.js";
export * from "./credentials.js";
export * from "./detect.js";
export * from "./actions.js";
export * from "./manifest.js";
export * from "./service.js";
export * from "./library.js";

export interface ModsRuntime {
  detector: AdapterDetector;
  service: ModService;
  credentials: CredentialStore;
  runner: CommandRunner;
}

export interface ModsRuntimeOptions {
  paths: DataPaths;
  env: Readonly<Record<string, string | undefined>>;
  credentials?: CredentialStore;
  runner?: CommandRunner;
  fetch?: typeof fetch;
  cacheMs?: number;
}

/** One runtime per server: shared detection cache, credential store and runner. */
export function createModsRuntime(options: ModsRuntimeOptions): ModsRuntime {
  const credentials = options.credentials ?? defaultCredentialStore();
  const runner = options.runner ?? new ExecaRunner(options.env);
  const detector = new AdapterDetector(
    { runner, credentials, env: options.env, ...(options.fetch ? { fetch: options.fetch } : {}) },
    options.cacheMs ?? 60_000,
    undefined,
    join(options.paths.adapters, "detected.json"),
  );
  const service = new ModService(options.paths, detector);
  return { detector, service, credentials, runner };
}
