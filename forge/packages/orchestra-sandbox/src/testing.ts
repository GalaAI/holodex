import { mkdirSync, writeFileSync } from "node:fs";
import { createServer } from "node:net";
import { join } from "node:path";
import type { CommandResult, DockerCli, ExecOptions } from "./docker.js";

/**
 * A scripted Docker for tests: records every command and answers from a
 * handler. Not exported from the package index; tests import it directly.
 */
export interface FakeDocker extends DockerCli {
  calls: Array<{ args: string[]; env: Record<string, string> }>;
}

export function fakeDocker(
  handler: (args: string[], options: ExecOptions) => Partial<CommandResult> | Promise<Partial<CommandResult>>,
): FakeDocker {
  const calls: FakeDocker["calls"] = [];
  return {
    calls,
    async exec(args, options = {}) {
      calls.push({ args, env: options.env ?? {} });
      const result = await handler(args, options);
      return { code: 0, stdout: "", stderr: "", timedOut: false, ...result };
    },
  };
}

/** A Docker that is installed and running, with every compose command succeeding. */
export function healthyDocker(onBots?: (flightDir: string) => void): FakeDocker {
  return fakeDocker((args, options) => {
    if (args[0] === "version") return { stdout: "29.9.2\n" };
    if (args[0] === "compose" && args[1] === "version") return { stdout: "5.6.1\n" };
    if (args.includes("ps")) return { stdout: "" };
    if (args.includes("run") && args.includes("bots")) {
      const dir = options.env?.FORGE_SANDBOX_FLIGHT;
      if (dir && onBots) onBots(dir);
      return { stdout: "" };
    }
    return { stdout: "" };
  });
}

export async function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const server = createServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      const port = typeof address === "object" && address ? address.port : 0;
      server.close(() => resolve(port));
    });
  });
}

/** Write bot results the way forge/sandbox/bots/bot.mjs does. */
export function writeBotResults(dir: string, options: { failStep?: boolean; a11yProblem?: boolean; badRoute?: boolean } = {}): void {
  mkdirSync(join(dir, "customer"), { recursive: true });
  writeFileSync(join(dir, "customer", "01-open.png"), "png");
  const page = (path: string) => ({
    path,
    status: options.badRoute && path === "/missing" ? 404 : 200,
    title: "Ink and Iron",
    problems: options.a11yProblem && path === "/" ? ["1 image(s) have no alt text"] : [],
  });
  const persona = (id: string, role: string, entryPath: string, paths: string[]) => ({
    personaId: id,
    role,
    entryPath,
    steps: [
      {
        id: `${id}-01`,
        personaId: id,
        kind: "open",
        action: `Open ${entryPath}`,
        expectation: "The page loads",
        status: "passed",
        screenshot: `${id}/01-open.png`,
        at: new Date().toISOString(),
      },
      {
        id: `${id}-02`,
        personaId: id,
        kind: "link",
        action: "Follow the link",
        expectation: "It opens",
        status: options.failStep && id === "customer" ? "failed" : "passed",
        ...(options.failStep && id === "customer" ? { error: "/book answered 500" } : {}),
        at: new Date().toISOString(),
      },
      {
        id: `${id}-03`,
        personaId: id,
        kind: "accessibility",
        action: "Check accessibility basics",
        expectation: "Basics hold",
        status: options.a11yProblem ? "failed" : "passed",
        at: new Date().toISOString(),
      },
    ],
    pages: paths.map(page),
    consoleErrors: [],
    pageErrors: [],
    failedRequests: [],
  });
  writeFileSync(
    join(dir, "results.json"),
    JSON.stringify({
      schemaVersion: 1,
      flightId: "tfl_test",
      startedAt: new Date().toISOString(),
      finishedAt: new Date().toISOString(),
      personas: [
        persona("staff", "staff", "/admin", ["/admin", "/"]),
        persona("customer", "customer", "/", options.badRoute ? ["/", "/missing"] : ["/", "/contact"]),
      ],
      artifacts: [],
      issues: [],
    }),
  );
}
