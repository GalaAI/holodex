import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  blueprintSchema,
  makeId,
  type AtlasEntry,
  type Blueprint,
  type BlueprintInput,
  type SessionEvent,
  type TestFlight,
} from "@4ge/contracts";
import { dataPaths, ensureDataDirs } from "@4ge/contracts/node";
import { afterEach, describe, expect, it } from "vitest";
import { OrchestraError } from "./errors.js";
import type { ChatRequest, ChatResult, ModelClient } from "./model.js";
import { Orchestra } from "./orchestra.js";
import { LayerNotConnected, type AtlasPort, type SandboxPort } from "./ports.js";
import type { EventDraft } from "./util.js";

/* ------------------------------------------------------------------ */
/* Test doubles: a scripted model, an in-memory Atlas, a sandbox.       */
/* The orchestra code under test is the real one.                       */
/* ------------------------------------------------------------------ */

const WEBSITE: BlueprintInput = {
  blueprintId: "blueprint:test-website",
  name: "Test website",
  version: "1.0.0",
  state: "promoted",
  origin: "starter",
  kind: "website",
  platforms: ["web"],
  description: "A small business website with bookings",
  parameters: [{ key: "businessName", description: "The business name", required: true }],
  stages: [
    {
      stage: "architecture",
      goal: "Content model and API for {{businessName}}",
      layers: [
        {
          key: "content-model",
          kind: "data",
          title: "Content model",
          agent: "hephaestus",
          description: "Services and artists for {{businessName}}",
          deliverables: ["src/content.json"],
          checks: ["Content lists services"],
        },
        {
          key: "api",
          kind: "backend",
          title: "API",
          agent: "hephaestus",
          description: "Serve the content",
          dependsOn: ["content-model"],
          deliverables: ["src/server.mjs"],
          checks: ["Server answers /health"],
        },
      ],
      checkpoint: { title: "Architecture review", question: "Does the content model fit your shop?" },
    },
    {
      stage: "orchestration",
      goal: "Booking flow",
      layers: [
        {
          key: "booking",
          kind: "orchestration",
          title: "Booking flow",
          agent: "athena",
          description: "Take booking requests",
          dependsOn: ["api"],
          deliverables: ["src/booking.mjs"],
          checks: ["Booking requests are validated"],
        },
      ],
      checkpoint: { title: "Booking review", question: "Is the booking flow right?" },
    },
    {
      stage: "interface",
      goal: "Pages",
      layers: [
        {
          key: "pages",
          kind: "interface",
          title: "Pages",
          agent: "smith",
          description: "Landing page",
          deliverables: ["public/index.html"],
          checks: ["Landing page shows services"],
        },
      ],
      checkpoint: { title: "Interface review", question: "Do the pages look right?" },
    },
    {
      stage: "deploy",
      goal: "Sandbox and test flight",
      layers: [
        {
          key: "container",
          kind: "deploy",
          title: "Container",
          agent: "icarus",
          description: "Dockerfile",
          deliverables: ["Dockerfile"],
          checks: ["Dockerfile exposes PORT"],
        },
        {
          key: "flight",
          kind: "test-flight",
          title: "Test flight",
          agent: "icarus",
          description: "Staff and customer bots",
          dependsOn: ["container"],
          deliverables: ["tests/flight.md"],
          checks: ["Flight plan covers staff and customer"],
        },
      ],
      checkpoint: { title: "Launch review", question: "Ready to join your Apps?" },
    },
  ],
};

const blueprint = blueprintSchema.parse(WEBSITE);

