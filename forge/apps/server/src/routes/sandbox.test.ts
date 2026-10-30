import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { apiErrorSchema, sandboxStatusSchema } from "@4ge/contracts";
import { dataPaths } from "@4ge/contracts/node";
import { SandboxManager } from "@4ge/orchestra-sandbox";
import { fakeDocker, freePort, healthyDocker, writeBotResults } from "@4ge/orchestra-sandbox/testing";
import { afterAll, describe, expect, it } from "vitest";
import { createApp } from "../app.js";
import { registerSandbox } from "./sandbox.js";

const root = mkdtempSync(join(tmpdir(), "4ge-sandbox-routes-"));
afterAll(() => rmSync(root, { recursive: true, force: true }));
const BUILD = "bld_routes";

async function appWith(docker: ReturnType<typeof fakeDocker>, name: string) {
  const dataRoot = join(root, name);
  const paths = dataPaths(dataRoot);
  const ports = { app: await freePort(), cdp: await freePort(), viewer: await freePort() };
  const manager = new SandboxManager({ paths, docker, ports });
  const forge = await createApp({
    dataRoot,
    studioDist: join(root, "no-studio"),
    layers: { sandbox: (app, ctx) => registerSandbox(app, ctx, manager) },
  });
  mkdirSync(paths.build(BUILD).app, { recursive: true });
  writeFileSync(join(paths.build(BUILD).app, "index.html"), "<!doctype html><title>t</title>");
  const call = (method: string, path: string, body?: unknown) =>
    forge.app.fetch(
      new Request(`http://127.0.0.1/api${path}`, {
        method,
        ...(body === undefined ? {} : { body: JSON.stringify(body), headers: { "content-type": "application/json" } }),
      }),
    );
  return { forge, manager, call };
}

describe("sandbox routes", () => {
  it("answers 503 docker_unavailable with plain words when Docker is down", async () => {
    const { call, forge } = await appWith(
      fakeDocker((args) => (args[0] === "version" ? { code: 1, stderr: "cannot connect" } : {})),
      "down",
    );
    const response = await call("POST", "/sandbox/start", { buildId: BUILD });
    expect(response.status).toBe(503);
    const body = apiErrorSchema.parse(await response.json());
    expect(body.error.code).toBe("docker_unavailable");
    expect(body.error.message).toContain("Docker Desktop");
    const status = sandboxStatusSchema.parse(await (await call("GET", "/sandbox/status")).json());
    expect(status.docker.available).toBe(false);
    await forge.ctx.close();
  });

  it("runs start, test flight and stop through the API and streams events", async () => {
    const { call, forge, manager } = await appWith(
      healthyDocker((dir) => writeBotResults(dir)),
      "up",
    );
    const types: string[] = [];
    forge.ctx.events.subscribe(() => true, (event) => types.push(event.type));
    const started = await call("POST", "/sandbox/start", { buildId: BUILD });
    expect(started.status).toBe(200);
    const { run } = (await started.json()) as { run: { id: string; status: string } };
    expect(run.status).toBe("starting");
    await manager.waitFor(run.id);
    const got = (await (await call("GET", `/sandbox/runs/${run.id}`)).json()) as { run: { status: string } };
    expect(got.run.status).toBe("running");
    const listed = (await (await call("GET", `/sandbox/runs?buildId=${BUILD}`)).json()) as { runs: unknown[] };
    expect(listed.runs).toHaveLength(1);

    const flown = await call("POST", "/sandbox/test-flight", { buildId: BUILD });
    expect(flown.status).toBe(200);
    const { testFlight } = (await flown.json()) as { testFlight: { id: string } };
    await manager.waitFor(testFlight.id);
    const flight = (await (await call("GET", `/sandbox/test-flight/${testFlight.id}`)).json()) as {
      testFlight: { status: string; steps: unknown[] };
    };
    expect(flight.testFlight.status).toBe("passed");
    expect(flight.testFlight.steps.length).toBeGreaterThan(0);

    const stopped = (await (await call("POST", "/sandbox/stop", { runId: run.id })).json()) as { run: { status: string } };
    expect(stopped.run.status).toBe("stopped");
    expect(types).toEqual(expect.arrayContaining(["sandbox.updated", "testflight.updated"]));

    const missing = await call("GET", "/sandbox/runs/run_nope");
    expect(missing.status).toBe(404);
    await forge.ctx.close();
  });
});
