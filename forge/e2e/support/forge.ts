import { spawn, type ChildProcess } from "node:child_process";
import { createServer } from "node:net";
import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { createForgeClient, isReservedPort, type ForgeClient } from "@4ge/contracts";

/**
 * Start a real 4GE server (apps/server/dist/main.js) the way Holodex does
 * (ForgeHost.cs): a child node process with its own environment. Each suite
 * gets its own data root under e2e/.data and its own AInbox queue, so tests
 * never touch the person's Atlas, Seeds or builds, and the environment
 * decides the edge case (no model key, a scripted model, Docker unreachable,
 * a PATH without the providers' CLIs).
 */

const here = dirname(fileURLToPath(import.meta.url));
export const FORGE_ROOT = resolve(here, "..", "..");
export const E2E_ROOT = resolve(here, "..");
export const DATA_ROOT = join(E2E_ROOT, ".data", "round-1");
export const SHOTS_ROOT = join(DATA_ROOT, "shots");
const MAIN = join(FORGE_ROOT, "apps", "server", "dist", "main.js");

/** Keys a suite must never inherit from the shell (the suite sets its own). */
const MODEL_KEYS = ["NEBIUS_API_KEY", "NEBIUS_BASE_URL", "HIVEMIND_URL", "HIVEMIND_TOKEN", "FORGE_DATA_ROOT", "FORGE_AINBOX_QUEUE", "FORGE_PORT"];

export interface ForgeServer {
  name: string;
  port: number;
  url: string;
  dataRoot: string;
  ainboxQueue: string;
  api: ForgeClient;
  child: ChildProcess;
  output(): string;
  stop(): Promise<void>;
}

export interface StartForgeOptions {
  /** Short folder name for the data root. */
  name: string;
  port: number;
  env?: Record<string, string | undefined>;
  /** Keep the data root from an earlier run (default: start empty). */
  keepData?: boolean;
  timeoutMs?: number;
}

function baseEnv(): Record<string, string> {
  const env: Record<string, string> = {};
  for (const [key, value] of Object.entries(process.env)) {
    if (value !== undefined && !MODEL_KEYS.includes(key)) env[key] = value;
  }
  return env;
}

/** True when nothing listens on 127.0.0.1:port. */
export function portIsFree(port: number): Promise<boolean> {
  return new Promise((resolve) => {
    const probe = createServer();
    probe.once("error", () => resolve(false));
    probe.listen(port, "127.0.0.1", () => probe.close(() => resolve(true)));
  });
}

export async function startForge(options: StartForgeOptions): Promise<ForgeServer> {
  if (isReservedPort(options.port)) throw new Error(`Port ${options.port} belongs to Ark`);
  // Another server already answering on the port would pass the health check in this one's place.
  if (!(await portIsFree(options.port))) throw new Error(`Port ${options.port} is taken; stop whatever runs there before the ${options.name} suite.`);
  const dataRoot = join(DATA_ROOT, options.name);
  const ainboxQueue = join(DATA_ROOT, `${options.name}-ainbox`);
  if (!options.keepData) {
    rmSync(dataRoot, { recursive: true, force: true });
    rmSync(ainboxQueue, { recursive: true, force: true });
  }
  mkdirSync(dataRoot, { recursive: true });
  mkdirSync(ainboxQueue, { recursive: true });
  const env: Record<string, string> = {
    ...baseEnv(),
    FORGE_DATA_ROOT: dataRoot,
    FORGE_AINBOX_QUEUE: ainboxQueue,
    FORGE_PORT: String(options.port),
  };
  for (const [key, value] of Object.entries(options.env ?? {})) {
    if (value === undefined) delete env[key];
    else env[key] = value;
  }
  const child = spawn(process.execPath, [MAIN], { cwd: FORGE_ROOT, env, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
  let output = "";
  child.stdout?.on("data", (chunk: Buffer) => (output += chunk.toString()));
  child.stderr?.on("data", (chunk: Buffer) => (output += chunk.toString()));
  const url = `http://127.0.0.1:${options.port}`;
  const api = createForgeClient({ baseUrl: url });
  const deadline = Date.now() + (options.timeoutMs ?? 60_000);
  let exited: number | null = null;
  child.once("exit", (code) => (exited = code ?? 0));
  for (;;) {
    if (exited !== null) throw new Error(`The 4GE server ${options.name} exited (${exited}): ${output.slice(-1500)}`);
    try {
      const response = await fetch(`${url}/api/health`);
      if (response.ok) break;
    } catch {
      // not listening yet
    }
    if (Date.now() > deadline) {
      child.kill();
      throw new Error(`The 4GE server ${options.name} did not answer /api/health on ${url}: ${output.slice(-1500)}`);
    }
    await new Promise((done) => setTimeout(done, 300));
  }
  const server: ForgeServer = {
    name: options.name,
    port: options.port,
    url,
    dataRoot,
    ainboxQueue,
    api,
    child,
    output: () => output,
    async stop() {
      if (child.exitCode !== null) return;
      await new Promise<void>((done) => {
        const timer = setTimeout(() => {
          child.kill("SIGKILL");
          done();
        }, 15_000);
        child.once("exit", () => {
          clearTimeout(timer);
          done();
        });
        child.kill("SIGTERM");
      });
      writeFileSync(join(DATA_ROOT, `${options.name}.server.log`), output);
    },
  };
  return server;
}

/** Run a server that is expected to exit (port taken); resolve with its code and output. */
export async function runForgeExpectingExit(options: StartForgeOptions): Promise<{ code: number | null; output: string }> {
  const dataRoot = join(DATA_ROOT, options.name);
  mkdirSync(dataRoot, { recursive: true });
  const env: Record<string, string> = { ...baseEnv(), FORGE_DATA_ROOT: dataRoot, FORGE_PORT: String(options.port), ...(options.env as Record<string, string>) };
  const child = spawn(process.execPath, [MAIN], { cwd: FORGE_ROOT, env, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
  let output = "";
  child.stdout?.on("data", (chunk: Buffer) => (output += chunk.toString()));
  child.stderr?.on("data", (chunk: Buffer) => (output += chunk.toString()));
  const code = await new Promise<number | null>((done) => {
    const timer = setTimeout(() => {
      child.kill();
      done(null);
    }, options.timeoutMs ?? 30_000);
    child.once("exit", (exitCode) => {
      clearTimeout(timer);
      done(exitCode);
    });
  });
  return { code, output };
}

/** Folder for this round's screenshots (git-ignored: adapter tiles can name a signed-in account). */
export function shotPath(name: string): string {
  mkdirSync(SHOTS_ROOT, { recursive: true });
  return join(SHOTS_ROOT, `${name}.png`);
}

/** The scripted model's environment for a server. */
export function stubModelEnv(baseUrl: string): Record<string, string> {
  return { NEBIUS_API_KEY: "e2e-stub-key-not-a-secret", NEBIUS_BASE_URL: baseUrl };
}