class MemoryAtlas implements AtlasPort {
  entries = new Map<string, AtlasEntry>([
    [blueprint.blueprintId, { blueprint, source: "starter", registeredAt: "2026-10-30T00:00:00.000Z", uses: 0 }],
  ]);
  matchScore = 0.9;
  async list() {
    return [...this.entries.values()];
  }
  async get(id: string) {
    return this.entries.get(id);
  }
  async register(value: Blueprint) {
    const entry: AtlasEntry = { blueprint: value, source: "user", registeredAt: new Date().toISOString(), uses: 0 };
    this.entries.set(value.blueprintId, entry);
    return entry;
  }
  async match(_seedId: string) {
    return {
      matches: [{ blueprintId: blueprint.blueprintId, score: this.matchScore, reason: "Websites with bookings" }],
      needsCrafting: this.matchScore < 0.6,
    };
  }
}

const offlineAtlas: AtlasPort = {
  list: async () => {
    throw new LayerNotConnected("atlas", "not connected");
  },
  get: async () => {
    throw new LayerNotConnected("atlas", "not connected");
  },
  register: async () => {
    throw new LayerNotConnected("atlas", "not connected");
  },
  match: async () => {
    throw new LayerNotConnected("atlas", "not connected");
  },
};

function fileFor(path: string): string {
  if (path.endsWith(".json")) return JSON.stringify({ services: ["Custom tattoos", "Flash"] });
  if (path.endsWith(".mjs") || path.endsWith(".js")) return "export const ready = true;\n";
  if (path.endsWith(".html")) return "<!doctype html><html><body><main>Custom tattoos</main></body></html>";
  return "Content for the layer.\n";
}

type Script = (request: ChatRequest) => string | Promise<string>;

class ScriptedModel implements ModelClient {
  calls: ChatRequest[] = [];
  /** Per layer key: answers to give instead of the default good one. */
  layerOverrides = new Map<string, Array<string | "default">>();
  delayMs = 0;
  constructor(private readonly extra: Partial<Record<string, Script>> = {}) {}

  async route() {
    return {
      provider: "nebius" as const,
      baseUrl: "https://api.tokenfactory.nebius.com/v1",
      models: { lead: "lead-model", worker: "worker-model", vision: "vision-model" },
      reason: "scripted",
      checkedAt: new Date().toISOString(),
    };
  }
  async models() {
    return [{ id: "lead-model" }, { id: "worker-model" }];
  }

  private defaultAnswer(request: ChatRequest): string {
    const prompt = request.messages.map((message) => message.content).join("\n");
    if (request.purpose === "imagistation.turn") {
      return `What makes your shop different, and who books with you most?\n<<<STATE>>>\n{"draft": {"title": "Ink and Iron", "kind": "website", "platforms": ["web"]}, "questions": ["What makes your shop different?"], "ready": false}`;
    }
    if (request.purpose === "seed.draft") {
      return JSON.stringify({
        title: "Ink and Iron",
        problem: "Clients cannot see the portfolio or book online",
        audience: "People in town looking for custom tattoos",
        successSignal: "Ten online booking requests a week",
        kind: "website",
        platforms: ["web"],
        features: ["Portfolio", "Booking requests"],
      });
    }
    if (request.purpose === "blueprint.parameters") return JSON.stringify({ businessName: "Ink and Iron" });
    if (request.purpose === "blueprint.craft") {
      // Answers to the Atlas crafting method's questions; the method builds the Blueprint.
      return JSON.stringify({
        name: "Crafted tattoo site",
        kind: "website",
        platforms: ["web"],
        purpose: "A website for a tattoo shop where people see the work and request a booking",
        records: ["artists", "booking requests"],
        actions: ["browse the portfolio", "request a booking"],
        roles: ["staff", "customer"],
      });
    }
    if (request.purpose.startsWith("layer:")) {
      const deliverables = /Deliverables: (.*)/.exec(prompt)?.[1]?.split("; ") ?? [];
      const checks = [...(/Checks your files must pass:\n([\s\S]*?)(?:\n\n|\n[A-Z]|$)/.exec(prompt)?.[1] ?? "").matchAll(/^\d+\. (.*)$/gm)].map(
        (match) => match[1] as string,
      );
      return [
        "<<<SUMMARY>>>",
        `Built ${request.purpose.slice(6)}.`,
        "<<<END SUMMARY>>>",
        ...deliverables.flatMap((path) => [`<<<FILE ${path}>>>`, fileFor(path), "<<<END FILE>>>"]),
        "<<<CHECKS>>>",
        JSON.stringify(checks.map((name) => ({ name, passed: true, detail: "met" }))),
        "<<<END CHECKS>>>",
      ].join("\n");
    }
    if (request.purpose === "checkpoint.summary") return "The sprint built what was planned.";
    if (request.purpose === "design.system") {
      return JSON.stringify({
        name: "Ink",
        principles: ["Bold and clear"],
        tokens: { colors: [{ name: "ink", value: "#111111", role: "background" }], typography: [{ name: "body", family: "Inter", size: "16px" }] },
        components: [{ name: "Gallery", category: "media", description: "Portfolio grid", source: "21st" }],
      });
    }
    if (request.purpose === "design.mockups") {
      return "<<<MOCKUP Direction A: Gallery first>>>\n<!doctype html><html><body>A</body></html>\n<<<END MOCKUP>>>\n<<<MOCKUP Direction B: Booking first>>>\n<!doctype html><html><body>B</body></html>\n<<<END MOCKUP>>>\n<<<NOTES>>>A shows work, B drives bookings.<<<END NOTES>>>";
    }
    if (request.purpose === "design.orchestration") {
      return JSON.stringify({ proposals: [{ title: "Weekly social posts", what: "Post new work", connects: ["app:ryozan"], cadence: "weekly" }] });
    }
    if (request.purpose === "session.turn" || request.purpose === "session.open") return "Here is where the build stands.";
    throw new Error(`no script for ${request.purpose}`);
  }

