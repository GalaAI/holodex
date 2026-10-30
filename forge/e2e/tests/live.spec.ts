import { expect, test } from "@playwright/test";
import { createForgeClient } from "@4ge/contracts";

/**
 * One live check against the person's real model route: a 4GE server
 * started by e2e/live/start-server.ps1 (the Nebius key goes from Windows
 * Credential Manager into that server's environment only, never here).
 * Skipped unless FORGE_E2E_LIVE_URL names that server.
 */
const live = process.env.FORGE_E2E_LIVE_URL;

test.skip(!live, "Set FORGE_E2E_LIVE_URL to a 4GE server started with e2e/live/start-server.ps1 to run the live check.");

test("live: Prometheus answers a real idea through the person's own model route", async ({ page }) => {
  test.setTimeout(5 * 60_000);
  const api = createForgeClient({ baseUrl: live as string });
  const models = await api.call("models.list", {});
  expect(models.route.provider).not.toBe("none");
  expect(models.models.length).toBeGreaterThan(0);

  await page.goto(`${live}/imagistation`);
  await expect(page.getByTestId("model-status")).toHaveText(new RegExp(`Model on ${models.route.provider}`));
  await page.getByLabel("What do you want to build?").fill("A website for my tattoo shop with a booking request form");
  await page.getByTestId("start-idea").click();
  await expect(page).toHaveURL(/\/imagistation\/ses_/, { timeout: 4 * 60_000 });
  const replies = page.locator('[data-testid="thread"] li[data-role="assistant"]');
  // The opening line plus Prometheus's first real answer.
  await expect(replies).toHaveCount(2, { timeout: 60_000 });
  const answer = (await replies.nth(1).innerText()).trim();
  expect(answer.length).toBeGreaterThan(20);
  await expect(page.locator('[data-testid="thread"] li[data-role="system"]')).toHaveCount(0);
});
