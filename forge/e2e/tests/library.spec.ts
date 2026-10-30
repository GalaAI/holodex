import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { expect, test } from "@playwright/test";
import { LIBRARY_GROUPS, LIBRARY_ORIGINS } from "@4ge/contracts";
import { DATA_ROOT, startForge, type ForgeServer } from "../support/forge.js";
import { checkScreen } from "../support/visual.js";

/**
 * The Library grouped by origin (operator rulings 2026-10-30): My projects,
 * Repos, Mods with the plus tile, Augments, Adapters, in that order, every
 * item carrying its origin; clicking an item opens the matching Smith
 * session. The server runs with a PATH that holds only node, Windows and
 * one stand-in Railway CLI that answers "not signed in", so the adapters
 * show both edge cases for real: a CLI that is not installed and a CLI
 * that is installed but not signed in.
 */

let server: ForgeServer;

test.beforeAll(async () => {
  const bin = join(DATA_ROOT, "fake-bin");
  mkdirSync(bin, { recursive: true });
  // What the Railway CLI prints when nobody is signed in (exit code 1, no JSON).
  writeFileSync(join(bin, "railway.cmd"), "@echo off\r\necho Unauthorized. Please login with `railway login` 1>&2\r\nexit /b 1\r\n");
  const system = process.env.SystemRoot ?? "C:\\Windows";
  server = await startForge({
    name: "library",
    port: 17405,
    env: {
      PATH: [bin, dirname(process.execPath), join(system, "System32"), system].join(";"),
      Path: undefined,
      NEBIUS_API_KEY: undefined,
    },
  });
});

test.afterAll(async () => server?.stop());

test("GET /library groups every item by origin, in the ruled order", async () => {
  const library = await server.api.call("library.list", {});
  expect(library.groups.map((group) => group.origin)).toEqual([...LIBRARY_ORIGINS]);
  expect(library.groups.map((group) => group.label)).toEqual(["My projects", "Repos", "Mods", "Augments", "Adapters"]);
  for (const group of library.groups) {
    for (const item of group.items) expect(item.origin, item.id).toBe(group.origin);
  }
  const titles = (origin: string) => library.groups.find((group) => group.origin === origin)?.items.map((item) => item.title) ?? [];
  expect(titles("project")).toContain("Ryozan");
  expect(titles("mod")).toEqual(expect.arrayContaining(["Dev Mod", "PC Mod", "Launch Mod"]));
  expect(titles("adapter")).toEqual(
    expect.arrayContaining(["Northflank", "Cloudflare", "Google Cloud", "Railway", "Vercel", "Supabase", "Docker", "Tailscale"]),
  );
  expect(library.groups.find((group) => group.origin === "mod")?.canCreate).toBe(true);
  expect(library.total).toBe(library.groups.reduce((sum, group) => sum + group.items.length, 0));
});

test("the Library tab shows the five groups in order with the plus tile under Mods", async ({ page }) => {
  await page.goto(`${server.url}/library`);
  const groups = page.locator("section.lib-group");
  await expect(groups).toHaveCount(5, { timeout: 30_000 });
  expect(await groups.evaluateAll((sections) => sections.map((section) => (section as HTMLElement).dataset.origin))).toEqual([...LIBRARY_ORIGINS]);
  for (const origin of LIBRARY_ORIGINS) {
    await expect(page.locator(`section[data-origin="${origin}"] h2`)).toContainText(LIBRARY_GROUPS[origin].label);
  }
  const plus = page.locator('section[data-origin="mod"]').getByRole("button", { name: "Make a mod" });
  await expect(plus).toBeVisible();
  // Only Mods can create; no other group has a plus tile.
  await expect(page.getByRole("button", { name: "Make a mod" })).toHaveCount(1);
  await checkScreen(page, "library-groups");
});