  async chat(request: ChatRequest): Promise<ChatResult> {
    this.calls.push(request);
    if (this.delayMs > 0) {
      await new Promise<void>((resolve, reject) => {
        const timer = setTimeout(resolve, this.delayMs);
        request.signal?.addEventListener("abort", () => {
          clearTimeout(timer);
          reject(new OrchestraError("cancelled", "The model call was cancelled"));
        });
      });
    }
    if (request.signal?.aborted) throw new OrchestraError("cancelled", "The model call was cancelled");
    let text: string;
    const key = request.purpose.startsWith("layer:") ? request.purpose.slice(6) : "";
    const queue = key ? this.layerOverrides.get(key) : undefined;
    const override = queue?.shift();
    const script = this.extra[request.purpose];
    if (override !== undefined && override !== "default") text = override;
    else if (script) text = await script(request);
    else text = this.defaultAnswer(request);
    for (const piece of text.match(/[\s\S]{1,40}/g) ?? []) request.onDelta?.(piece);
    return { text, model: request.tier === "worker" ? "worker-model" : "lead-model", usage: { model: "scripted" } };
  }
}

function flight(buildId: string, status: TestFlight["status"]): TestFlight {
  return {
    id: makeId("testFlight", "abc123"),
    buildId,
    runId: makeId("sandboxRun", "run123"),
    status,
    personas: [{ id: "staff", role: "staff", name: "Staff", brief: "Runs the shop", goals: ["See bookings"] }],
    steps: [{ id: "s1", personaId: "staff", action: "Open bookings", expectation: "List shows", status: status === "passed" ? "passed" : "running" }],
    layers: [],
    summary: status === "passed" ? "Staff and customer flows passed" : undefined,
    startedAt: new Date().toISOString(),
  };
}

const sandboxRun = (buildId: string, status: "starting" | "running") => ({
  id: makeId("sandboxRun", "run123"),
  buildId,
  status,
  ports: { app: 17410, cdp: 17411, viewer: 17412 },
  composeProject: `4ge-${buildId}`,
  startedAt: new Date().toISOString(),
});

