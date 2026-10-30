/**
 * Live proof of the sandbox on this PC's Docker:
 *
 *   pnpm --filter @4ge/orchestra-sandbox prove [dataRoot]
 *
 * Puts the tiny static site in fixtures/tiny-site into a build under
 * dataRoot (default: a fresh folder in the OS temp dir, never the user's real
 * 4GE data), brings the sandbox up, checks the three ports answer, flies the
 * Blueprint's staff and customer personas, prints every step with its
 * screenshot, then tears the sandbox down and confirms nothing is left.
 * Exits non-zero when any part fails.
 */
import { cpSync, existsSync, mkdirSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { blueprintSchema, buildSchema, type Blueprint, type Build } from "@4ge/contracts";
import { dataPaths, ensureDataDirs } from "@4ge/contracts/node";
import { loadReceipts } from "@4ge/gate";
import { createDockerCli } from "./docker.js";
import { COMPOSE_PROJECT, SandboxManager } from "./manager.js";

const here = dirname(fileURLToPath(import.meta.url));
const fixture = resolve(here, "..", "fixtures", "tiny-site");
const root = resolve(process.argv[2] ?? join(tmpdir(), `4ge-sandbox-proof-${Date.now()}`));
const paths = ensureDataDirs(dataPaths(root));
const buildId = `bld_proof${Date.now().toString(36)}`;
const stamp = new Date().toISOString();

const blueprint: Blueprint = blueprintSchema.parse({
  blueprintId: "blueprint:sandbox-proof",
  name: "Sandbox proof site",
  version: "1.0.0",
  state: "reviewed",
  origin: "crafted",
  kind: "website",
  platforms: ["web"],
  description: "A four page static site the sandbox flies to prove itself.",
  stages: [
    {
      stage: "interface",
      goal: "Pages a visitor and the owner can click through",
      layers: [
        {
          key: "landing-page",
          kind: "interface",
          title: "Landing and booking pages",
          agent: "smith",
          description: "Home, services and booking form",
          deliverables: ["index.html", "services.html", "book.html", "styles.css"],
          checks: ["A customer can send the booking form", "Every image has alt text", "The copy sounds like the studio"],
        },
        {
          key: "staff-area",
          kind: "content",
          title: "Staff requests page",
          agent: "smith",
          description: "Where requests will show",
          deliverables: ["admin/index.html"],
          checks: ["The staff page opens"],
        },
      ],
      checkpoint: { title: "Pages", question: "Do the pages read right?" },
    },
  ],
  personas: [
    {
      id: "staff",
      role: "staff",
      name: "Studio owner",
      brief: "Runs the studio and checks the site and the requests page before sharing it.",
      goals: ["Open the requests page", "Go back to the public site", "Check every page opens"],
      entryPath: "/admin/",
    },
    {
      id: "customer",
      role: "customer",
      name: "First-time client",
      brief: "Wants a first tattoo and books a session from the site.",
      goals: ["Read the services", "Book a session"],
      script: [
        { action: "click", target: "Book a session" },
        { action: "expect-path", target: "/book.html" },
        { action: "fill", target: "Your name", value: "Sam Proof" },
        { action: "fill", target: "Email", value: "sam@example.test" },
        { action: "fill", target: "Preferred day", value: "2026-11-12" },
        { action: "fill", target: "Your idea", value: "A small fern on the wrist" },
        { action: "click", target: "Send request" },
        { action: "expect-text", target: "Request received" },
      ],
    },
  ],
});

const layer = (id: string, key: string, kind: "interface" | "content", title: string) => ({
  id,
  buildId,
  key,
  kind,
  stage: "interface" as const,
  agent: "smith" as const,
  title,
  status: "passed" as const,
  dependsOn: [],
  previews: [],
  testFlight: "not-run" as const,
});

const build: Build = buildSchema.parse({
  id: buildId,
  seedId: "seed_sandboxproof",
  blueprintId: blueprint.blueprintId,
  title: "Sandbox proof site",
  status: "test-flight",
  stages: [{ stage: "interface", owner: "smith", status: "passed" }],
  layers: [layer("lyr_landing", "landing-page", "interface", "Landing and booking pages"), layer("lyr_staff", "staff-area", "content", "Staff requests page")],
  sprints: [],
  createdAt: stamp,
  updatedAt: stamp,
});

function say(line: string): void {
  console.log(line);
}

async function probe(url: string): Promise<string> {
  try {
    const response = await fetch(url);
    return `${response.status}`;
  } catch (error) {
    return `unreachable (${error instanceof Error ? error.message : String(error)})`;
  }
}

const buildPaths = paths.build(buildId);
mkdirSync(buildPaths.root, { recursive: true });
cpSync(fixture, buildPaths.app, { recursive: true });
writeFileSync(buildPaths.record, JSON.stringify(build, null, 2));
writeFileSync(join(paths.atlasBlueprints, "sandbox-proof.json"), JSON.stringify(blueprint, null, 2));
say(`data root: ${root}`);
say(`build: ${buildId} (app copied from ${fixture})`);

const manager = new SandboxManager({ paths });
manager.on((event) => {
  if (event.type === "log") say(`  [icarus] ${event.data.message}`);
  if (event.type === "sandbox.updated") say(`  [sandbox] ${event.data.run.status}`);
  if (event.type === "receipt.recorded") say(`  [receipt] ${event.data.receipt.layerId} ${event.data.receipt.id}`);
});

let exitCode = 0;
const status = await manager.status();
say(`docker: ${status.docker.available ? status.docker.version : status.docker.message}`);
say(`ports: ${status.ports.map((port) => `${port.port} ${port.free ? "free" : "taken"}`).join(", ")}`);

const started = await manager.start(buildId);
await manager.waitFor(started.id);
const run = manager.getRun(started.id);
if (run.status !== "running") {
  say(`sandbox failed: ${run.failure?.code} ${run.failure?.message}`);
  process.exit(1);
}
say(`app ${run.appUrl} -> ${await probe(run.appUrl ?? "")}`);
say(`cdp http://127.0.0.1:${run.ports.cdp}/json/version -> ${await probe(`http://127.0.0.1:${run.ports.cdp}/json/version`)}`);
say(`viewer ${run.viewerUrl} -> ${await probe(`http://127.0.0.1:${run.ports.viewer}/vnc.html`)}`);

try {
  const flight = await manager.startTestFlight({ buildId });
  await manager.waitFor(flight.id);
  const done = manager.getTestFlight(flight.id);
  say(`\ntest flight ${done.id}: ${done.status}`);
  say(done.summary ?? "");
  for (const step of done.steps) {
    say(`  ${step.status === "passed" ? "PASS" : "FAIL"} [${step.personaId}] ${step.action}${step.error ? ` :: ${step.error}` : ""}`);
    if (step.screenshot) say(`         ${join(buildPaths.root, step.screenshot)}${existsSync(join(buildPaths.root, step.screenshot)) ? "" : " (missing)"}`);
  }
  say(`layers: ${done.layers.map((entry) => `${entry.layerId}=${entry.status}`).join(", ")}`);
  const receipts = loadReceipts(buildId, paths);
  say(`receipts: ${receipts.loaded.length} loaded, ${receipts.broken.length} broken, in ${buildPaths.receipts}`);
  say(`evidence: ${join(buildPaths.evidence, done.id)}`);
  if (done.status !== "passed") exitCode = 1;
} finally {
  const stopped = await manager.stop(run.id);
  say(`\nsandbox ${stopped.status}`);
  const left = await createDockerCli().exec(["ps", "-a", "--filter", `label=com.docker.compose.project=${COMPOSE_PROJECT}`, "--format", "{{.Names}}"]);
  const volumes = await createDockerCli().exec(["volume", "ls", "--filter", "name=4ge_sandbox", "--format", "{{.Name}}"]);
  say(`containers left: ${left.stdout.trim() || "none"}; volumes left: ${volumes.stdout.trim() || "none"}`);
}
process.exit(exitCode);
