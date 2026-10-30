// The 4GE flow end to end against a running server with a real model
// (SPEC "Done means"): Imagistation with Prometheus, the Seed Scroll, Build
// now, four sprints in nested sessions with every checkpoint answered, one
// tweak, the sandbox with staff and customer bots and Icarus's test flight,
// and the build in GET /library under origin "project".
//
// It drives the same contract client the studio uses and writes evidence to
// forge/e2e/evidence/integration (API answers, receipts, the session tree,
// studio screenshots). Paths under the data root and the user's home are
// replaced with placeholders before anything is written; no key is ever read
// here (the server holds it).
//
//   node forge/e2e/live/flow.mjs [--base http://127.0.0.1:17400] [--build <buildId>] [--timeout-min 90]
import { existsSync, mkdirSync, readdirSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { createForgeClient } from "@4ge/contracts";
import { chromium } from "playwright-core";

const here = dirname(fileURLToPath(import.meta.url));
const args = new Map();
for (let index = 2; index < process.argv.length; index += 2) args.set(process.argv[index].replace(/^--/, ""), process.argv[index + 1]);
const base = args.get("base") ?? "http://127.0.0.1:17400";
const evidence = resolve(args.get("evidence") ?? join(here, "..", "evidence", "integration"));
const dataRoot = process.env.FORGE_DATA_ROOT ? resolve(process.env.FORGE_DATA_ROOT) : undefined;
const timeoutMs = Number(args.get("timeout-min") ?? 90) * 60_000;
mkdirSync(evidence, { recursive: true });

const api = createForgeClient({ baseUrl: base });
const started = Date.now();
const log = [];

/** Personal paths and emails never reach the evidence folder. */
function redact(text) {
  let out = String(text);
  const swaps = [
    [dataRoot, "<FORGE_DATA_ROOT>"],
    [dataRoot?.replace(/\\/g, "/"), "<FORGE_DATA_ROOT>"],
    [dataRoot?.replace(/\\/g, "\\\\"), "<FORGE_DATA_ROOT>"],
    [homedir(), "<HOME>"],
    [homedir().replace(/\\/g, "/"), "<HOME>"],
    [homedir().replace(/\\/g, "\\\\"), "<HOME>"],
  ];
  for (const [from, to] of swaps) if (from) out = out.split(from).join(to);
  return out.replace(/[A-Za-z0-9._%+*-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g, (email) =>
    /@example\.(com|test)$/.test(email) ? email : "<email>",
  );
}

/**
 * Adapter subtitles name the signed-in account (a masked email, a tailnet,
 * a CLI user). They are real and belong on screen, not in a committed file.
 */
export function redactLibrary(library) {
  return {
    ...library,
    groups: library.groups.map((group) =>
      group.origin === "adapter"
        ? { ...group, items: group.items.map((item) => (item.subtitle ? { ...item, subtitle: "<signed-in account redacted>" } : item)) }
        : group,
    ),
  };
}

function say(line) {
  const stamp = `${((Date.now() - started) / 1000).toFixed(0).padStart(5)}s`;
  const entry = `${stamp} ${redact(line)}`;
  log.push(entry);
  console.log(entry);
  writeFileSync(join(evidence, "flow-log.txt"), `${log.join("\n")}\n`);
}

function save(name, value) {
  writeFileSync(join(evidence, name), `${redact(JSON.stringify(value, null, 2))}\n`);
}

const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

// The person's side of the brainstorm. Prometheus asks; these answer.
const ANSWERS = [
  "It's called Ink and Iron, a two-artist custom tattoo shop in Portland. People come to us for custom blackwork and fine line. Right now clients can't see our portfolio in one place and have to DM us on Instagram to ask for a booking, and requests get lost.",
  "Customers are adults in Portland looking for a custom piece, often their first or second tattoo. Staff are our two artists and a front desk manager who triages the booking requests and confirms deposits.",
  "Success is ten booking requests a week through the site and far fewer DMs. Must have: a portfolio gallery by artist, a services and pricing guide, hours and location, and a booking request form that explains the deposit. Dark and bold look.",
  "That covers it. Please write the Seed Scroll.",
];

async function imagistation() {
  say("Imagistation: opening a session with Prometheus");
  const opened = await api.call("imagistation.sessions.create", { body: { idea: "a website for my tattoo shop" } });
  const sessionId = opened.session.id;
  const transcript = [...opened.messages];
  say(`Imagistation session ${sessionId}: ${opened.messages.at(-1)?.content.slice(0, 160).replace(/\s+/g, " ")}`);
  for (const answer of ANSWERS) {
    const turn = await api.call("imagistation.messages.send", { params: { id: sessionId }, body: { content: answer } });
    transcript.push(turn.message, turn.reply);
    say(`Prometheus: ${turn.reply.content.slice(0, 240).replace(/\s+/g, " ")}`);
  }
  save("imagistation-transcript.json", transcript.map((message) => ({ role: message.role, agent: message.agent, content: message.content })));
  say("Imagistation: writing the Seed Scroll");
  const { seed } = await api.call("imagistation.seed", { params: { id: sessionId } });
  save("seed.json", seed);
  say(`Seed ${seed.id}: ${seed.title} // kind ${seed.kind} // Blueprint ${seed.blueprintId ?? "none"}`);
  const finalSession = await api.call("imagistation.sessions.get", { params: { id: sessionId } });
  save("imagistation-session.json", finalSession);
  return { sessionId, seed };
}

async function buildNow(seed) {
  say(`Seed ${seed.id}: Build now`);
  const decided = await api.call("seeds.decide", { params: { id: seed.id }, body: { when: "now" } });
  if (!decided.build) throw new Error("Build now returned no build");
  say(`Build ${decided.build.id} started: ${decided.build.layers.length} layers over ${decided.build.stages.length} stages`);
  return decided.build.id;
}

const FINISHED = new Set(["passed", "failed", "cancelled"]);

async function runBuild(buildId) {
  let last = "";
  const answered = new Set();
  let revisions = 0;
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const { build } = await api.call("builds.get", { params: { id: buildId } });
    const line = `${build.status} // ${build.stages.map((stage) => `${stage.stage}:${stage.status}`).join(" ")} // layers ${build.layers
      .map((layer) => `${layer.key}:${layer.status}${layer.testFlight !== "not-run" ? `/${layer.testFlight}` : ""}`)
      .join(" ")}`;
    if (line !== last) {
      say(`Build ${buildId}: ${line}`);
      last = line;
    }
    if (build.openCheckpointId && !answered.has(build.openCheckpointId)) {
      const { checkpoints } = await api.call("builds.checkpoints.list", { params: { id: buildId } });
      const open = checkpoints.find((checkpoint) => checkpoint.id === build.openCheckpointId);
      if (open) {
        save(`checkpoint-${open.stage}.json`, open);
        say(`Checkpoint ${open.stage}: ${open.summary?.slice(0, 240).replace(/\s+/g, " ") ?? open.question}`);
        const decision = await api
          .call("builds.checkpoints.decide", { params: { id: buildId, checkpointId: open.id }, body: { decision: "approve" } })
          .catch((error) => ({ error }));
        if ("error" in decision) {
          // What a person does when a sprint did not pass: ask for a revision with what is wrong.
          const failedLayers = build.layers.filter((layer) => layer.stage === open.stage && layer.status !== "passed");
          const note = `Please fix these and run the sprint again: ${failedLayers
            .map((layer) => `${layer.title}: ${(layer.error ?? layer.status).slice(0, 600)}`)
            .join(" | ")}`.slice(0, 3900);
          say(`Checkpoint ${open.stage}: approve refused (${decision.error.message})`);
          answered.add(open.id);
          if (revisions < 1) {
            revisions += 1;
            const revised = await api
              .call("builds.checkpoints.decide", { params: { id: buildId, checkpointId: open.id }, body: { decision: "revise", note } })
              .catch((error) => ({ error }));
            say(`Checkpoint ${open.stage}: ${"error" in revised ? `revise refused (${revised.error.message})` : "revise sent with the failed layers' problems"}`);
            if ("error" in revised) {
              save("build.json", build);
              return build;
            }
          } else {
            say(`Checkpoint ${open.stage}: already revised once; the driver stops here`);
            save("build.json", build);
            return build;
          }
        } else {
          say(`Checkpoint ${open.stage}: approved`);
          answered.add(open.id);
        }
      }
    }
    if (FINISHED.has(build.status)) {
      save("build.json", build);
      return build;
    }
    if (Date.now() > deadline) {
      save("build.json", build);
      say(`Build ${buildId}: still ${build.status} after ${timeoutMs / 60_000} minutes; stopping the driver (the build keeps running)`);
      return build;
    }
    await sleep(4000);
  }
}

async function afterBuild(buildId, imagistationId) {
  const { build } = await api.call("builds.get", { params: { id: buildId } });
  const receipts = await api.call("builds.receipts", { params: { id: buildId } });
  save("receipts.json", receipts);
  say(`Receipts: ${receipts.receipts.length} recorded // gate ${JSON.stringify(receipts.gate).slice(0, 300)}`);
  if (build.rootSessionId) {
    const { tree } = await api.call("sessions.tree", { params: { id: build.rootSessionId } });
    save("session-tree.json", tree);
    const count = (node) => 1 + node.children.reduce((sum, child) => sum + count(child), 0);
    say(`Session tree under Smith ${build.rootSessionId}: ${count(tree)} sessions`);
  }

  // One tweak on the built app's real source.
  const tweakTarget = { selector: "h1", page: "/", label: "Page headline" };
  const tweak = await api
    .call("design.tweaks.apply", {
      body: { buildId, target: tweakTarget, kind: "color", property: "color", value: "#C9A227" },
    })
    .catch((error) => ({ error }));
  if ("error" in tweak) say(`Tweak: refused (${tweak.error.code}: ${tweak.error.message})`);
  else {
    save("tweak.json", tweak.tweak);
    say(`Tweak ${tweak.tweak.id}: h1 color applied to ${tweak.tweak.changedFiles.join(", ")}`);
  }

  // The sandbox and Icarus's test flight with staff and customer bots.
  let flight;
  const runs = await api.call("sandbox.runs.list", { query: { buildId } });
  save("sandbox-runs-before.json", runs);
  say(`Sandbox runs for the build so far: ${runs.runs.length}`);
  const start = await api.call("sandbox.start", { body: { buildId } }).catch((error) => ({ error }));
  if ("error" in start) say(`Sandbox: start refused (${start.error.code}: ${start.error.message}${start.error.hint ? ` // ${start.error.hint}` : ""})`);
  else {
    // The sandbox answers "starting" at once and comes up in the background.
    let run = start.run;
    const upDeadline = Date.now() + 12 * 60_000;
    while (run.status === "starting" && Date.now() < upDeadline) {
      await sleep(3000);
      run = (await api.call("sandbox.runs.get", { params: { id: run.id } })).run;
    }
    start.run = run;
    save("sandbox-run.json", start.run);
    say(`Sandbox run ${start.run.id}: ${start.run.status} ${start.run.appUrl ?? ""}${start.run.failure ? ` // ${start.run.failure.message.slice(0, 600)}` : ""}`);
    const begun = await api.call("sandbox.testFlight.start", { body: { buildId, runId: start.run.id } }).catch((error) => ({ error }));
    if ("error" in begun) say(`Test flight: refused (${begun.error.code}: ${begun.error.message})`);
    else {
      flight = begun.testFlight;
      const flightDeadline = Date.now() + 15 * 60_000;
      while (!["passed", "failed", "cancelled"].includes(flight.status) && Date.now() < flightDeadline) {
        await sleep(3000);
        flight = (await api.call("sandbox.testFlight.get", { params: { id: flight.id } })).testFlight;
      }
      save("test-flight.json", flight);
      say(
        `Test flight ${flight.id}: ${flight.status} // personas ${flight.personas.map((persona) => `${persona.role}:${persona.name}`).join(", ")} // steps ${flight.steps
          .map((step) => `${step.personaId}:${step.status}`)
          .join(" ")}`,
      );
    }
  }

  // The Library, grouped by origin.
  const library = await api.call("library.list", {});
  save("library.json", redactLibrary(library));
  for (const group of library.groups) say(`Library // ${group.label}: ${group.items.map((item) => item.title).join(", ") || "(empty)"}`);
  const project = library.groups.find((group) => group.origin === "project")?.items.find((item) => item.id === `project:${buildId}`);
  say(project ? `Library: the build is listed under My projects as ${project.title} (${project.status ?? project.tone})` : "Library: the build is NOT listed under My projects");

  await screenshots(buildId, imagistationId, flight);
  return { project: Boolean(project), flight };
}

async function screenshots(buildId, imagistationId, flight) {
  let browser;
  try {
    browser = existsSync(chromium.executablePath()) ? await chromium.launch() : await chromium.launch({ channel: "msedge" });
  } catch (error) {
    say(`Screenshots skipped: no headless browser (${error.message})`);
    return;
  }
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const shots = [
    ["01-imagistation", `/imagistation/${imagistationId ?? ""}`],
    ["02-ide", `/ide/${buildId}`],
    ["03-tweak", `/tweak/${buildId}`],
    ["04-sandbox", `/sandbox/${buildId}`],
    ["05-library", "/library"],
    ["06-mods", "/mods"],
  ];
  for (const [name, path] of shots) {
    try {
      await page.goto(`${base}${path}`, { waitUntil: "networkidle", timeout: 45_000 });
      await sleep(1500);
      await page.screenshot({ path: join(evidence, `${name}.png`), fullPage: false });
      say(`Screenshot ${name}.png of ${path}`);
    } catch (error) {
      say(`Screenshot ${name} failed: ${error.message}`);
    }
  }
  // The IDE inspector on the first artboard, with the tweak controls open.
  try {
    await page.goto(`${base}/ide/${buildId}`, { waitUntil: "networkidle", timeout: 45_000 });
    const frame = page.locator('[data-testid^="frame-preview:"]').first();
    if ((await frame.count()) > 0) {
      await frame.click({ position: { x: 20, y: 20 } });
      const tweak = page.getByTestId("inspector-tweak");
      if ((await tweak.count()) > 0) await tweak.click();
      await sleep(1000);
      await page.screenshot({ path: join(evidence, "07-ide-inspector-tweak.png") });
      say("Screenshot 07-ide-inspector-tweak.png of the IDE inspector with the tweak controls");
    } else say("Screenshot 07 skipped: the IDE board has no artboard frames");
  } catch (error) {
    say(`Screenshot 07 failed: ${error.message}`);
  }
  if (flight) {
    const shotsDir = join(evidence, "flight");
    mkdirSync(shotsDir, { recursive: true });
    const withShots = flight.steps.filter((step) => step.screenshot).slice(0, 6);
    for (const [index, step] of withShots.entries()) {
      const response = await fetch(api.url("builds.files.get", { id: buildId }, { path: step.screenshot }));
      if (response.ok) writeFileSync(join(shotsDir, `${String(index + 1).padStart(2, "0")}-${step.personaId}.png`), Buffer.from(await response.arrayBuffer()));
    }
    say(`Copied ${readdirSync(shotsDir).length} bot screenshots to flight/`);
  }
  await browser.close();
}

try {
  const health = await api.call("health", {});
  save("health.json", health);
  say(`Server ${health.name} ${health.version} // layers ${JSON.stringify(health.layers)} // studio served ${health.studio.served}`);
  const models = await api.call("models.list", {});
  save("models.json", { route: models.route, modelCount: models.models.length });
  say(`Model route: ${models.route.provider} // lead ${models.route.models.lead} // worker ${models.route.models.worker} // ${models.models.length} models listed`);
  if (models.route.provider === "none") throw new Error(`No model is connected: ${models.route.reason}`);

  let buildId = args.get("build");
  let imagistationId = args.get("imagistation");
  if (!buildId) {
    const { sessionId, seed } = await imagistation();
    imagistationId = sessionId;
    buildId = await buildNow(seed);
  }
  const build = await runBuild(buildId);
  const outcome = await afterBuild(buildId, imagistationId);
  save("summary.json", {
    buildId,
    imagistationId,
    buildStatus: build.status,
    layers: build.layers.map((layer) => ({ key: layer.key, stage: layer.stage, status: layer.status, testFlight: layer.testFlight, receiptId: layer.receiptId })),
    inLibraryAsProject: outcome.project,
    testFlight: outcome.flight ? { id: outcome.flight.id, status: outcome.flight.status } : null,
    minutes: Math.round((Date.now() - started) / 60_000),
  });
  say(`Done: build ${build.status}, in Library ${outcome.project}, test flight ${outcome.flight?.status ?? "not run"}`);
} catch (error) {
  say(`Flow stopped: ${error?.code ? `${error.code}: ` : ""}${error?.message ?? String(error)}${error?.hint ? ` // ${error.hint}` : ""}`);
  process.exitCode = 1;
}