// Like the real sandbox: start answers "starting" and the run is up a moment later.
const sandbox: SandboxPort = {
  start: async (buildId) => sandboxRun(buildId, "starting"),
  getRun: async () => sandboxRun("bld_x", "running"),
  stop: async () => ({ ...sandboxRun("bld_x", "running"), status: "stopped" as const }),
  startTestFlight: async (request) => flight(request.buildId, "passed"),
  getTestFlight: async () => flight("bld_x", "passed"),
};

const roots: string[] = [];
afterEach(() => {
  for (const dir of roots.splice(0)) rmSync(dir, { recursive: true, force: true });
});

function setup(options: { atlas?: AtlasPort; model?: ScriptedModel; sandbox?: SandboxPort } = {}) {
  const root = mkdtempSync(join(tmpdir(), "4ge-orchestra-"));
  roots.push(root);
  const paths = ensureDataDirs(dataPaths(join(root, "data")));
  const events: SessionEvent[] = [];
  let sequence = 0;
  const sink = {
    publish(draft: EventDraft) {
      sequence += 1;
      const event = { ...draft, id: String(sequence), at: new Date().toISOString() } as SessionEvent;
      events.push(event);
      return event;
    },
  };
  const model = options.model ?? new ScriptedModel();
  const orchestra = new Orchestra({
    paths,
    env: {},
    events: sink,
    atlas: options.atlas ?? new MemoryAtlas(),
    ...(options.sandbox ? { sandbox: options.sandbox } : {}),
    models: model,
    startersDir: join(root, "starters"),
    ainboxDir: join(root, "ainbox"),
    maxAttempts: 2,
    testFlightTimeoutMs: 5_000,
  });
  return { orchestra, paths, events, model, root };
}

async function seedFromImagistation(orchestra: Orchestra) {
  const opened = await orchestra.imagistation.create("a website for my tattoo shop");
  await orchestra.imagistation.send(opened.session.id, "We do custom blackwork and most clients are locals");
  const seed = await orchestra.imagistation.writeSeed(opened.session.id);
  return { session: opened.session, seed };
}

describe("Imagistation with Prometheus", () => {
  it("asks follow-up questions, keeps the person's words, and picks a Blueprint from the Atlas", async () => {
    const { orchestra, events } = setup();
    const opened = await orchestra.imagistation.create("a website for my tattoo shop");
    expect(opened.messages.map((message) => message.role)).toEqual(["assistant", "user", "assistant"]);
    const reply = opened.messages.at(-1);
    expect(reply?.content).toBe("What makes your shop different, and who books with you most?");
    expect(reply?.content).not.toContain("<<<STATE>>>");
    const deltas = events.filter((event) => event.type === "message.delta").map((event) => (event.type === "message.delta" ? event.data.delta : "")).join("");
    expect(deltas).not.toContain("STATE");

    await orchestra.imagistation.send(opened.session.id, "We do custom blackwork and most clients are locals");
    const seed = await orchestra.imagistation.writeSeed(opened.session.id);
    expect(seed.idea).toBe("a website for my tattoo shop");
    expect(seed.answers[0]?.answer).toBe("We do custom blackwork and most clients are locals");
    expect(seed.blueprintId).toBe("blueprint:test-website");
    expect(seed.parameters).toEqual({ businessName: "Ink and Iron" });
    expect(seed.status).toBe("ready");
    expect(orchestra.sessions.messages(opened.session.id).at(-1)?.content).toMatch(/Build it now, or later\?/);
  });

  it("refuses to write a Seed before the person says anything", async () => {
    const { orchestra } = setup();
    const opened = await orchestra.imagistation.create("   ");
    await expect(orchestra.imagistation.writeSeed(opened.session.id)).rejects.toMatchObject({ code: "bad_request" });
  });

  it("crafts a Blueprint and keeps it with the Seed when the Atlas is not connected", async () => {
    const { orchestra, model } = setup({ atlas: offlineAtlas });
    const { seed } = await seedFromImagistation(orchestra);
    expect(model.calls.some((call) => call.purpose === "blueprint.craft")).toBe(true);
    expect(seed.blueprintId).toMatch(/^blueprint:crafted-tattoo-site/);
    expect(orchestra.seeds.heldBlueprint(seed.id)?.origin).toBe("crafted");
  });

  it("crafts and registers a Blueprint when nothing in the Atlas fits", async () => {
    const atlas = new MemoryAtlas();
    atlas.matchScore = 0.2;
    const { orchestra } = setup({ atlas });
    const { seed } = await seedFromImagistation(orchestra);
    expect(atlas.entries.get(seed.blueprintId as string)?.source).toBe("user");
  });

  it("says plainly when no model is connected", async () => {
    const root = mkdtempSync(join(tmpdir(), "4ge-nomodel-"));
    roots.push(root);
    const orchestra = new Orchestra({
      paths: ensureDataDirs(dataPaths(join(root, "data"))),
      env: {},
      events: { publish: () => undefined },
      atlas: new MemoryAtlas(),
      ainboxDir: join(root, "ainbox"),
    });
    const opened = await orchestra.imagistation.create("a website for my tattoo shop");
    const last = opened.messages.at(-1);
    expect(last?.role).toBe("system");
    expect(last?.content).toMatch(/No model is connected/);
    await expect(orchestra.imagistation.send(opened.session.id, "hello")).rejects.toMatchObject({ code: "model_unavailable" });
  });
});