test("an adapter whose CLI is not installed, and one not signed in, say so in plain words", async ({ page }) => {
  const { adapters } = await server.api.call("adapters.list", { query: { refresh: "true" } });
  const vercel = adapters.find((adapter) => adapter.id === "adapter:vercel");
  const railway = adapters.find((adapter) => adapter.id === "adapter:railway");
  expect(vercel?.status).toBe("needs-cli");
  expect(vercel?.statusMessage).toMatch(/The vercel CLI is not installed\. Install it with: /);
  expect(railway?.status).toBe("disconnected");
  expect(railway?.statusMessage).toBe("The Railway CLI is not signed in.");

  await page.goto(`${server.url}/library`);
  const tiles = page.locator('section[data-origin="adapter"]');
  const vercelTile = tiles.locator(".lib-tile").filter({ hasText: "Vercel" });
  await expect(vercelTile).toContainText("CLI missing");
  await expect(vercelTile).toContainText("is not installed");
  const railwayTile = tiles.locator(".lib-tile").filter({ hasText: "Railway" });
  await expect(railwayTile).toContainText("Not connected");
  await expect(railwayTile).toContainText("not signed in");

  // The Launch Mod page names the missing CLI and how to install it.
  await page.goto(`${server.url}/mods`);
  await page.getByRole("link", { name: /Launch Mod/ }).first().click();
  await expect(page.getByText(/CLI vercel is not installed:/)).toBeVisible();
  await checkScreen(page, "mods-launch-adapters");
});

test("the plus tile makes a mod from adapters and augments, and it joins the Mods group", async ({ page }) => {
  await page.goto(`${server.url}/library`);
  await page.locator('section[data-origin="mod"]').getByRole("button", { name: "Make a mod" }).click();
  await expect(page).toHaveURL(/\/mods\?create=1$/);
  await expect(page.getByRole("heading", { name: "Make a mod" })).toBeVisible();
  await checkScreen(page, "mods-create");
  await page.getByLabel("Name").fill("Shop launch");
  await page.getByLabel("What it is for").fill("Ship the shop site to my static host and keep its domain pointed at it.");
  await page.getByLabel(/^Docker \(/).check();
  await page.locator("fieldset", { hasText: "Augments" }).getByRole("checkbox").first().check();
  await page.getByRole("button", { name: "Make mod" }).click();
  await expect(page).not.toHaveURL(/create=1/, { timeout: 20_000 });
  await expect(page.getByRole("heading", { name: "Shop launch" })).toBeVisible();

  const library = await server.api.call("library.list", {});
  const mods = library.groups.find((group) => group.origin === "mod")?.items ?? [];
  expect(mods.map((item) => item.title)).toContain("Shop launch");
  await page.goto(`${server.url}/library`);
  await expect(page.locator('section[data-origin="mod"]')).toContainText("Shop launch");
});

test("a repo shortcut is added under Repos and removed again", async ({ page }) => {
  await page.goto(`${server.url}/library`);
  const repos = page.locator('section[data-origin="repo"]');
  await repos.getByLabel(/repo shortcut/).fill("https://github.com/microsoft/playwright");
  await repos.getByRole("button", { name: "Add repo" }).click();
  await expect(repos.locator(".lib-tile")).toContainText("playwright", { timeout: 20_000 });
  const library = await server.api.call("library.list", {});
  const repo = library.groups.find((group) => group.origin === "repo")?.items[0];
  expect(repo?.origin).toBe("repo");
  await repos.getByRole("button", { name: new RegExp(`Remove ${repo?.title ?? "playwright"}`) }).click();
  await expect(repos.locator(".lib-tile")).toHaveCount(0, { timeout: 20_000 });
  // A bad address is refused in plain words.
  await repos.getByLabel(/repo shortcut/).fill("ftp://example.com/repo");
  await repos.getByRole("button", { name: "Add repo" }).click();
  await expect(repos.getByText(/https:\/\/ or ssh:\/\//)).toBeVisible();
});

test("clicking a Library item opens the matching Smith session", async ({ page }) => {
  await page.goto(`${server.url}/library`);
  await page.locator('section[data-origin="mod"] .lib-tile').filter({ hasText: "Dev Mod" }).getByRole("button").first().click();
  const sessions = await (async () => {
    for (let i = 0; i < 20; i += 1) {
      const list = (await server.api.call("sessions.list", {})).sessions.filter((session) => session.subject?.label === "Dev Mod" || /Dev Mod/.test(session.task));
      if (list.length > 0) return list;
      await page.waitForTimeout(500);
    }
    return [];
  })();
  expect(sessions.length).toBeGreaterThan(0);
  expect(sessions[0]?.agent).toBe("smith");
  await expect(page.locator(".lib")).toContainText(/Dev Mod/);
  await checkScreen(page, "library-smith-session");
});
