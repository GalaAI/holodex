import { mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { createServer, type Server } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { receiptSchema, type Build, type Persona } from "@4ge/contracts";
import { dataPaths, ensureDataDirs, type DataPaths } from "@4ge/contracts/node";
import { loadReceipts } from "@4ge/gate";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { botResultsSchema, categoryFor, evaluateFlight, pathDeliverables } from "./checks.js";
import { SandboxError } from "./errors.js";
import { SandboxManager, applyTestFlightToBuild, type SandboxEventDraft } from "./manager.js";
import { defaultPersonas, personasFor } from "./personas.js";
import { fakeDocker, freePort, healthyDocker, writeBotResults } from "./testing.js";

const BUILD = "bld_sandboxtest";
const NOW = "2026-10-30T12:00:00.000Z";

let root: string;
let paths: DataPaths;

function writeApp(): void {
  const app = paths.build(BUILD).app;
  mkdirSync(join(app, "admin"), { recursive: true });
  writeFileSync(join(app, "index.html"), "<!doctype html><title>x</title>");
  writeFileSync(join(app, "admin", "index.html"), "<!doctype html><title>admin</title>");
}

function buildRecord(): Build {
  return {
    id: BUILD,
    seedId: "seed_sandboxtest",
    blueprintId: "blueprint:tattoo-site",
    title: "Ink and Iron",
    status: "test-flight",
    stages: [],
    layers: [
      {
        id: "lyr_ui",
        buildId: BUILD,
        key: "landing-page",
        kind: "interface",
        stage: "interface",
        agent: "smith",
        title: "Landing page",
        status: "passed",
        dependsOn: [],
        previews: [],
        testFlight: "not-run",
      },
      {
        id: "lyr_api",
        buildId: BUILD,
        key: "booking-api",
        kind: "backend",
        stage: "architecture",
        agent: "hephaestus",
        title: "Booking API",
        status: "passed",
        dependsOn: [],
        previews: [],
        testFlight: "not-run",
      },
      {
        id: "lyr_later",
        buildId: BUILD,
        key: "social",
        kind: "integrations",
        stage: "orchestration",
        agent: "athena",
        title: "Social posting",
        status: "pending",
        dependsOn: [],
        previews: [],
        testFlight: "not-run",
      },
    ],
    sprints: [],
    createdAt: NOW,
    updatedAt: NOW,
  };
}

async function portsFor(): Promise<{ app: number; cdp: number; viewer: number }> {
  return { app: await freePort(), cdp: await freePort(), viewer: await freePort() };
}

beforeEach(() => {
  root = mkdtempSync(join(tmpdir(), "4ge-sandbox-"));
  paths = ensureDataDirs(dataPaths(root));
});

afterEach(() => {
  rmSync(root, { recursive: true, force: true });
});

describe("personas", () => {
  it("derives one staff and one customer from the build kind", () => {
    const personas = defaultPersonas("website");
    expect(personas.map((persona) => persona.role)).toEqual(["staff", "customer"]);
    expect(personas.every((persona) => persona.goals.length > 0)).toBe(true);
  });

  it("uses the Blueprint's personas and adds the missing side", () => {
    const customer: Persona = {
      id: "regular",
      role: "customer",
      name: "Regular client",
      brief: "Books a touch-up.",
      goals: ["Book a touch-up"],
      script: [{ action: "click", target: "Book" }],
    };
    const personas = personasFor({ blueprint: { kind: "website", personas: [customer] } });
    expect(personas.map((persona) => persona.id)).toEqual(["regular", "staff"]);
    expect(personas[0]?.script?.[0]?.target).toBe("Book");
  });

  it("keeps an explicit request as given", () => {
    const only = defaultPersonas("game").filter((persona) => persona.role === "customer");
    expect(personasFor({ requested: only, kind: "game" })).toHaveLength(1);
  });
});

describe("checks", () => {
  it("maps Blueprint check sentences onto automated checks", () => {
    expect(categoryFor("Every image has alt text")).toBe("accessibility");
    expect(categoryFor("A customer can submit the booking form")).toBe("ui-flows");
    expect(categoryFor("GET /api/health returns 200")).toBe("routes");
    expect(categoryFor("The staff page opens")).toBe("routes");
    expect(categoryFor("npm run build succeeds")).toBe("build");
    expect(categoryFor("The tone feels warm")).toBeNull();
  });

  it("only treats file-like deliverables as paths", () => {
    expect(pathDeliverables(["index.html", "src/api/book.ts", "booking API", "{{slug}}/x.md", "../escape.js"])).toEqual([
      "index.html",
      "src/api/book.ts",
    ]);
  });

  it("greens a layer only when every check that applies passes, and writes valid receipts", () => {
    writeApp();
    const dir = join(root, "flight");
    mkdirSync(dir, { recursive: true });
    writeBotResults(dir, { a11yProblem: true });
    const results = botResultsSchema.parse(JSON.parse(readFileSync(join(dir, "results.json"), "utf8")));
    const plans = new Map([
      [
        "landing-page",
        {
          key: "landing-page",
          kind: "interface" as const,
          title: "Landing",
          agent: "smith" as const,
          description: "Landing",
          dependsOn: [],
          deliverables: ["index.html", "hero section"],
          checks: ["Images have alt text", "The tone feels warm"],
        },
      ],
    ]);
    const evaluation = evaluateFlight({
      flightId: "tfl_x",
      appReady: true,
      results,
      build: buildRecord(),
      plans,
      appDir: paths.build(BUILD).app,
      evidenceRel: "evidence/tfl_x",
      now: () => NOW,
    });
    expect(evaluation.categories.build.passed).toBe(true);
    expect(evaluation.categories.routes.passed).toBe(true);
    expect(evaluation.categories["ui-flows"].passed).toBe(true);
    expect(evaluation.categories.accessibility.passed).toBe(false);
    const ui = evaluation.layers.find((layer) => layer.layerId === "lyr_ui");
    const api = evaluation.layers.find((layer) => layer.layerId === "lyr_api");
    const later = evaluation.layers.find((layer) => layer.layerId === "lyr_later");
    expect(ui?.status).toBe("failed");
    expect(ui?.notAutomated).toEqual(["The tone feels warm"]);
    expect(api?.status).toBe("passed");
    expect(later?.status).toBe("not-covered");
    expect(later?.receipt).toBeUndefined();
    expect(receiptSchema.safeParse(ui?.receipt).success).toBe(true);
    expect(ui?.receipt?.artifacts).toEqual(["index.html"]);
    expect(evaluation.status).toBe("failed");
    expect(evaluation.steps[0]?.screenshot).toBe("evidence/tfl_x/staff/01-open.png");
    expect(evaluation.summary).toContain("1 Blueprint check(s) have no automated test");
  });

  it("fails every runtime check when the bots produced nothing", () => {
    const evaluation = evaluateFlight({
      flightId: "tfl_x",
      appReady: true,
      results: null,
      botsFailure: "The bots did not finish.",
      appDir: join(root, "none"),
      evidenceRel: "evidence/tfl_x",
    });
    expect(evaluation.status).toBe("failed");
    expect(evaluation.categories.routes.detail).toBe("The bots did not finish.");
  });
});

describe("SandboxManager", () => {
  it("says plainly when Docker is not running", async () => {
    writeApp();
    const docker = fakeDocker((args) => (args[0] === "version" ? { code: 1, stderr: "error during connect" } : {}));
    const manager = new SandboxManager({ paths, docker, ports: await portsFor() });
    const error = await manager.start(BUILD).catch((caught: unknown) => caught);
    expect(error).toBeInstanceOf(SandboxError);
    expect((error as SandboxError).code).toBe("docker_unavailable");
    expect((error as SandboxError).message).toContain("Start Docker Desktop");
    const status = await manager.status();
    expect(status.docker.available).toBe(false);
  });

  it("says plainly when Docker is not installed", async () => {
    writeApp();
    const docker = fakeDocker(() => ({ code: -1, spawnError: "ENOENT" }));
    const manager = new SandboxManager({ paths, docker, ports: await portsFor() });
    const error = (await manager.start(BUILD).catch((caught: unknown) => caught)) as SandboxError;
    expect(error.code).toBe("docker_unavailable");
    expect(error.message).toContain("not installed");
  });

  it("refuses a build with no app files", async () => {
    const manager = new SandboxManager({ paths, docker: healthyDocker(), ports: await portsFor() });
    const error = (await manager.start(BUILD).catch((caught: unknown) => caught)) as SandboxError;
    expect(error.code).toBe("conflict");
  });

  it("names the port when one is taken", async () => {
    writeApp();
    const ports = await portsFor();
    const blocker: Server = createServer();
    await new Promise<void>((resolve) => blocker.listen(ports.cdp, "127.0.0.1", resolve));
    try {
      const manager = new SandboxManager({ paths, docker: healthyDocker(), ports });
      const error = (await manager.start(BUILD).catch((caught: unknown) => caught)) as SandboxError;
      expect(error.code).toBe("port_taken");
      expect(error.port).toBe(ports.cdp);
      expect(error.message).toContain(String(ports.cdp));
    } finally {
      await new Promise<void>((resolve) => blocker.close(() => resolve()));
    }
  });

  it("starts, flies both personas, records receipts and stops", async () => {
    writeApp();
    writeFileSync(paths.build(BUILD).record, JSON.stringify(buildRecord()));
    const docker = healthyDocker((dir) => writeBotResults(dir));
    const manager = new SandboxManager({ paths, docker, ports: await portsFor() });
    const events: SandboxEventDraft[] = [];
    manager.on((event) => events.push(event));

    const starting = await manager.start(BUILD);
    expect(starting.status).toBe("starting");
    await manager.waitFor(starting.id);
    const run = manager.getRun(starting.id);
    expect(run.status).toBe("running");
    expect(run.appUrl).toBe(`http://127.0.0.1:${manager.ports.app}/`);
    const composeArgs = docker.calls.filter((call) => call.args[0] === "compose" && call.args[1] === "-p").map((call) => call.args.slice(5).join(" "));
    expect(composeArgs).toEqual(
      expect.arrayContaining(["--profile bots build", "run --rm --no-deps -T seed", expect.stringContaining("up -d --no-deps --wait")]),
    );
    expect(docker.calls.find((call) => call.args.includes("seed"))?.env.FORGE_SANDBOX_SOURCE).toContain(BUILD);
    expect(await manager.start(BUILD)).toEqual(run);

    const flight = await manager.startTestFlight({ buildId: BUILD });
    expect(flight.status).toBe("running");
    expect(flight.personas.map((persona) => persona.role).sort()).toEqual(["customer", "staff"]);
    await manager.waitFor(flight.id);
    const done = manager.getTestFlight(flight.id);
    expect(done.status).toBe("passed");
    expect(done.steps.length).toBe(6);
    expect(done.layers).toEqual([
      { layerId: "lyr_ui", status: "passed" },
      { layerId: "lyr_api", status: "passed" },
      { layerId: "lyr_later", status: "not-covered" },
    ]);
    const receipts = loadReceipts(BUILD, paths);
    expect(receipts.broken).toEqual([]);
    expect(receipts.loaded.map((receipt) => receipt.layerId).sort()).toEqual(["lyr_api", "lyr_ui"]);
    expect(receipts.loaded.every((receipt) => receipt.agentId === "icarus")).toBe(true);
    const evidence = readdirSync(join(paths.build(BUILD).evidence, flight.id));
    expect(evidence).toEqual(expect.arrayContaining(["plan.json", "results.json", "flight.json", "bots.log"]));
    const plan = JSON.parse(readFileSync(join(paths.build(BUILD).evidence, flight.id, "plan.json"), "utf8"));
    expect(plan.browserUrl).toBe(`http://127.0.0.1:${manager.ports.app}/`);

    const updated = applyTestFlightToBuild(buildRecord(), done, receipts.loaded);
    expect(updated.layers.find((layer) => layer.id === "lyr_ui")?.testFlight).toBe("passed");
    expect(updated.layers.find((layer) => layer.id === "lyr_later")?.testFlight).toBe("not-run");

    const stopped = await manager.stop(run.id);
    expect(stopped.status).toBe("stopped");
    expect(docker.calls.at(-1)?.args.join(" ")).toContain("down -v --remove-orphans");
    const types = new Set(events.map((event) => event.type));
    expect(types).toEqual(new Set(["sandbox.updated", "testflight.updated", "receipt.recorded", "log"]));

    // A fresh manager reads the same records from disk.
    const again = new SandboxManager({ paths, docker: healthyDocker(), ports: await portsFor() });
    expect(again.getTestFlight(flight.id).status).toBe("passed");
    expect(again.getRun(run.id).status).toBe("stopped");
  });

  it("marks a run failed with the seed log when the app cannot be prepared", async () => {
    writeApp();
    const docker = fakeDocker((args) => {
      if (args[0] === "version") return { stdout: "29\n" };
      if (args[1] === "version") return { stdout: "5\n" };
      if (args.includes("seed")) return { code: 1, stderr: "npm error 404 Not Found - left-padd" };
      return {};
    });
    const manager = new SandboxManager({ paths, docker, ports: await portsFor() });
    const run = await manager.start(BUILD);
    await manager.waitFor(run.id);
    const failed = manager.getRun(run.id);
    expect(failed.status).toBe("failed");
    expect(failed.failure?.code).toBe("compose_failed");
    expect(failed.failure?.message).toContain("left-padd");
  });

  it("reports an unhealthy app", async () => {
    writeApp();
    const docker = fakeDocker((args) => {
      if (args[0] === "version") return { stdout: "29\n" };
      if (args[1] === "version") return { stdout: "5\n" };
      if (args.includes("up")) return { code: 1, stderr: "container 4ge-sandbox-app-1 is unhealthy" };
      if (args.includes("ps")) return { stdout: '{"Service":"app","State":"running","Health":"unhealthy"}\n' };
      if (args.includes("logs")) return { stdout: "app-1  | Error: Cannot find module 'express'\n" };
      return {};
    });
    const manager = new SandboxManager({ paths, docker, ports: await portsFor() });
    const run = await manager.start(BUILD);
    await manager.waitFor(run.id);
    const failed = manager.getRun(run.id);
    expect(failed.failure?.code).toBe("app_unhealthy");
    expect(failed.failure?.message).toContain("Cannot find module");
  });

  it("refuses a test flight when the build is not in the sandbox", async () => {
    const manager = new SandboxManager({ paths, docker: healthyDocker(), ports: await portsFor() });
    const error = (await manager.startTestFlight({ buildId: BUILD }).catch((caught: unknown) => caught)) as SandboxError;
    expect(error.code).toBe("conflict");
    expect(error.message).toContain("Start the sandbox first");
  });

  it("fails a flight with a plain reason when the bots write nothing", async () => {
    writeApp();
    const manager = new SandboxManager({ paths, docker: healthyDocker(), ports: await portsFor() });
    const run = await manager.start(BUILD);
    await manager.waitFor(run.id);
    const flight = await manager.startTestFlight({ buildId: BUILD });
    await manager.waitFor(flight.id);
    const done = manager.getTestFlight(flight.id);
    expect(done.status).toBe("failed");
    expect(done.summary).toContain("The bots did not finish");
  });
});
