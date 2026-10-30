// 4GE sandbox bots: play each persona of a test flight against the app in the
// sandbox browser and record every step with a screenshot.
//
// Adapted from GalaAI/ark apps/desktop-node/aethernet/runner.mjs (origin/main):
// the fixed-runtime rule (the caller passes a validated plan, never code), the
// safeText redaction, sha256 artifact records, settle-then-capture screenshots
// with animations disabled, and the always-written manifest in `finally`. The
// Ark runner launched its own headless Chromium for one URL; this one connects
// over CDP to the sandbox's AEthernet Chromium, opens one isolated browser
// context per persona, and clicks through the UI: entry page, scripted moves,
// the links a persona would follow, buttons, forms, then accessibility basics.
import { createHash } from "node:crypto";
import { lookup } from "node:dns/promises";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { chromium } from "playwright-core";

const WORK = process.env.FORGE_WORK_DIR ?? "/work";
const plan = JSON.parse(await readFile(`${WORK}/plan.json`, "utf8"));
const limits = { pages: 6, buttons: 3, forms: 2, stepTimeoutMs: 15_000, ...(plan.limits ?? {}) };
const startedAt = new Date().toISOString();
const artifacts = [];
const issues = [];
const results = [];
let browser;

function sha256(value) {
  return createHash("sha256").update(value).digest("hex");
}

function safeText(value) {
  return String(value)
    .slice(0, 1_500)
    .replace(/(authorization|bearer|token|api[_-]?key|password)\s*[:=]\s*\S+/gi, "$1=[redacted]");
}

function slug(text) {
  return (
    String(text)
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-+|-+$/g, "")
      .slice(0, 40) || "step"
  );
}

function withTimeout(promise, ms, what) {
  let timer;
  return Promise.race([
    promise.finally(() => clearTimeout(timer)),
    new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error(`${what} took longer than ${Math.round(ms / 1000)} seconds`)), ms);
    }),
  ]);
}

const appUrl = new URL(plan.browserUrl);
const directUrl = new URL(plan.directUrl);

/** HTTP status of a path, asked straight from the app service (not through the browser). */
async function directStatus(path) {
  try {
    const response = await fetch(new URL(path, directUrl), { redirect: "follow" });
    return response.status;
  } catch {
    return null;
  }
}

/** Accessibility basics, evaluated in the page. */
function auditPage() {
  const problems = [];
  if (!document.title.trim()) problems.push("The page has no title");
  if (!document.documentElement.getAttribute("lang")) problems.push("The html element has no lang attribute");
  const visible = (element) => {
    const box = element.getBoundingClientRect();
    const style = getComputedStyle(element);
    return box.width > 0 && box.height > 0 && style.visibility !== "hidden" && style.display !== "none";
  };
  const noAlt = [...document.querySelectorAll("img")].filter((img) => !img.hasAttribute("alt")).length;
  if (noAlt > 0) problems.push(`${noAlt} image(s) have no alt text`);
  const named = (element) =>
    Boolean(
      element.getAttribute("aria-label")?.trim() ||
        element.getAttribute("aria-labelledby") ||
        element.getAttribute("title")?.trim(),
    );
  const fields = [...document.querySelectorAll("input, select, textarea")].filter(
    (field) => !["hidden", "submit", "button", "reset", "image"].includes(field.getAttribute("type") ?? "") && visible(field),
  );
  const unlabeled = fields.filter((field) => {
    if (named(field)) return false;
    if (field.closest("label")) return false;
    if (field.id && document.querySelector(`label[for="${CSS.escape(field.id)}"]`)) return false;
    return true;
  }).length;
  if (unlabeled > 0) problems.push(`${unlabeled} form field(s) have no label`);
  const controls = [...document.querySelectorAll("a[href], button, [role=button]")].filter(visible);
  const nameless = controls.filter((control) => {
    if (named(control)) return false;
    if (control.textContent?.trim()) return false;
    if ([...control.querySelectorAll("img[alt], svg[aria-label]")].some((img) => (img.getAttribute("alt") ?? img.getAttribute("aria-label"))?.trim())) return false;
    return true;
  }).length;
  if (nameless > 0) problems.push(`${nameless} button(s) or link(s) have no name`);
  const h1 = document.querySelectorAll("h1").length;
  if (h1 === 0) problems.push("The page has no main heading (h1)");
  return { title: document.title.trim(), problems };
}

