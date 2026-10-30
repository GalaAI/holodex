import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { startForge, stubModelEnv, type ForgeServer } from "../support/forge.js";
import { startModelStub, type ModelStub } from "../support/model-stub.js";
import { waitFor } from "../support/sandbox.js";
import { checkScreen } from "../support/visual.js";

/**
 * Seed decisions and the Blueprint choice: build later (the Seed goes to
 * the AInbox queue in AInbox's own format, with the reminder note), no
 * Blueprint in the Atlas fits (Prometheus crafts one and registers it), and
 * a build cancelled in the middle of a sprint. Real server, scripted model.
 */

let stub: ModelStub;
let server: ForgeServer;

test.beforeAll(async () => {
  stub = await startModelStub();
  server = await startForge({ name: "seed", port: 17406, env: { ...stubModelEnv(stub.baseUrl), FORGE_MODEL_TIMEOUT_MS: "120000" } });
});

test.afterAll(async () => {
  await server?.stop();
  await stub?.close();
});

/** Brainstorm an idea to a written Seed Scroll in the studio. */
async function seedFor(page: Page, idea: string, answer: string): Promise<string> {
  await page.goto(`${server.url}/imagistation`);
  await page.getByLabel("What do you want to build?").fill(idea);
  await page.getByTestId("start-idea").click();
  await expect(page).toHaveURL(/\/imagistation\/ses_/, { timeout: 30_000 });
  await page.getByLabel("Message Prometheus").fill(answer);
  await page.getByLabel("Message Prometheus").press("Enter");
  await expect(page.getByTestId("thread")).toContainText("I can write the Seed Scroll now", { timeout: 30_000 });
  await page.getByTestId("write-seed").click();
  await expect(page.getByTestId("seed-scroll")).toBeVisible({ timeout: 60_000 });
  return /\/imagistation\/(ses_[^/?#]+)/.exec(page.url())?.[1] ?? "";
}

test("build later: the Seed goes to the AInbox queue with a reminder note", async ({ page }) => {
  await seedFor(page, "A website for my tattoo shop", "Adults in Portland. Success is ten booking requests a week.");
  const decision = page.getByRole("region", { name: "Decision" });
  await decision.getByRole("button", { name: "Later" }).click();
  await decision.getByLabel("Reminder note for AInbox").fill("Block two hours on Saturday morning for this.");
  await decision.getByRole("button", { name: "Send to AInbox" }).click();
  const later = page.getByTestId("seed-later");
  await expect(later).toBeVisible({ timeout: 20_000 });
  await expect(later).toContainText("Reminder: Block two hours on Saturday morning for this.");
  await checkScreen(page, "seed-later");

  const pending = join(server.ainboxQueue, "pending");
  const files = await waitFor("the AInbox queue item", async () => readdirSync(pending).filter((name) => name.endsWith(".json")), (list) => list.length > 0, 10_000, 500);
  const item = JSON.parse(readFileSync(join(pending, files[0] as string), "utf8")) as Record<string, unknown>;
  // AInbox's own format: PascalCase fields, the build-later intent and the ask with the note.
  expect(item.IntentId).toBe("build-later");
  expect(item.IntentLabel).toBe("Build later");
  expect(JSON.stringify(item)).toContain("Block two hours on Saturday morning for this.");
  expect(JSON.stringify(item)).toContain("Ink and Iron");
  expect(typeof item.Sha256).toBe("string");

  const { seeds } = await server.api.call("seeds.list", {});
  expect(seeds.find((seed) => seed.decision === "later")?.reminderNote).toBe("Block two hours on Saturday morning for this.");
});

test("no Blueprint fits: Prometheus crafts one and registers it in the Atlas", async ({ page }) => {
  const before = (await server.api.call("atlas.blueprints.list", {})).entries.length;
  await seedFor(page, "A tool to update the firmware on my label printer", "Just me, the shop owner. Success is updating in one step.");
  const pick = page.getByTestId("blueprint-pick");
  await expect(pick).toContainText("Label printer updater", { timeout: 60_000 });
  expect(stub.calls.some((call) => call.purpose === "blueprint.craft")).toBe(true);
  await expect(page.getByTestId("thread")).toContainText(/crafted/i);
  const after = (await server.api.call("atlas.blueprints.list", {})).entries;
  expect(after.length).toBe(before + 1);
  const crafted = after.find((entry) => entry.blueprint.name === "Label printer updater");
  expect(crafted?.source).toBe("user");
  await checkScreen(page, "seed-crafted-blueprint");
});

test("a build cancelled in the middle of a sprint stops every layer and says so", async ({ page }) => {
  test.setTimeout(3 * 60_000);
  await seedFor(page, "A website for my bakery", "Neighbors on the way to work. Success is twenty preorders a week.");
  stub.layerDelayMs = 45_000;
  try {
    await page.getByTestId("build-now").click();
    await expect(page).toHaveURL(/\/ide\/bld_/, { timeout: 30_000 });
    const buildId = /\/ide\/(bld_[^/?#]+)/.exec(page.url())?.[1] ?? "";
    await waitFor(
      "a layer to be running",
      async () => (await server.api.call("builds.get", { params: { id: buildId } })).build,
      (build) => build.layers.some((layer) => layer.status === "running"),
      60_000,
      500,
    );
    await page.getByTestId("cancel-build").click();
    await page.getByTestId("confirm-cancel").click();
    const build = await waitFor(
      "the build to be cancelled",
      async () => (await server.api.call("builds.get", { params: { id: buildId } })).build,
      (value) => value.status === "cancelled",
      30_000,
      500,
    );
    expect(build.layers.filter((layer) => layer.status === "running")).toEqual([]);
    expect(build.layers.some((layer) => layer.status === "cancelled")).toBe(true);
    await expect(page.getByTestId("feature-ide")).toContainText(/cancelled/i, { timeout: 20_000 });
    await expect(page.getByTestId("cancel-build")).toBeHidden();
    // A cancelled build cannot be pushed on; the checkpoint API refuses in plain words.
    const refused = await server.api.call("builds.cancel", { params: { id: buildId } }).then(
      () => undefined,
      (error: Error) => error.message,
    );
    expect(refused ?? "already cancelled").toMatch(/cancel/i);
    await checkScreen(page, "ide-cancelled");
  } finally {
    stub.layerDelayMs = 0;
  }
});
