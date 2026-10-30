import { expect, test } from "@playwright/test";

/**
 * Smoke test of the studio shell: it loads from the server, lands on
 * Imagistation, shows every feature in the rail, reports the server live,
 * and every feature folder opens. Text is checked for em and en dashes,
 * which 4GE never shows.
 */
/** En dash and em dash, built from code points so this file contains neither. */
const DASHES = new RegExp(`[${String.fromCodePoint(0x2013)}${String.fromCodePoint(0x2014)}]`);

const FEATURES = [
  { id: "imagistation", label: "Imagistation", path: "/imagistation" },
  { id: "ide", label: "Build", path: "/ide" },
  { id: "tweak", label: "Tweak", path: "/tweak" },
  { id: "sandbox", label: "Sandbox", path: "/sandbox" },
  { id: "library", label: "Library", path: "/library" },
  { id: "mods", label: "Mods", path: "/mods" },
];

test("the shell loads, reports the server, and opens every feature", async ({ page }) => {
  const consoleErrors: string[] = [];
  page.on("console", (message) => {
    if (message.type() === "error") consoleErrors.push(message.text());
  });

  await page.goto("/");
  await expect(page).toHaveURL(/\/imagistation$/);
  await expect(page.getByRole("navigation", { name: "4GE" })).toBeVisible();
  await expect(page.getByTestId("server-status")).toHaveText(/Server live/);

  for (const feature of FEATURES) {
    await page.getByTestId(`rail-${feature.id}`).click();
    await expect(page).toHaveURL(new RegExp(`${feature.path}$`));
    await expect(page.getByRole("heading", { level: 1, name: feature.label })).toBeVisible();
    await expect(page.getByTestId(`rail-${feature.id}`)).toHaveClass(/is-active/);
  }

  const text = await page.locator("body").innerText();
  expect(text).not.toMatch(DASHES);
  expect(consoleErrors.filter((line) => !line.includes("501"))).toEqual([]);
});

test("an unknown address says so in plain words", async ({ page }) => {
  await page.goto("/nowhere");
  await expect(page.getByRole("heading", { name: "This page could not open" })).toBeVisible();
  await expect(page.getByText("There is no 4GE page at this address.")).toBeVisible();
});