/** Same-origin links on the current page that lead somewhere new. */
async function collectLinks(page) {
  const found = await page.evaluate(() =>
    [...document.querySelectorAll("a[href]")]
      .filter((anchor) => {
        const box = anchor.getBoundingClientRect();
        return box.width > 0 && box.height > 0;
      })
      .map((anchor) => ({
        href: anchor.href,
        text: (anchor.textContent ?? "").trim().replace(/\s+/g, " ").slice(0, 80) || anchor.getAttribute("aria-label") || "",
        rawHref: anchor.getAttribute("href") ?? "",
      })),
  );
  const seen = new Set();
  const links = [];
  for (const link of found) {
    let url;
    try {
      url = new URL(link.href);
    } catch {
      continue;
    }
    if (url.origin !== appUrl.origin) continue;
    if (link.rawHref.startsWith("#")) continue;
    if (seen.has(url.pathname)) continue;
    seen.add(url.pathname);
    links.push({ path: url.pathname + url.search, pathname: url.pathname, text: link.text, rawHref: link.rawHref });
  }
  return links;
}

const ROLE_WORDS = {
  staff: ["admin", "dashboard", "manage", "staff", "orders", "bookings", "requests", "inbox", "settings", "schedule"],
  customer: ["book", "contact", "shop", "services", "about", "pricing", "menu", "gallery", "order", "appointment"],
};

function rankLinks(links, persona) {
  const words = new Set(
    [...persona.goals, persona.brief]
      .join(" ")
      .toLowerCase()
      .split(/[^a-z0-9]+/)
      .filter((word) => word.length > 3),
  );
  for (const word of ROLE_WORDS[persona.role] ?? []) words.add(word);
  const score = (link) =>
    `${link.text} ${link.pathname}`
      .toLowerCase()
      .split(/[^a-z0-9]+/)
      .filter((word) => words.has(word)).length;
  return [...links].sort((left, right) => score(right) - score(left));
}

function testValue(field, persona, goal) {
  const hint = `${field.name} ${field.id} ${field.label} ${field.placeholder}`.toLowerCase();
  switch (field.type) {
    case "email":
      return `${slug(persona.id)}@example.test`;
    case "tel":
      return "555 0100";
    case "number":
    case "range":
      return field.min && field.min !== "" ? field.min : "2";
    case "date": {
      const date = new Date(Date.now() + 7 * 86_400_000);
      return date.toISOString().slice(0, 10);
    }
    case "time":
      return "14:00";
    case "datetime-local": {
      const date = new Date(Date.now() + 7 * 86_400_000);
      return `${date.toISOString().slice(0, 10)}T14:00`;
    }
    case "url":
      return "https://example.test";
    case "password":
      return "sandbox-only-Test-1";
    default:
      break;
  }
  if (/mail/.test(hint)) return `${slug(persona.id)}@example.test`;
  if (/phone|tel/.test(hint)) return "555 0100";
  if (/name/.test(hint)) return persona.name;
  if (field.tag === "textarea" || /message|note|detail|comment|idea|description/.test(hint)) {
    return `Sandbox test from ${persona.name}: ${goal}`;
  }
  return `Test ${persona.name}`;
}

