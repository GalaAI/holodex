import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { expect, test, type Page } from "@playwright/test";
import type { Build } from "@4ge/contracts";
import { chromium } from "playwright-core";
import { shotPath, startForge, stubModelEnv, type ForgeServer } from "../support/forge.js";
import { startModelStub, type ModelStub } from "../support/model-stub.js";
import { sandboxDown, waitFor } from "../support/sandbox.js";
import { expectClean, shootScreen, type VisualReport } from "../support/visual.js";

/**
 * The SPEC flow end to end through the studio, against a real 4GE server
 * whose model is the scripted endpoint (support/model-stub.ts) and whose
 * sandbox is the real 4ge-sandbox on local Docker: Imagistation, the Seed
 * Scroll with a Blueprint from the Atlas, Build now, four sprints in nested
 * sessions with a failed layer retried at its checkpoint, a thread Smith
 * acts on, the deploy sprint with Icarus launching the app in the sandbox
 * and the bots flying staff and customer, the built app seen in the sandbox
 * Chromium over CDP on 17411, the build in the Library under My projects, a
 * tweak with undo, a persona failure, and a visual pass over every screen.
 *
 * Steps depend on each other, so the file runs in order and stops at the
 * first broken step.
 */
test.describe.configure({ mode: "serial" });

let stub: ModelStub;
let server: ForgeServer;
let sessionId = "";
let buildId = "";
/** Screens measured along the way; the last test asserts them, so a visual problem never hides a later flow step. */
const measured: Array<VisualReport & { name: string }> = [];
const shoot = async (page: Page, name: string) => measured.push(await shootScreen(page, name));

const getBuild = async (): Promise<Build> => (await server.api.call("builds.get", { params: { id: buildId } })).build;
const layer = (build: Build, key: string) => build.layers.find((item) => item.key === key);
const appFile = (path: string) => join(server.dataRoot, "builds", buildId, "app", path);

async function openCheckpoint(stage: string, timeoutMs: number): Promise<Build> {
  return waitFor(
    `the ${stage} checkpoint`,
    getBuild,
    (build) =>
      build.status === "failed" ||
      build.status === "cancelled" ||
      (build.status === "awaiting-checkpoint" && build.stages.some((state) => state.stage === stage && (state.status === "passed" || state.status === "failed"))),
    timeoutMs,
  );
}

/** Jump the canvas to a stage and select its stage frame, as a person would. */
async function selectStageFrame(page: Page, stage: "architecture" | "orchestration" | "interface" | "deploy"): Promise<void> {
  const label = { architecture: "01 Architecture", orchestration: "02 Orchestration", interface: "03 Interface", deploy: "04 Deploy" }[stage];
  await page.getByRole("toolbar", { name: "Canvas" }).getByRole("button", { name: label }).click();
  const frame = page.getByTestId(`frame-stage:${stage}`);
  await expect(frame).toBeVisible({ timeout: 20_000 });
  await frame.locator(".frame__body").click({ position: { x: 24, y: 24 } });
  await expect(page.getByTestId("inspector")).toBeVisible();
}

async function approveInUi(page: Page, stage: string): Promise<void> {
  await page.goto(`${server.url}/ide/${buildId}`);
  const card = page.getByTestId("checkpoint");
  await expect(card).toBeVisible({ timeout: 20_000 });
  await expect(card).toContainText(new RegExp(stage, "i"));
  await page.getByTestId("checkpoint-approve").click();
  await expect(card).toBeHidden({ timeout: 20_000 });
}

test.beforeAll(async () => {
  await sandboxDown();
  stub = await startModelStub();
  server = await startForge({ name: "flow", port: 17404, env: { ...stubModelEnv(stub.baseUrl), FORGE_MODEL_TIMEOUT_MS: "60000" } });
});

test.afterAll(async () => {
  if (server && buildId) {
    const runs = await server.api.call("sandbox.runs.list", { query: { buildId } }).catch(() => ({ runs: [] }));
    for (const run of runs.runs) {
      if (run.status === "running" || run.status === "starting") await server.api.call("sandbox.stop", { body: { runId: run.id } }).catch(() => undefined);
    }
  }
  await server?.stop();
  await stub?.close();
  await sandboxDown();
});

