// Restart a build's sandbox so it carries the latest tweaks, then capture
// the app's headline and its computed color as evidence the tweak reached the
// running app.
//
//   node forge/e2e/live/tweak-in-sandbox.mjs <buildId> [--base http://127.0.0.1:17400]
import { existsSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { createForgeClient } from "@4ge/contracts";
import { chromium } from "playwright-core";

const here = dirname(fileURLToPath(import.meta.url));
const buildId = process.argv[2];
const base = process.argv[4] ?? "http://127.0.0.1:17400";
const evidence = join(here, "..", "evidence", "integration");
const api = createForgeClient({ baseUrl: base });
const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

const { runs } = await api.call("sandbox.runs.list", { query: { buildId } });
for (const run of runs.filter((item) => item.status === "running" || item.status === "starting")) {
  await api.call("sandbox.stop", { body: { runId: run.id } });
  console.log(`stopped ${run.id}`);
}
let { run } = await api.call("sandbox.start", { body: { buildId } });
const deadline = Date.now() + 12 * 60_000;
while (run.status === "starting" && Date.now() < deadline) {
  await sleep(3000);
  run = (await api.call("sandbox.runs.get", { params: { id: run.id } })).run;
}
console.log(`sandbox ${run.id}: ${run.status} ${run.appUrl ?? ""}`);
if (run.status !== "running" || !run.appUrl) process.exit(1);

const browser = existsSync(chromium.executablePath()) ? await chromium.launch() : await chromium.launch({ channel: "msedge" });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
await page.goto(run.appUrl, { waitUntil: "load" });
await sleep(1500);
const color = await page.locator("h1").first().evaluate((element) => getComputedStyle(element).color);
await page.screenshot({ path: join(evidence, "08-sandbox-app-after-tweak.png") });
writeFileSync(join(evidence, "tweak-in-sandbox.json"), `${JSON.stringify({ runId: run.id, appUrl: run.appUrl, h1Color: color, expected: "rgb(201, 162, 39)" }, null, 2)}\n`);
console.log(`h1 color in the running app: ${color}`);
await browser.close();