async function playPersona(persona) {
  const dir = `${WORK}/${slug(persona.id)}`;
  await mkdir(dir, { recursive: true });
  const record = {
    personaId: persona.id,
    role: persona.role,
    entryPath: "/",
    steps: [],
    pages: [],
    consoleErrors: [],
    pageErrors: [],
    failedRequests: [],
  };
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 }, serviceWorkers: "block" });
  const page = await context.newPage();
  let navigationStatus = null;
  page.on("console", (message) => {
    if (message.type() === "error") record.consoleErrors.push(safeText(message.text()));
  });
  page.on("pageerror", (error) => record.pageErrors.push(safeText(error.message)));
  page.on("response", (response) => {
    const request = response.request();
    let mainNavigation = false;
    try {
      mainNavigation = request.isNavigationRequest() && request.frame() === page.mainFrame();
    } catch {}
    if (mainNavigation) navigationStatus = response.status();
    let origin = null;
    try {
      origin = new URL(response.url()).origin;
    } catch {}
    if (origin === appUrl.origin && response.status() >= 400 && !request.isNavigationRequest()) {
      record.failedRequests.push({ url: new URL(response.url()).pathname, status: response.status() });
    }
  });

  let index = 0;
  async function capture(label) {
    index += 1;
    const file = `${String(index).padStart(2, "0")}-${slug(label)}.png`;
    try {
      const image = await page.screenshot({ fullPage: true, type: "png", animations: "disabled", timeout: 10_000 });
      await writeFile(`${dir}/${file}`, image);
      const relative = `${slug(persona.id)}/${file}`;
      artifacts.push({ kind: "screenshot", path: relative, sha256: sha256(image), sizeBytes: image.byteLength });
      return relative;
    } catch (error) {
      issues.push(`Screenshot for "${label}" failed: ${safeText(error.message)}`);
      return undefined;
    }
  }

  async function settle() {
    await page.waitForLoadState("domcontentloaded", { timeout: 10_000 }).catch(() => {});
    await page.waitForLoadState("networkidle", { timeout: 4_000 }).catch(() => {});
  }

  async function auditHere() {
    const path = new URL(page.url()).pathname;
    if (record.pages.some((entry) => entry.path === path)) return;
    const audit = await page.evaluate(auditPage).catch((error) => ({ title: "", problems: [`Audit failed: ${error.message}`] }));
    record.pages.push({ path, status: navigationStatus ?? (await directStatus(path)), title: audit.title, problems: audit.problems });
  }

  async function step(kind, action, expectation, run) {
    const entry = {
      id: `${slug(persona.id)}-${String(record.steps.length + 1).padStart(2, "0")}`,
      personaId: persona.id,
      kind,
      action,
      expectation,
      status: "running",
      at: new Date().toISOString(),
    };
    record.steps.push(entry);
    const errorsBefore = record.pageErrors.length;
    try {
      await withTimeout(Promise.resolve().then(run), limits.stepTimeoutMs, action);
      await settle();
      const newErrors = record.pageErrors.slice(errorsBefore);
      if (newErrors.length > 0) throw new Error(`Script error on the page: ${newErrors[0]}`);
      entry.status = "passed";
    } catch (error) {
      entry.status = "failed";
      entry.error = safeText(error instanceof Error ? error.message : error);
    }
    entry.screenshot = await capture(action);
    await auditHere().catch(() => {});
    return entry.status === "passed";
  }

  async function open(path) {
    navigationStatus = null;
    const response = await page.goto(new URL(path, appUrl).toString(), { waitUntil: "domcontentloaded", timeout: 20_000 });
    navigationStatus = response?.status() ?? navigationStatus;
    await settle();
    return navigationStatus;
  }

  async function locate(target) {
    const candidates = [
      page.getByRole("link", { name: target, exact: false }),
      page.getByRole("button", { name: target, exact: false }),
      page.getByLabel(target, { exact: false }),
      page.getByText(target, { exact: false }),
    ];
    for (const candidate of candidates) {
      if ((await candidate.count().catch(() => 0)) > 0 && (await candidate.first().isVisible().catch(() => false))) {
        return candidate.first();
      }
    }
    try {
      const css = page.locator(target);
      if ((await css.count()) > 0) return css.first();
    } catch {}
    throw new Error(`Could not find "${target}" on ${new URL(page.url()).pathname}`);
  }

  // 1. Entry page.
  let entryPath = persona.entryPath;
  if (!entryPath && persona.role === "staff") {
    for (const candidate of plan.staffPaths ?? []) {
      const status = await directStatus(candidate);
      if (status !== null && status < 400) {
        entryPath = candidate;
        break;
      }
    }
  }
  entryPath = entryPath ?? "/";
  record.entryPath = entryPath;
  const opened = await step(
    "open",
    `Open ${entryPath} as ${persona.name}`,
    "The page loads with a status below 400 and shows visible content",
    async () => {
      const status = await open(entryPath);
      if (status !== null && status >= 400) throw new Error(`${entryPath} answered ${status}`);
      const text = (await page.locator("body").innerText({ timeout: 5_000 })).trim();
      if (text.length === 0) throw new Error(`${entryPath} rendered no visible text`);
    },
  );

  if (opened) {
    // 2. Scripted moves from the Blueprint.
    for (const move of persona.script ?? []) {
      const label =
        move.label ??
        {
          goto: `Go to ${move.target}`,
          click: `Click "${move.target}"`,
          fill: `Type into "${move.target}"`,
          select: `Choose "${move.value ?? ""}" in "${move.target}"`,
          press: `Press ${move.value ?? "Enter"} in "${move.target}"`,
          "expect-text": `Look for "${move.target}"`,
          "expect-path": `Check the page is ${move.target}`,
        }[move.action];
      await step("script", label, "The move works without an error page or a script error", async () => {
        if (move.action === "goto") {
          const status = await open(move.target);
          if (status !== null && status >= 400) throw new Error(`${move.target} answered ${status}`);
        } else if (move.action === "click") {
          navigationStatus = null;
          await (await locate(move.target)).click({ timeout: 8_000 });
          await settle();
          if (navigationStatus !== null && navigationStatus >= 400) throw new Error(`The click led to a page that answered ${navigationStatus}`);
        } else if (move.action === "fill") {
          await (await locate(move.target)).fill(move.value ?? "", { timeout: 8_000 });
        } else if (move.action === "select") {
          await (await locate(move.target)).selectOption({ label: move.value ?? "" }, { timeout: 8_000 });
        } else if (move.action === "press") {
          await (await locate(move.target)).press(move.value ?? "Enter", { timeout: 8_000 });
        } else if (move.action === "expect-text") {
          await page.getByText(move.target, { exact: false }).first().waitFor({ state: "visible", timeout: 8_000 });
        } else if (move.action === "expect-path") {
          const now = new URL(page.url()).pathname;
          if (now !== move.target) throw new Error(`The page is ${now}, not ${move.target}`);
        }
      });
    }

    // 3. Follow the links this persona would follow, most relevant first.
    await open(entryPath).catch(() => {});
    const links = rankLinks(await collectLinks(page), persona)
      .filter((link) => link.pathname !== new URL(page.url()).pathname)
      .slice(0, limits.pages);
    for (const link of links) {
      const name = link.text || link.pathname;
      await step(
        "link",
        `Follow the link "${name}" from ${entryPath}`,
        `${link.pathname} opens with a status below 400 and no script error`,
        async () => {
          await open(entryPath);
          navigationStatus = null;
          const anchor = page.locator(`a[href="${link.rawHref.replace(/"/g, '\\"')}"]`).first();
          await anchor.click({ timeout: 8_000 });
          await page.waitForURL((url) => url.pathname === link.pathname, { timeout: 8_000 }).catch(() => {});
          await settle();
          const status = navigationStatus ?? (await directStatus(link.path));
          if (status !== null && status >= 400) throw new Error(`${link.pathname} answered ${status}`);
          const now = new URL(page.url()).pathname;
          if (now !== link.pathname) throw new Error(`The click stayed on ${now} instead of opening ${link.pathname}`);
        },
      );
    }

    // 4. Buttons on the entry page that are not form submits.
    await open(entryPath).catch(() => {});
    const buttonNames = await page
      .evaluate(() =>
        [...document.querySelectorAll("button, [role=button]")]
          .filter((button) => !button.closest("form") && button.getAttribute("type") !== "submit")
          .filter((button) => {
            const box = button.getBoundingClientRect();
            return box.width > 0 && box.height > 0 && !button.hasAttribute("disabled");
          })
          .map((button) => (button.getAttribute("aria-label") ?? button.textContent ?? "").trim().replace(/\s+/g, " "))
          .filter((name) => name.length > 0 && !/log ?out|sign ?out/i.test(name)),
      )
      .catch(() => []);
    for (const name of [...new Set(buttonNames)].slice(0, limits.buttons)) {
      await step("button", `Press the "${name.slice(0, 60)}" button`, "The page responds without a script error", async () => {
        await open(entryPath);
        await page.getByRole("button", { name, exact: true }).first().click({ timeout: 8_000 });
        await page.waitForTimeout(500);
      });
    }

    // 5. Forms on the pages this persona saw, filled with plain test data.
    let formsTried = 0;
    const formPages = [entryPath, ...links.map((link) => link.path)];
    for (const path of formPages) {
      if (formsTried >= limits.forms) break;
      await open(path).catch(() => {});
      const formCount = await page.locator("form").count().catch(() => 0);
      for (let formIndex = 0; formIndex < formCount && formsTried < limits.forms; formIndex += 1) {
        formsTried += 1;
        const goal = persona.goals[formsTried - 1] ?? persona.goals[0];
        await step(
          "form",
          `Fill in and send the form on ${new URL(page.url()).pathname}`,
          "The form sends without an error page or a script error",
          async () => {
            await open(path);
            const form = page.locator("form").nth(formIndex);
            const fields = await form.evaluate((element) =>
              [...element.querySelectorAll("input, textarea, select")]
                .map((field, position) => {
                  const box = field.getBoundingClientRect();
                  const labelled = field.id ? document.querySelector(`label[for="${CSS.escape(field.id)}"]`) : null;
                  return {
                    position,
                    tag: field.tagName.toLowerCase(),
                    type: (field.getAttribute("type") ?? (field.tagName === "TEXTAREA" ? "textarea" : "text")).toLowerCase(),
                    name: field.getAttribute("name") ?? "",
                    id: field.id ?? "",
                    placeholder: field.getAttribute("placeholder") ?? "",
                    label: (labelled?.textContent ?? field.closest("label")?.textContent ?? field.getAttribute("aria-label") ?? "").trim(),
                    min: field.getAttribute("min") ?? "",
                    required: field.hasAttribute("required"),
                    visible: box.width > 0 && box.height > 0 && !field.disabled && !field.readOnly,
                  };
                })
                .filter((field) => field.visible && !["hidden", "submit", "button", "reset", "image", "file"].includes(field.type)),
            );
            const all = form.locator("input, textarea, select");
            const radiosDone = new Set();
            for (const field of fields) {
              const handle = all.nth(field.position);
              if (field.tag === "select") {
                const value = await handle.evaluate((select) => [...select.options].find((option) => option.value)?.value ?? "");
                if (value) await handle.selectOption(value);
              } else if (field.type === "checkbox") {
                if (field.required) await handle.check();
              } else if (field.type === "radio") {
                if (!radiosDone.has(field.name)) {
                  radiosDone.add(field.name);
                  await handle.check();
                }
              } else {
                await handle.fill(testValue(field, persona, goal));
              }
            }
            navigationStatus = null;
            const submit = form.locator('button[type=submit], input[type=submit], button:not([type])').first();
            if ((await submit.count()) > 0) await submit.click({ timeout: 8_000 });
            else if (fields.length > 0) await all.nth(fields[fields.length - 1].position).press("Enter");
            else throw new Error("The form has no fields and no submit button");
            await page.waitForTimeout(800);
            await settle();
            if (navigationStatus !== null && navigationStatus >= 400) {
              throw new Error(`Sending the form led to a page that answered ${navigationStatus}`);
            }
            // Still on the form's page: either the app handled it in place or the
            // browser refused it. Only ask the form when it is still there.
            if (new URL(page.url()).pathname === new URL(path, appUrl).pathname) {
              const invalid = await form
                .evaluate((element) => !element.checkValidity(), undefined, { timeout: 2_000 })
                .catch(() => false);
              if (invalid) throw new Error("The browser refused to send the form: a field is still invalid after filling it");
            }
          },
        );
      }
    }
  }

  // 6. Accessibility basics across every page this persona saw.
  await step(
    "accessibility",
    `Check accessibility basics on the ${record.pages.length || 1} page(s) ${persona.name} saw`,
    "Every page has a title, a language, alt text on images, labels on fields, names on buttons and links, and a main heading",
    async () => {
      const failing = record.pages.filter((entry) => entry.problems.length > 0);
      if (failing.length > 0) {
        throw new Error(failing.map((entry) => `${entry.path}: ${entry.problems.join("; ")}`).join(" | "));
      }
    },
  );

  for (const entry of record.steps) {
    if (entry.status === "running") entry.status = "failed";
  }
  await context.close().catch(() => {});
  return record;
}

