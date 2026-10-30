import { existsSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { expect, test, type Page } from "@playwright/test";
import { DATA_ROOT, startForge, type ForgeServer } from "../support/forge.js";
import { checkScreen } from "../support/visual.js";

/**
 * Visual checks of every studio screen with a real build in it: the data
 * root the flow suite left behind (flow.spec.ts runs first), opened by a
 * fresh server. Each screen is screenshotted and measured for sideways
 * scroll, clipped text, overlapping controls, Phosphor tokens and dashes
 * (support/visual.ts). Screenshots go to e2e/.data/round-1/shots.
 */

const flowRoot = join(DATA_ROOT, "flow");
const hasBuild = existsSync(join(flowRoot, "builds")) && readdirSync(join(flowRoot, "builds")).some((name) => name.startsWith("bld_"));
test.skip(!hasBuild, "Run flow.spec.ts first: the visual pass reads the build it leaves in e2e/.data/round-1/flow.");

let server: ForgeServer;
let buildId = "";
let sessionId = "";

test.beforeAll(async () => {
  server = await startForge({ name: "flow", port: 17404, keepData: true, env: { NEBIUS_API_KEY: undefined } });
  const { builds } = await server.api.call("builds.list", {});
  buildId = builds.find((build) => build.status === "passed")?.id ?? builds[0]?.id ?? "";
  sessionId = (await server.api.call("imagistation.sessions.list", {})).sessions[0]?.id ?? "";
});
test.afterAll(async () => server?.stop());

async function screen(page: Page, name: string, path: string): Promise<void> {
  const started = Date.now();
  await page.goto(`${server.url}${path}`);
  await page.waitForTimeout(1000);
  await checkScreen(page, name);
  test.info().annotations.push({ type: "screen", description: `${name} ${path} ${Date.now() - started} ms` });
}

test("Imagistation, the IDE and the Tweak page", async ({ page }) => {
  expect(buildId).not.toBe("");
  await screen(page, "visual-imagistation-start", "/imagistation");
  await screen(page, "visual-imagistation-session", `/imagistation/${sessionId}`);
  await screen(page, "visual-ide-pick", "/ide");
  await screen(page, "visual-ide-build", `/ide/${buildId}`);
  await screen(page, "visual-tweak", `/tweak/${buildId}`);
});

test("the IDE inspector with its tweak controls open", async ({ page }) => {
  await page.goto(`${server.url}/ide/${buildId}`);
  // An HTML artboard is the tunable kind; fit the board so every frame is on screen, then pick one.
  await page.getByRole("toolbar", { name: "Canvas" }).getByRole("button", { name: /Fit all/ }).click();
  const artboard = page.locator('[data-testid^="frame-preview:"][data-testid$=".html"]').first();
  await expect(artboard).toBeVisible({ timeout: 20_000 });
  await artboard.locator(".frame__body").click({ position: { x: 4, y: 4 } });
  await expect(page.getByTestId("inspector")).toBeVisible();
  await page.getByTestId("inspector-tweak").click();
  await checkScreen(page, "visual-ide-inspector-tweak");
});

test("the Sandbox, Library and Mods pages", async ({ page }) => {
  await screen(page, "visual-sandbox-pick", "/sandbox");
  await screen(page, "visual-sandbox-build", `/sandbox/${buildId}`);
  await screen(page, "visual-library", "/library");
  await screen(page, "visual-mods", "/mods");
  await screen(page, "visual-mods-create", "/mods?create=1");
});

test("a narrow window still fits", async ({ page }) => {
  await page.setViewportSize({ width: 1024, height: 700 });
  await screen(page, "visual-narrow-library", "/library");
  await screen(page, "visual-narrow-ide", `/ide/${buildId}`);
  await screen(page, "visual-narrow-imagistation", `/imagistation/${sessionId}`);
});