describe("Seed decisions", () => {
  it("queues a later Seed in AInbox with the reminder", async () => {
    const { orchestra, root } = setup();
    const { seed } = await seedFromImagistation(orchestra);
    const decided = await orchestra.decideSeed(seed.id, "later", "Block Saturday morning");
    expect(decided.build).toBeUndefined();
    expect(decided.seed.status).toBe("queued");
    expect(decided.seed.decision).toBe("later");
    const file = join(root, "ainbox", "pending", decided.seed.ainboxItem as string);
    expect(JSON.parse(readFileSync(file, "utf8")).Ask).toContain("Block Saturday morning");
  });
});

describe("Smith's build", () => {
  it("runs every stage in parallel child sessions, gated by receipts, through the test flight", async () => {
    const { orchestra, paths, events, model } = setup({ sandbox });
    const { seed } = await seedFromImagistation(orchestra);
    const { build } = await orchestra.decideSeed(seed.id, "now");
    expect(build?.status).toBe("running");
    const id = build?.id as string;

    await orchestra.builds.whenIdle(id);
    let current = orchestra.builds.get(id);
    expect(current.status).toBe("awaiting-checkpoint");
    const architecture = current.layers.filter((layer) => layer.stage === "architecture");
    expect(architecture.map((layer) => layer.status)).toEqual(["passed", "passed"]);
    expect(current.layers.filter((layer) => layer.stage !== "architecture").every((layer) => layer.status === "pending")).toBe(true);

    // Parameters were filled into the plan the agents saw.
    expect(model.calls.find((call) => call.purpose === "layer:content-model")?.messages[1]?.content).toContain("Services and artists for Ink and Iron");

    // Real files and verified receipts.
    const app = paths.build(id).app;
    expect(readFileSync(join(app, "src", "content.json"), "utf8")).toContain("Custom tattoos");
    const { receipts, gate } = orchestra.builds.receipts(id);
    expect(receipts.filter((receipt) => receipt.stage === "architecture")).toHaveLength(2);
    expect(gate.find((decision) => decision.stage === "orchestration")?.admitted).toBe(true);
    expect(gate.find((decision) => decision.stage === "interface")?.admitted).toBe(false);

    // Every step is a .bamf envelope.
    expect(readdirSync(join(paths.build(id).root, "bamf", "steps")).length).toBe(10);

    // The session tree: layer sessions nest under Smith's root.
    const tree = orchestra.sessions.tree(current.rootSessionId as string);
    const layerChildren = tree.children.filter((child) => child.kind === "layer");
    expect(layerChildren.map((child) => child.agent).sort()).toEqual(["hephaestus", "hephaestus"]);
    expect(tree.children.some((child) => child.kind === "chat" && child.agent === "smith")).toBe(true);
    expect(layerChildren.every((child) => child.parentId === tree.id && child.status === "done")).toBe(true);

    // The design conversation ran beside the layers.
    expect(existsSync(join(paths.build(id).design, "smith", "mockups", "direction-a-gallery-first.html"))).toBe(true);
    expect(events.some((event) => event.type === "preview.updated" && event.data.preview.ref.startsWith("design/smith/mockups/"))).toBe(true);
    expect(existsSync(join(paths.build(id).root, "orchestration.json"))).toBe(true);

    // Checkpoints pause the build until the person answers.
    for (const stage of ["architecture", "orchestration", "interface", "deploy"]) {
      current = orchestra.builds.get(id);
      expect(current.status).toBe("awaiting-checkpoint");
      const open = orchestra.builds.checkpoints(id).find((checkpoint) => checkpoint.id === current.openCheckpointId);
      expect(open?.stage).toBe(stage);
      await orchestra.builds.decide(id, open?.id as string, { decision: "approve" });
      await orchestra.builds.whenIdle(id);
    }
    current = orchestra.builds.get(id);
    expect(current.status).toBe("passed");
    expect(current.layers.every((layer) => layer.status === "passed")).toBe(true);
    expect(existsSync(join(app, ".4ge", "test-flights", `${makeId("testFlight", "abc123")}.json`))).toBe(true);
    expect(orchestra.seeds.get(seed.id).status).toBe("built");
    expect(events.filter((event) => event.type === "checkpoint.opened")).toHaveLength(4);
    expect(events.every((event) => event.type === "log" || event.buildId === undefined || event.buildId === id)).toBe(true);
  });

  it("retries a layer, then fails it, gates the next stage, and rebuilds on revise", async () => {
    const model = new ScriptedModel();
    model.layerOverrides.set("api", ["no files here", "still nothing"]);
    const { orchestra } = setup({ model });
    const { seed } = await seedFromImagistation(orchestra);
    const { build } = await orchestra.decideSeed(seed.id, "now");
    const id = build?.id as string;
    await orchestra.builds.whenIdle(id);
    let current = orchestra.builds.get(id);
    const api = current.layers.find((layer) => layer.key === "api");
    expect(api?.status).toBe("failed");
    expect(model.calls.filter((call) => call.purpose === "layer:api")).toHaveLength(2);
    expect(api?.error).toMatch(/No files were written/);

    const checkpointId = current.openCheckpointId as string;
    await expect(orchestra.builds.decide(id, checkpointId, { decision: "approve" })).rejects.toMatchObject({ code: "gated" });
    await expect(orchestra.builds.decide(id, checkpointId, { decision: "revise" })).rejects.toMatchObject({ code: "bad_request" });

    await orchestra.builds.decide(id, checkpointId, { decision: "revise", note: "Add a health route" });
    await orchestra.builds.whenIdle(id);
    current = orchestra.builds.get(id);
    expect(current.layers.find((layer) => layer.key === "api")?.status).toBe("passed");
    const revised = model.calls.filter((call) => call.purpose === "layer:api").at(-1);
    expect(revised?.messages[1]?.content).toContain("Add a health route");
    expect(current.status).toBe("awaiting-checkpoint");
  });

  it("blocks a layer whose dependency failed", async () => {
    const model = new ScriptedModel();
    model.layerOverrides.set("content-model", ["nothing", "nothing"]);
    const { orchestra } = setup({ model });
    const { seed } = await seedFromImagistation(orchestra);
    const { build } = await orchestra.decideSeed(seed.id, "now");
    await orchestra.builds.whenIdle(build?.id as string);
    const layers = orchestra.builds.get(build?.id as string).layers;
    expect(layers.find((layer) => layer.key === "api")?.status).toBe("blocked");
  });

  it("cancels a running build and every layer session", async () => {
    const model = new ScriptedModel();
    const { orchestra } = setup({ model });
    const { seed } = await seedFromImagistation(orchestra);
    model.delayMs = 400;
    const { build } = await orchestra.decideSeed(seed.id, "now");
    const id = build?.id as string;
    await new Promise((resolve) => setTimeout(resolve, 100));
    const cancelled = orchestra.builds.cancel(id);
    expect(cancelled.status).toBe("cancelled");
    await orchestra.builds.whenIdle(id);
    const after = orchestra.builds.get(id);
    expect(after.status).toBe("cancelled");
    expect(after.layers.every((layer) => layer.status === "cancelled")).toBe(true);
    const tree = orchestra.sessions.tree(after.rootSessionId as string);
    expect(tree.status).toBe("cancelled");
    expect(tree.children.every((child) => child.status === "cancelled")).toBe(true);
  });

  it("fails the test flight plainly when the sandbox is not connected", async () => {
    const { orchestra } = setup();
    const { seed } = await seedFromImagistation(orchestra);
    const { build } = await orchestra.decideSeed(seed.id, "now");
    const id = build?.id as string;
    for (let index = 0; index < 3; index += 1) {
      await orchestra.builds.whenIdle(id);
      const current = orchestra.builds.get(id);
      await orchestra.builds.decide(id, current.openCheckpointId as string, { decision: "approve" });
    }
    await orchestra.builds.whenIdle(id);
    const flightLayer = orchestra.builds.get(id).layers.find((layer) => layer.key === "flight");
    expect(flightLayer?.status).toBe("failed");
    expect(flightLayer?.testFlight).toBe("failed");
    expect(flightLayer?.error).toMatch(/sandbox is not connected/);
  });

  it("lets Smith rebuild a layer from the conversation", async () => {
    const model = new ScriptedModel({
      "session.turn": () => 'On it.\n<<<ACTIONS>>>\n[{"type":"rebuild-layer","layer":"api","note":"use port 8080"}]',
    });
    const { orchestra } = setup({ model });
    const { seed } = await seedFromImagistation(orchestra);
    const { build } = await orchestra.decideSeed(seed.id, "now");
    const id = build?.id as string;
    await orchestra.builds.whenIdle(id);
    const root = orchestra.builds.get(id).rootSessionId as string;
    const { reply } = await orchestra.conversations.send(root, "Please make the API use port 8080");
    expect(reply.content).toBe("On it.");
    await orchestra.builds.whenIdle(id);
    expect(model.calls.filter((call) => call.purpose === "layer:api").at(-1)?.messages[1]?.content).toContain("use port 8080");
    expect(orchestra.sessions.messages(root).some((message) => message.role === "system" && message.content.includes("Rebuilding API"))).toBe(true);
    expect(orchestra.builds.get(id).status).toBe("awaiting-checkpoint");
  });

  it("reloads sessions and builds from disk after a restart", async () => {
    const { orchestra, paths } = setup();
    const { seed, session } = await seedFromImagistation(orchestra);
    const { build } = await orchestra.decideSeed(seed.id, "now");
    await orchestra.builds.whenIdle(build?.id as string);
    const again = new Orchestra({
      paths,
      env: {},
      events: { publish: () => undefined },
      atlas: new MemoryAtlas(),
      models: new ScriptedModel(),
    });
    expect(again.sessions.messages(session.id).length).toBeGreaterThan(3);
    expect(again.builds.get(build?.id as string).status).toBe("awaiting-checkpoint");
    expect(again.builds.checkpoints(build?.id as string)).toHaveLength(1);
  });
});