test("Imagistation: Prometheus asks, then writes a Seed Scroll with a Blueprint from the Atlas", async ({ page }) => {
  await page.goto(`${server.url}/imagistation`);
  await page.getByLabel("What do you want to build?").fill("A website for my tattoo shop");
  await page.getByTestId("start-idea").click();
  await expect(page).toHaveURL(/\/imagistation\/ses_/, { timeout: 30_000 });
  sessionId = /\/imagistation\/(ses_[^/?#]+)/.exec(page.url())?.[1] ?? "";
  const thread = page.getByTestId("thread");
  await expect(thread).toContainText("Who are the people who will use Ink and Iron");
  await page.getByLabel("Message Prometheus").fill("Adults in Portland who want custom work. Success is ten booking requests a week.");
  await page.getByLabel("Message Prometheus").press("Enter");
  await expect(thread).toContainText("I can write the Seed Scroll now");
  await page.getByTestId("write-seed").click();
  const scroll = page.getByTestId("seed-scroll");
  await expect(scroll).toBeVisible({ timeout: 30_000 });
  await expect(scroll).toContainText("Ink and Iron");
  await expect(page.getByTestId("blueprint-pick")).toContainText(/website/i);
  await expect(page.getByTestId("build-now")).toBeEnabled();
  await shoot(page, "flow-01-imagistation-seed");
});

test("Build now: the IDE opens and the orchestra builds in nested sessions; a failing layer stops at its checkpoint", async ({ page }) => {
  test.setTimeout(5 * 60_000);
  // Every try of the content model fails one of its checks: the layer cannot pass on its own.
  stub.layerFailures.set("Content model", 3);
  await page.goto(`${server.url}/imagistation/${sessionId}`);
  await page.getByTestId("build-now").click();
  await expect(page).toHaveURL(/\/ide\/bld_/, { timeout: 30_000 });
  buildId = /\/ide\/(bld_[^/?#]+)/.exec(page.url())?.[1] ?? "";
  await expect(page.getByTestId("stage-strip")).toBeVisible();
  await expect(page.getByTestId("agent-tree")).toBeVisible();

  const build = await openCheckpoint("architecture", 4 * 60_000);
  expect(build.status).toBe("awaiting-checkpoint");
  expect(layer(build, "content-model")?.status).toBe("failed");
  expect(layer(build, "business-brief")?.status).toBe("passed");
  const tree = await server.api.call("sessions.tree", { params: { id: build.rootSessionId as string } });
  expect(tree.tree.children.length).toBeGreaterThanOrEqual(2);
  expect(stub.calls.filter((call) => call.layer === "Content model").length).toBe(3);

  await page.reload();
  const card = page.getByTestId("checkpoint");
  await expect(card).toBeVisible({ timeout: 20_000 });
  await expect(card).toContainText("Content model");
  await shoot(page, "flow-02-ide-failed-layer");
});

test("a failed layer is retried with the person's note and passes", async ({ page }) => {
  test.setTimeout(5 * 60_000);
  const before = stub.calls.filter((call) => call.layer === "Content model").length;
  await page.goto(`${server.url}/ide/${buildId}`);
  const card = page.getByTestId("checkpoint");
  await expect(card).toBeVisible({ timeout: 20_000 });
  await card.getByRole("button", { name: "Revise" }).click();
  await card.getByLabel("What should change?").fill("The content model must pass the typecheck. Keep the starter's site.json shape.");
  await card.getByRole("button", { name: "Send revision" }).click();
  const build = await waitFor("the content model to pass", getBuild, (value) => layer(value, "content-model")?.status === "passed" && value.status === "awaiting-checkpoint", 4 * 60_000);
  expect(stub.calls.filter((call) => call.layer === "Content model").length).toBeGreaterThan(before);
  expect(build.stages.find((state) => state.stage === "architecture")?.status).toBe("passed");
  await approveInUi(page, "architecture|brief");
  await openCheckpoint("orchestration", 4 * 60_000);
  await approveInUi(page, "orchestration|bookings");
});

test("a thread pinned on a frame is sent to Smith, and Smith rebuilds the layer it names", async ({ page }) => {
  test.setTimeout(6 * 60_000);
  const build = await openCheckpoint("interface", 4 * 60_000);
  expect(build.status).toBe("awaiting-checkpoint");
  await page.goto(`${server.url}/ide/${buildId}`);
  await selectStageFrame(page, "interface");
  const inspector = page.getByTestId("inspector");
  await inspector.getByLabel("Note for Smith").fill("Make the gallery the first thing people see on the landing page");
  await inspector.getByRole("button", { name: "Add thread" }).click();
  const card = inspector.locator('[data-testid^="thread-ann_"]').first();
  await expect(card).toBeVisible();
  await expect(card).toContainText("Make the gallery the first thing");
  await shoot(page, "flow-03-ide-inspector-thread");

  const before = stub.calls.filter((call) => call.layer === "Landing page").length;
  stub.smithAction = { layer: "landing-page", note: "Make the gallery the first thing people see" };
  await card.getByRole("button", { name: "Send to Smith" }).click();
  await expect(card).toContainText("I will rebuild that layer with your note", { timeout: 60_000 });
  await expect(card).toContainText(/In progress/);
  await waitFor("Smith's rebuild of the landing page", async () => stub.calls.filter((call) => call.layer === "Landing page").length, (count) => count > before, 2 * 60_000);
  const after = await waitFor("the rebuilt landing page", getBuild, (value) => layer(value, "landing-page")?.status === "passed" && value.status === "awaiting-checkpoint", 2 * 60_000);
  expect(layer(after, "landing-page")?.status).toBe("passed");
  const smith = await server.api.call("sessions.get", { params: { id: after.rootSessionId as string } });
  expect(smith.messages.map((message) => message.content).join("\n")).toMatch(/Landing page is rebuilt and passed its checks/);

  // The findings list collects the thread.
  await page.getByRole("button", { name: "Close the inspector (Esc)" }).click();
  await page.getByRole("tab", { name: /Findings/ }).click();
  await expect(page.getByTestId("findings")).toContainText("Make the gallery the first thing");
  await approveInUi(page, "interface|page");
});

test("deploy: Icarus launches the app in the 4ge-sandbox, the bots fly staff and customer, and the build passes", async ({ page }) => {
  test.setTimeout(30 * 60_000);
  const build = await openCheckpoint("deploy", 28 * 60_000);
  const launch = layer(build, "sandbox-launch");
  expect(launch?.status, launch?.error).toBe("passed");
  for (const key of ["test-flight-customer", "test-flight-staff"]) {
    const flight = layer(build, key);
    expect(flight?.status, flight?.error).toBe("passed");
    expect(flight?.testFlight).toBe("passed");
  }
  const receipts = await server.api.call("builds.receipts", { params: { id: buildId } });
  expect(receipts.gate.every((decision) => decision.admitted)).toBe(true);
  await approveInUi(page, "deploy|launch");
  const done = await waitFor("the build to pass", getBuild, (value) => value.status === "passed" || value.status === "failed", 60_000);
  expect(done.status).toBe("passed");
});

test("the built app is live in the sandbox Chromium over CDP on 17411", async ({ page }) => {
  test.setTimeout(10 * 60_000);
  await page.goto(`${server.url}/sandbox/${buildId}`);
  await expect(page.getByTestId("docker-state")).toContainText(/Docker/);
  const runs = await server.api.call("sandbox.runs.list", { query: { buildId } });
  if (!runs.runs.some((run) => run.status === "running")) {
    await page.getByTestId("sandbox-start").click();
  }
  await waitFor("a running sandbox", async () => (await server.api.call("sandbox.runs.list", { query: { buildId } })).runs, (list) => list.some((run) => run.status === "running"), 8 * 60_000, 3000);
  await page.reload();
  await expect(page.getByTestId("sandbox-run")).toContainText("browser 17411");
  await shoot(page, "flow-04-sandbox-running");

  const browser = await chromium.connectOverCDP("http://127.0.0.1:17411");
  try {
    const pages = browser.contexts().flatMap((context) => context.pages());
    const app = pages.find((candidate) => candidate.url().startsWith("http://127.0.0.1:17410"));
    expect(app, `pages in the sandbox browser: ${pages.map((candidate) => candidate.url()).join(", ")}`).toBeDefined();
    if (!app) return;
    await app.reload({ waitUntil: "load" });
    await expect(app.locator("#app")).not.toBeEmpty({ timeout: 30_000 });
    await expect(app.locator("form").first()).toBeVisible();
    await app.screenshot({ path: shotPath("flow-05-sandbox-chromium-app") });
  } finally {
    await browser.close();
  }
});

test("the build joins the Library under My projects and opens in the IDE", async ({ page }) => {
  const library = await server.api.call("library.list", {});
  const project = library.groups.find((group) => group.origin === "project")?.items.find((item) => item.id === `project:${buildId}`);
  expect(project?.title).toBeTruthy();
  await page.goto(`${server.url}/library`);
  const mine = page.locator('section[data-origin="project"]');
  await expect(mine.getByRole("heading", { name: /My projects/ })).toBeVisible();
  await mine.getByRole("button", { name: new RegExp(project?.title ?? "Ink and Iron") }).first().click();
  await expect(page).toHaveURL(new RegExp(`/ide/${buildId}$`));
});

test("a tweak lands in the build's files and Undo takes it back", async ({ page }) => {
  await page.goto(`${server.url}/tweak/${buildId}`);
  const panel = page.getByTestId("tweak-panel");
  await expect(panel).toBeVisible({ timeout: 20_000 });
  await panel.getByLabel("Element", { exact: true }).fill("h1");
  await panel.getByRole("tab", { name: "Color" }).click();
  await panel.getByLabel("Color", { exact: true }).fill("#3d7ab8");
  await panel.getByRole("button", { name: "Apply tweak" }).click();
  const item = panel.locator(".tweak-history__item").first();
  await expect(item).toContainText("Applied", { timeout: 20_000 });
  const { tweaks } = await server.api.call("design.tweaks.list", { query: { buildId } });
  const applied = tweaks.find((tweak) => tweak.status === "applied");
  expect(applied?.changedFiles.length).toBeGreaterThan(0);
  const changed = applied?.changedFiles.find((file) => file.endsWith(".css")) ?? applied?.changedFiles[0];
  expect(readFileSync(appFile(changed as string), "utf8").toLowerCase()).toContain("#3d7ab8");
  await shoot(page, "flow-06-tweak-applied");

  await item.getByRole("button", { name: "Undo" }).click();
  await expect(item).toContainText("Undone", { timeout: 20_000 });
  expect(readFileSync(appFile(changed as string), "utf8").toLowerCase()).not.toContain("#3d7ab8");
});

test("a sandbox persona that cannot do its job turns the test flight red in plain words", async ({ page }) => {
  test.setTimeout(15 * 60_000);
  const indexPath = appFile("index.html");
  const original = readFileSync(indexPath, "utf8");
  // The page no longer loads its script: no services, no booking form for the customer bot.
  writeFileSync(indexPath, original.replace(/<script type="module" src="\/src\/main\.ts"><\/script>/, ""));
  try {
    await page.goto(`${server.url}/sandbox/${buildId}`);
    const stop = page.getByRole("button", { name: "Stop the sandbox" });
    await expect(stop.or(page.getByTestId("sandbox-start"))).toBeVisible({ timeout: 20_000 });
    if (await stop.isVisible()) {
      await stop.click();
      await expect(page.getByTestId("sandbox-start")).toBeVisible({ timeout: 3 * 60_000 });
    }
    await page.getByTestId("sandbox-start").click();
    await waitFor("the sandbox with the broken page", async () => (await server.api.call("sandbox.runs.list", { query: { buildId } })).runs, (list) => list.some((run) => run.status === "running"), 8 * 60_000, 3000);
    await page.reload();
    await expect(page.getByTestId("testflight-start")).toBeEnabled({ timeout: 20_000 });
    await page.getByTestId("testflight-start").click();
    const flight = page.getByTestId("test-flight");
    await expect(flight).toBeVisible();
    await expect(flight).toContainText("Test flight red.", { timeout: 8 * 60_000 });
    await expect(flight.locator('[data-testid^="step-"]').filter({ hasText: /failed/i }).first()).toBeVisible();
    await shoot(page, "flow-07-sandbox-persona-failed");
  } finally {
    writeFileSync(indexPath, original);
  }
});

test("the screens measured along the flow are clean", () => {
  for (const report of measured) expectClean(report);
});
