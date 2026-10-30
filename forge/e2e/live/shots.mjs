// Extra studio screenshots for a finished build: the sandbox page (it keeps a
// live stream open, so it waits for load, not network idle) and the IDE
// inspector on an HTML artboard with the tweak controls open.
//
//   node forge/e2e/live/shots.mjs <buildId> [--base http://127.0.0.1:17400]
import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright-core";

const here = dirname(fileURLToPath(import.meta.url));
const buildId = process.argv[2];
const base = process.argv[4] ?? "http://127.0.0.1:17400";
const evidence = join(here, "..", "evidence", "integration");
const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

const browser = existsSync(chromium.executablePath()) ? await chromium.launch() : await chromium.launch({ channel: "msedge" });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

await page.goto(`${base}/sandbox/${buildId}`, { waitUntil: "load" });
await sleep(4000);
await page.screenshot({ path: join(evidence, "04-sandbox.png") });
console.log("04-sandbox.png");

await page.goto(`${base}/ide/${buildId}`, { waitUntil: "load" });
await sleep(3000);
const html = page.locator('[data-testid^="frame-preview:"][data-testid$=".html"]');
console.log(`HTML artboards on the board: ${await html.count()}`);
if ((await html.count()) > 0) {
  await html.first().click({ position: { x: 30, y: 30 } });
  await sleep(1000);
  await page.getByTestId("inspector-tweak").click();
  await sleep(1500);
  await page.screenshot({ path: join(evidence, "07-ide-inspector-tweak.png") });
  console.log("07-ide-inspector-tweak.png");
}
await browser.close();