try {
  const { address } = await lookup(plan.cdp.host);
  browser = await withTimeout(
    chromium.connectOverCDP(`http://${address}:${plan.cdp.port}`),
    30_000,
    "Connecting to the sandbox browser",
  );
  for (const persona of plan.personas) {
    try {
      results.push(await playPersona(persona));
    } catch (error) {
      issues.push(`${persona.name}: ${safeText(error instanceof Error ? error.message : error)}`);
      results.push({
        personaId: persona.id,
        role: persona.role,
        entryPath: persona.entryPath ?? "/",
        steps: [
          {
            id: `${slug(persona.id)}-01`,
            personaId: persona.id,
            kind: "open",
            action: `Play ${persona.name}`,
            expectation: "The persona runs to the end",
            status: "failed",
            error: safeText(error instanceof Error ? error.message : error),
            at: new Date().toISOString(),
          },
        ],
        pages: [],
        consoleErrors: [],
        pageErrors: [],
        failedRequests: [],
      });
    }
  }
} catch (error) {
  issues.push(safeText(error instanceof Error ? error.message : error));
  process.exitCode = 1;
} finally {
  await browser?.close().catch(() => {});
  await writeFile(
    `${WORK}/results.json`,
    JSON.stringify(
      {
        schemaVersion: 1,
        flightId: plan.flightId,
        startedAt,
        finishedAt: new Date().toISOString(),
        personas: results,
        artifacts,
        issues,
      },
      null,
      2,
    ),
    "utf8",
  );
}
