import { spawn } from "node:child_process";
import { createServer } from "node:net";

/**
 * The Docker CLI, behind an interface so tests can replace it. 4GE talks to
 * the user's own Docker Desktop (or Docker Engine) through `docker` and
 * `docker compose`; it never reaches a Docker socket directly.
 */
export interface CommandResult {
  code: number;
  stdout: string;
  stderr: string;
  timedOut: boolean;
  /** Set when the docker binary itself could not be started (not installed). */
  spawnError?: string;
}

export interface ExecOptions {
  env?: Record<string, string>;
  cwd?: string;
  timeoutMs?: number;
  signal?: AbortSignal;
}

export interface DockerCli {
  exec(args: string[], options?: ExecOptions): Promise<CommandResult>;
}

const OUTPUT_LIMIT = 1_000_000;

/**
 * The environment docker runs with: the server's environment minus anything
 * that looks like a credential (the user's model keys never reach compose),
 * plus the sandbox's own variables.
 */
export function dockerEnv(extra: Record<string, string> = {}, base: NodeJS.ProcessEnv = process.env): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = {};
  for (const [key, value] of Object.entries(base)) {
    if (value === undefined) continue;
    if (/KEY|TOKEN|SECRET|PASSWORD|CREDENTIAL/i.test(key) && !/^DOCKER_|^COMPOSE_/.test(key)) continue;
    env[key] = value;
  }
  return { ...env, ...extra };
}

export function createDockerCli(binary = "docker"): DockerCli {
  return {
    exec(args, options = {}) {
      return new Promise<CommandResult>((resolve) => {
        let stdout = "";
        let stderr = "";
        let timedOut = false;
        let settled = false;
        const finish = (result: CommandResult) => {
          if (settled) return;
          settled = true;
          clearTimeout(timer);
          options.signal?.removeEventListener("abort", onAbort);
          resolve(result);
        };
        const child = spawn(binary, args, {
          cwd: options.cwd,
          env: dockerEnv(options.env ?? {}),
          windowsHide: true,
          stdio: ["ignore", "pipe", "pipe"],
        });
        child.stdout.setEncoding("utf8");
        child.stderr.setEncoding("utf8");
        child.stdout.on("data", (chunk: string) => {
          if (stdout.length < OUTPUT_LIMIT) stdout += chunk;
        });
        child.stderr.on("data", (chunk: string) => {
          if (stderr.length < OUTPUT_LIMIT) stderr += chunk;
        });
        const onAbort = () => {
          child.kill();
        };
        options.signal?.addEventListener("abort", onAbort, { once: true });
        const timer = setTimeout(() => {
          timedOut = true;
          child.kill();
        }, options.timeoutMs ?? 120_000);
        child.on("error", (error: NodeJS.ErrnoException) => {
          finish({ code: -1, stdout, stderr, timedOut, spawnError: error.code ?? error.message });
        });
        child.on("close", (code) => {
          finish({ code: code ?? -1, stdout, stderr, timedOut });
        });
      });
    },
  };
}

export interface DockerAvailability {
  available: boolean;
  version?: string;
  message?: string;
}

/** Is Docker installed and its engine running? Plain words when not. */
export async function checkDocker(docker: DockerCli): Promise<DockerAvailability> {
  const result = await docker.exec(["version", "--format", "{{.Server.Version}}"], { timeoutMs: 15_000 });
  if (result.spawnError === "ENOENT") {
    return {
      available: false,
      message:
        "Docker is not installed on this PC. Install Docker Desktop (https://www.docker.com/products/docker-desktop/), start it, then try again.",
    };
  }
  if (result.timedOut) {
    return { available: false, message: "Docker did not answer within 15 seconds. Make sure Docker Desktop has finished starting, then try again." };
  }
  if (result.code !== 0 || result.stdout.trim().length === 0) {
    return {
      available: false,
      message: "Docker is installed but its engine is not running. Start Docker Desktop, wait until it says it is running, then try again.",
    };
  }
  const compose = await docker.exec(["compose", "version", "--short"], { timeoutMs: 15_000 });
  if (compose.code !== 0) {
    return {
      available: false,
      version: result.stdout.trim(),
      message: "Docker Compose is missing. Update Docker Desktop (it includes Compose v2), then try again.",
    };
  }
  return { available: true, version: `Engine ${result.stdout.trim()}, Compose ${compose.stdout.trim()}` };
}

/** True when nothing listens on host:port. */
export function isPortFree(port: number, host = "127.0.0.1"): Promise<boolean> {
  return new Promise((resolve) => {
    const server = createServer();
    server.unref();
    server.once("error", () => resolve(false));
    server.listen({ port, host, exclusive: true }, () => {
      server.close(() => resolve(true));
    });
  });
}

/** Last lines of a log, for an error message a person can read. */
export function tail(text: string, lines = 12, maxChars = 1500): string {
  const kept = text.trim().split(/\r?\n/).slice(-lines).join("\n");
  return kept.length > maxChars ? kept.slice(kept.length - maxChars) : kept;
}
