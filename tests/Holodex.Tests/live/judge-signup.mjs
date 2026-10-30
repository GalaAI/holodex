// Headless sign-up and consent for the judge-path proof (JudgePathLiveTests). It drives a Hivemind DEV gateway's
// /authorize -> /login -> Create an account -> /signup -> consent pages in headless Chromium, so no person's browser
// is used, and ends on Holodex's loopback redirect. It refuses any host that is not this PC.
//
//   node judge-signup.mjs <authorization-url>
//   env: PLAYWRIGHT_CORE (path to the playwright-core package), HOLODEX_SIGNUP_EMAIL, HOLODEX_SIGNUP_PASSWORD,
//        HOLODEX_SIGNUP_INVITE, HOLODEX_SIGNUP_TRACE (folder for consent.html and screenshots, optional)
// The password is never printed.
import { createRequire } from "node:module";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_CORE ?? "playwright-core");

const authUrl = new URL(process.argv[2]);
if (!["127.0.0.1", "localhost", "[::1]"].includes(authUrl.hostname)) {
  console.error(`refused: ${authUrl.hostname} is not a local development host`);
  process.exit(2);
}
const email = process.env.HOLODEX_SIGNUP_EMAIL;
const password = process.env.HOLODEX_SIGNUP_PASSWORD;
const invite = process.env.HOLODEX_SIGNUP_INVITE;
const trace = process.env.HOLODEX_SIGNUP_TRACE;
if (!email || !password || !invite) {
  console.error("HOLODEX_SIGNUP_EMAIL, HOLODEX_SIGNUP_PASSWORD and HOLODEX_SIGNUP_INVITE are required");
  process.exit(2);
}
if (trace) mkdirSync(trace, { recursive: true });

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage();
const shot = async (name) => { if (trace) await page.screenshot({ path: join(trace, `${name}.png`), fullPage: true }); };
try {
  await page.goto(authUrl.toString(), { waitUntil: "load" });
  console.log(`landed ${new URL(page.url()).pathname}`);
  await shot("1-login");

  let signUp = process.env.HOLODEX_SIGNIN_EXISTING !== "1";
  if (!signUp) {
    // An account that may exist already (the dev gateway limits sign-ups per address): email and password on
    // /login; when the page refuses them, the same email and password sign up instead.
    await page.fill("#account-email", email);
    await page.fill('input[name="owner_key"]', password);
    await Promise.all([page.waitForLoadState("load"), page.click("button.connect")]);
    console.log(`after sign-in ${new URL(page.url()).pathname}`);
    if ((await page.locator(".error[role=alert]").count()) > 0 && (await page.locator('a[href^="/signup"]').count()) > 0) {
      console.log("sign-in refused; signing up with the same email");
      signUp = true;
    }
  }
  if (signUp) {
    await page.click('a[href^="/signup"]');
    await page.waitForSelector("#signup-email");
    await page.fill("#signup-name", "Holodex Judge Probe");
    await page.fill("#signup-email", email);
    await page.fill('input[name="password"]', password);
    await page.fill("#signup-code", invite);
    await shot("2-signup");
    await Promise.all([page.waitForLoadState("load"), page.click("button.connect")]);
    console.log(`after signup ${new URL(page.url()).pathname}`);
  }
  const refused = await page.locator(".error[role=alert]").first().innerText().catch(() => "");
  if (refused) throw new Error(`the page refused: ${refused.replace(/\s+/g, " ").trim()}`);

  if (trace) writeFileSync(join(trace, "consent.html"), await page.content());
  await shot("3-consent");
  const scopes = await page.$$eval('input[type="checkbox"]', (boxes) => boxes.map((b) => `${b.name || b.value}:${b.checked ? "on" : "off"}`));
  console.log(`consent scopes ${scopes.join(" ") || "(no checkboxes)"}`);
  const approve = page.locator("button[type=submit], input[type=submit]").filter({ hasText: /authori[sz]e|allow|approve|continue|connect/i }).first();
  const fallback = page.locator("button[type=submit], input[type=submit]").first();
  const target = (await approve.count()) > 0 ? approve : fallback;
  console.log(`consent button "${(await target.innerText().catch(() => "")).trim()}"`);
  await Promise.all([page.waitForURL(/^http:\/\/127\.0\.0\.1:47613\//, { timeout: 30000 }), target.click()]);
  console.log(`redirected to ${new URL(page.url()).origin}${new URL(page.url()).pathname}`);
  console.log(`page says: ${(await page.innerText("body")).trim().slice(0, 120)}`);
} catch (error) {
  await shot("x-failure").catch(() => {});
  if (trace) writeFileSync(join(trace, "failure.html"), await page.content().catch(() => ""));
  console.error(`failed at ${page.url().split("?")[0]}: ${error.message.split("\n")[0]}`);
  process.exitCode = 1;
} finally {
  await browser.close();
}
