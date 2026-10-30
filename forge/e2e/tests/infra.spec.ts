import { mkdirSync, writeFileSync } from "node:fs";
import { createServer, type Server } from "node:net";
import { join } from "node:path";
import { expect, test } from "@playwright/test";
import { runForgeExpectingExit, startForge, stubModelEnv, type ForgeServer } from "../support/forge.js";
import { startModelStub, type ModelStub } from "../support/model-stub.js";
import { sandboxDown } from "../support/sandbox.js";
import { checkScreen } from "../support/visual.js";

/**
 * The machine around 4GE: a port already taken (the server's own, and a
 * sandbox port) and Docker down. Each runs against a real server; Docker
 * "down" is the real Docker CLI pointed at an address where no engine
 * answers (DOCKER_HOST), which is exactly what the CLI sees when Docker
 * Desktop is stopped.
 */

function hold(port: number): Promise<Server> {
  return new Promise((resolve, reject) => {
    const blocker = createServer();
    blocker.once("error", reject);
    blocker.listen(port, "127.0.0.1", () => resolve(blocker));
  });
}

const release = (blocker: Server | undefined) => new Promise<void>((resolve) => (blocker ? blocker.close(() => resolve()) : resolve()));

test("the server's port is taken: 4GE exits and says what to do", async () => {
  const blocker = await hold(17407);
  try {
    const result = await runForgeExpectingExit({ name: "port-taken", port: 17407, env: { NEBIUS_API_KEY: undefined } });
    expect(result.code).toBe(1);
    expect(result.output).toContain("Port 17407 is already in use, so the 4GE server cannot start.");
    expect(result.output).toContain("set FORGE_PORT to a free port");
  } finally {
    await release(blocker);
  }
});

test("an Ark port is refused before anything binds it", async () => {
  const result = await runForgeExpectingExit({ name: "ark-port", port: 17407, env: { FORGE_PORT: "8788", NEBIUS_API_KEY: undefined } });
  expect(result.code).toBe(1);
  expect(result.output).toContain("Port 8788 belongs to Ark and 4GE must not bind it.");
});

test.describe("a sandbox port is taken", () => {
  let stub: ModelStub;
  let server: ForgeServer;
  let blocker: Server | undefined;
  let buildId = "";

  test.beforeAll(async () => {
    await sandboxDown();
    stub = await startModelStub();
    server = await startForge({ name: "sandbox-port", port: 17407, env: stubModelEnv(stub.baseUrl) });
    // A real build to put in the sandbox, made through the same API the studio uses.
    const opened = await server.api.call("imagistation.sessions.create", { body: { idea: "A website for my tattoo shop" } });
    await server.api.call("imagistation.messages.send", { params: { id: opened.session.id }, body: { content: "Adults in Portland; ten booking requests a week." } });
    const { seed } = await server.api.call("imagistation.seed", { params: { id: opened.session.id } });
    const decided = await server.api.call("seeds.decide", { params: { id: seed.id }, body: { when: "now" } });
    buildId = decided.build?.id ?? "";
    blocker = await hold(17410);
  });

  test.afterAll(async () => {
    await release(blocker);
    if (server && buildId) {
      await server.api.call("builds.cancel", { params: { id: buildId } }).catch(() => undefined);
    }
    await server?.stop();
    await stub?.close();
  });

  test("the Sandbox page shows the taken port and starting refuses in plain words", async ({ page }) => {
    const status = await server.api.call("sandbox.status", {});
    expect(status.ports.find((port) => port.port === 17410)?.free).toBe(false);
    const refused = await server.api.call("sandbox.start", { body: { buildId } }).then(
      () => "started",
      (error: Error & { code?: string }) => `${error.code}: ${error.message}`,
    );
    expect(refused).toBe("port_taken: Port 17410 (sandbox app) is in use by another program, so the sandbox cannot start.");

    await page.goto(`${server.url}/sandbox/${buildId}`);
    await expect(page.getByTestId("docker-state")).toContainText("app 17410 taken", { timeout: 20_000 });
    await page.getByTestId("sandbox-start").click();
    await expect(page.getByText("Port 17410 (sandbox app) is in use by another program, so the sandbox cannot start.")).toBeVisible({ timeout: 20_000 });
    await checkScreen(page, "sandbox-port-taken");
  });
});

test.describe("Docker is down", () => {
  let server: ForgeServer;

  test.beforeAll(async () => {
    server = await startForge({ name: "docker-down", port: 17408, env: { DOCKER_HOST: "tcp://127.0.0.1:1", NEBIUS_API_KEY: undefined } });
  });
  test.afterAll(async () => server?.stop());

  test("the Sandbox page says Docker is not running and will not start", async ({ page }) => {
    const status = await server.api.call("sandbox.status", {});
    expect(status.docker.available).toBe(false);
    // An app folder to put in the sandbox, so the only thing missing is Docker.
    const appDir = join(server.dataRoot, "builds", "bld_dockerdown01", "app");
    mkdirSync(appDir, { recursive: true });
    writeFileSync(join(appDir, "index.html"), "<!doctype html><html><body><main>Docker down</main></body></html>");
    const refused = await server.api.call("sandbox.start", { body: { buildId: "bld_dockerdown01" } }).then(
      () => "started",
      (error: Error & { code?: string; hint?: string }) => `${error.code}: ${error.hint ?? ""}`,
    );
    expect(refused).toBe("docker_unavailable: The sandbox runs on Docker Desktop on this PC. Start it and try again.");

    await page.goto(`${server.url}/sandbox`);
    const docker = page.getByTestId("docker-state");
    await expect(docker).toContainText("Docker is not running", { timeout: 20_000 });
    await checkScreen(page, "sandbox-docker-down");
  });

  test("the Docker adapter in the Library says Docker is not running", async ({ page }) => {
    const { adapters } = await server.api.call("adapters.list", { query: { refresh: "true" } });
    const docker = adapters.find((adapter) => adapter.id === "adapter:docker");
    expect(docker?.status).toBe("error");
    expect(docker?.statusMessage).toBe("Docker is installed but not running. Start Docker Desktop.");
    await page.goto(`${server.url}/library`);
    await expect(page.locator('section[data-origin="adapter"] .lib-tile').filter({ hasText: "Docker" }).first()).toContainText("not running");
  });
});
