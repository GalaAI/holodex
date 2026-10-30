import { execFile } from "node:child_process";
import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { promisify } from "node:util";
import { DATA_ROOT, FORGE_ROOT } from "./forge.js";

const run = promisify(execFile);

/**
 * Bring the 4ge-sandbox compose project down outside any server, so a suite
 * starts and ends with the sandbox ports (17410 to 17412) free. Uses the
 * same compose file and project name as @4ge/orchestra-sandbox.
 */
export async function sandboxDown(): Promise<void> {
  const empty = join(DATA_ROOT, "empty-source");
  mkdirSync(empty, { recursive: true });
  await run("docker", ["compose", "-p", "4ge-sandbox", "-f", join(FORGE_ROOT, "sandbox", "compose.yaml"), "--profile", "bots", "down", "-v", "--remove-orphans"], {
    env: { ...process.env, FORGE_SANDBOX_SOURCE: empty },
    timeout: 120_000,
    windowsHide: true,
  }).catch(() => undefined);
}

/** Poll until a check returns a value, or fail with the last value seen. */
export async function waitFor<T>(what: string, read: () => Promise<T>, done: (value: T) => boolean, timeoutMs: number, everyMs = 2000): Promise<T> {
  const deadline = Date.now() + timeoutMs;
  let last: T | undefined;
  for (;;) {
    last = await read();
    if (done(last)) return last;
    if (Date.now() > deadline) throw new Error(`Timed out after ${Math.round(timeoutMs / 1000)}s waiting for ${what}. Last: ${JSON.stringify(last).slice(0, 1500)}`);
    await new Promise((resolve) => setTimeout(resolve, everyMs));
  }
}
