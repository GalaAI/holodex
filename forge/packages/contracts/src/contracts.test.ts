import { describe, expect, it } from "vitest";
import {
  AGENT_IDS,
  LIBRARY_GROUPS,
  LIBRARY_ORIGINS,
  PORTS,
  RESERVED_PORTS,
  ROUTES,
  SERVER_LAYERS,
  SESSION_EVENT_TYPES,
  STAGE_ORDER,
  STAGE_OWNERS,
  TWEAK_KINDS,
  assertForgePort,
  blueprintSchema,
  buildPath,
  buildSessionTree,
  createForgeClient,
  ForgeApiError,
  groupLibrary,
  libraryItemSchema,
  listRoutes,
  makeId,
  receiptSchema,
  seedReadiness,
  sessionEventSchema,
  tweakOpSchema,
  type BlueprintInput,
  type LibraryItem,
  type Session,
} from "./index.js";

const now = "2026-10-30T12:00:00.000Z";

function sampleBlueprint(overrides: Partial<BlueprintInput> = {}): BlueprintInput {
  return {
    blueprintId: "blueprint:website-starter",
    name: "Website",
    version: "1.0.0",
    state: "promoted",
    origin: "starter",
    kind: "website",
    platforms: ["web"],
    description: "A marketing website with bookings.",
    stages: [
      {
        stage: "architecture",
        goal: "Data and API for the site",
        layers: [
          { key: "content-model", kind: "data", title: "Content model", agent: "hephaestus", description: "Pages and services", checks: ["schema validates"] },
          { key: "booking-api", kind: "backend", title: "Booking API", agent: "hephaestus", description: "Bookings", dependsOn: ["content-model"], checks: ["api responds"] },
        ],
        checkpoint: { title: "Architecture", question: "Does the data model cover your services?" },
      },
      {
        stage: "interface",
        goal: "Landing page",
        layers: [
          { key: "landing", kind: "interface", title: "Landing page", agent: "smith", description: "Hero and booking", dependsOn: ["booking-api"], checks: ["renders"] },
        ],
        checkpoint: { title: "Interface", question: "Does the landing page feel right?" },
      },
    ],
    ...overrides,
  };
}

describe("ports", () => {
  it("never hands out an Ark port", () => {
    for (const port of Object.values(PORTS)) expect(RESERVED_PORTS).not.toContain(port);
    for (const port of RESERVED_PORTS) expect(() => assertForgePort(port)).toThrow(/belongs to Ark/);
    expect(assertForgePort(PORTS.server)).toBe(17400);
    expect(() => assertForgePort(70000)).toThrow(/not a valid/);
  });
});

describe("agents and stages", () => {
  it("has the six SPEC agents and an owner for every stage", () => {
    expect(AGENT_IDS).toEqual(["prometheus", "smith", "hephaestus", "athena", "metis", "icarus"]);
    expect(STAGE_ORDER).toEqual(["architecture", "orchestration", "interface", "deploy"]);
    for (const stage of STAGE_ORDER) expect(AGENT_IDS).toContain(STAGE_OWNERS[stage].owner);
  });
});

describe("blueprintSchema", () => {
  it("accepts a well formed Blueprint", () => {
    const parsed = blueprintSchema.parse(sampleBlueprint());
    expect(parsed.stages[0]?.layers[1]?.dependsOn).toEqual(["content-model"]);
    expect(parsed.parameters).toEqual([]);
  });

  it("rejects duplicate layer keys, unknown dependencies and out of order stages", () => {
    const duplicate = sampleBlueprint();
    duplicate.stages[1]!.layers[0]!.key = "content-model";
    expect(blueprintSchema.safeParse(duplicate).success).toBe(false);

    const unknown = sampleBlueprint();
    unknown.stages[1]!.layers[0]!.dependsOn = ["nope"];
    const unknownResult = blueprintSchema.safeParse(unknown);
    expect(unknownResult.success).toBe(false);
    expect(JSON.stringify(unknownResult.error?.issues)).toContain("unknown layer");

    const reversed = sampleBlueprint();
    reversed.stages = [...reversed.stages].reverse();
    expect(JSON.stringify(blueprintSchema.safeParse(reversed).error?.issues)).toContain("order");
  });

  it("requires the blueprint: id prefix", () => {
    expect(blueprintSchema.safeParse(sampleBlueprint({ blueprintId: "website" })).success).toBe(false);
  });
});

describe("seeds", () => {
  it("reports what a Seed still needs before promotion", () => {
    expect(seedReadiness({ idea: "A site for my tattoo shop", problem: "", audience: "Walk-ins", successSignal: "" }))
      .toEqual({ ready: false, missing: ["problem", "successSignal"] });
    expect(seedReadiness({ idea: "x", problem: "y", audience: "z", successSignal: "w" }).ready).toBe(true);
  });
});

describe("receipts", () => {
  it("refuses absolute and parent-hopping artifact paths", () => {
    const base = {
      id: makeId("receipt", "abc"),
      buildId: makeId("build", "abc"),
      stage: "architecture",
      agentId: "hephaestus",
      checks: [{ name: "typecheck", passed: true }],
      recordedAt: now,
    };
    expect(receiptSchema.safeParse({ ...base, artifacts: ["src/index.ts"] }).success).toBe(true);
    expect(receiptSchema.safeParse({ ...base, artifacts: ["C:\\secret.txt"] }).success).toBe(false);
    expect(receiptSchema.safeParse({ ...base, artifacts: ["/etc/passwd"] }).success).toBe(false);
    expect(receiptSchema.safeParse({ ...base, artifacts: ["../other/app.ts"] }).success).toBe(false);
  });
});

describe("tweaks", () => {
  it("covers every tweak kind the SPEC names", () => {
    expect(TWEAK_KINDS).toEqual(["color", "placement", "layout", "text", "animation", "motion", "transition", "asset", "component"]);
    const target = { selector: "[data-4ge-id=hero]" };
    const buildId = makeId("build", "x1");
    const samples = [
      { kind: "color", property: "background", value: "#112233" },
      { kind: "placement", align: "center" },
      { kind: "layout", display: "grid", columns: 3 },
      { kind: "text", text: "Book a session" },
      { kind: "animation", name: "fade-in", durationMs: 300 },
      { kind: "motion", preset: "fade-up" },
      { kind: "transition", property: "opacity", durationMs: 200 },
      { kind: "asset", assetRef: "assets/hero.jpg", alt: "Studio" },
      { kind: "component", action: "remove" },
    ];
    for (const sample of samples) {
      const parsed = tweakOpSchema.parse({ buildId, target, ...sample });
      expect(parsed.kind).toBe(sample.kind);
      expect(parsed.target.page).toBe("/");
    }
  });
});

describe("library", () => {
  const item = (origin: LibraryItem["origin"], title: string): LibraryItem =>
    libraryItemSchema.parse({
      id: `${origin}:${title.toLowerCase()}`,
      origin,
      title,
      open: { agent: "smith", kind: "library", task: `Talk about ${title}`, subject: { type: origin === "project" ? "project" : origin, id: title } },
    });

  it("groups by origin in the ruled order with the ruled labels", () => {
    const grouped = groupLibrary([item("adapter", "Railway"), item("project", "Ryozan"), item("mod", "Dev Mod"), item("project", "Ink Site")]);
    expect(grouped.groups.map((group) => group.origin)).toEqual([...LIBRARY_ORIGINS]);
    expect(grouped.groups.map((group) => group.label)).toEqual(["My projects", "Repos", "Mods", "Augments", "Adapters"]);
    expect(grouped.groups[0]?.items.map((entry) => entry.title)).toEqual(["Ink Site", "Ryozan"]);
    expect(grouped.groups[1]?.items).toEqual([]);
    expect(grouped.groups.find((group) => group.origin === "mod")?.canCreate).toBe(true);
    expect(grouped.total).toBe(4);
    expect(LIBRARY_GROUPS.repo.label).toBe("Repos");
  });

  it("rejects an item without a known origin", () => {
    expect(libraryItemSchema.safeParse({ id: "x:y", origin: "plugin", title: "x", open: {} }).success).toBe(false);
  });
});

describe("sessions and events", () => {
  const session = (id: string, parentId: string | null, createdAt: string): Session => ({
    id: makeId("session", id),
    parentId: parentId ? makeId("session", parentId) : null,
    rootId: makeId("session", "root"),
    agent: parentId ? "hephaestus" : "smith",
    kind: parentId ? "layer" : "build",
    task: "work",
    status: "running",
    createdAt,
    updatedAt: createdAt,
  });

  it("nests sessions into a tree, ordered by creation", () => {
    const tree = buildSessionTree([
      session("b", "root", "2026-10-30T12:00:02.000Z"),
      session("root", null, "2026-10-30T12:00:00.000Z"),
      session("a", "root", "2026-10-30T12:00:01.000Z"),
      session("c", "a", "2026-10-30T12:00:03.000Z"),
    ]);
    expect(tree).toHaveLength(1);
    expect(tree[0]?.children.map((child) => child.id)).toEqual([makeId("session", "a"), makeId("session", "b")]);
    expect(tree[0]?.children[0]?.children[0]?.id).toBe(makeId("session", "c"));
  });

  it("validates every event type and lists them all", () => {
    expect(SESSION_EVENT_TYPES).toContain("checkpoint.opened");
    expect(SESSION_EVENT_TYPES).toContain("testflight.updated");
    expect(new Set(SESSION_EVENT_TYPES).size).toBe(SESSION_EVENT_TYPES.length);
    const heartbeat = sessionEventSchema.parse({ id: "1", at: now, type: "heartbeat", data: {} });
    expect(heartbeat.type).toBe("heartbeat");
    expect(sessionEventSchema.safeParse({ id: "1", at: now, type: "nope", data: {} }).success).toBe(false);
  });
});

describe("route table", () => {
  it("has unique method and path pairs, valid layers and stream schemas", () => {
    const keys = listRoutes().map((route) => `${route.method} ${route.path}`);
    expect(new Set(keys).size).toBe(keys.length);
    for (const route of listRoutes()) {
      expect(SERVER_LAYERS).toContain(route.layer);
      expect(route.path.startsWith("/")).toBe(true);
      if ("stream" in route && route.stream) expect(route.method).toBe("GET");
    }
  });

  it("covers every surface the foundation was asked for", () => {
    const paths = new Set(listRoutes().map((route) => route.path.split("/")[1]));
    for (const surface of ["health", "models", "imagistation", "seeds", "atlas", "builds", "sessions", "design", "mods", "adapters", "augments", "library", "sandbox"]) {
      expect(paths, surface).toContain(surface);
    }
    expect(ROUTES["library.list"].method).toBe("GET");
    expect(ROUTES["sandbox.testFlight.start"].path).toBe("/sandbox/test-flight");
  });

  it("fills and encodes path params", () => {
    expect(buildPath("/atlas/blueprints/:id", { id: "blueprint:website" })).toBe("/atlas/blueprints/blueprint%3Awebsite");
    expect(() => buildPath("/builds/:id/checkpoints/:checkpointId", { id: "bld_1" })).toThrow(/checkpointId/);
  });
});

describe("client", () => {
  const respond = (status: number, body: unknown) =>
    (async () => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } })) as typeof fetch;

  it("validates a good response", async () => {
    const client = createForgeClient({ baseUrl: "http://127.0.0.1:17400", fetch: respond(200, { mods: [] }) });
    await expect(client.call("mods.list")).resolves.toEqual({ mods: [] });
  });

  it("turns the error shape into ForgeApiError with the server's words", async () => {
    const client = createForgeClient({
      fetch: respond(501, { error: { code: "not_implemented", message: "The library layer is not connected yet", hint: "Later" } }),
    });
    const failure = await client.call("library.list").catch((error: unknown) => error);
    expect(failure).toBeInstanceOf(ForgeApiError);
    expect(failure).toMatchObject({ code: "not_implemented", status: 501, hint: "Later" });
  });

  it("reports contract drift and a dead server plainly", async () => {
    const drift = createForgeClient({ fetch: respond(200, { mods: "nope" }) });
    await expect(drift.call("mods.list")).rejects.toMatchObject({ code: "invalid_response" });
    const dead = createForgeClient({
      fetch: (async () => {
        throw new TypeError("fetch failed");
      }) as typeof fetch,
    });
    await expect(dead.call("health")).rejects.toMatchObject({ code: "network", status: 0 });
  });

  it("builds URLs with the API prefix and query", () => {
    const client = createForgeClient({ baseUrl: "http://127.0.0.1:17400/" });
    expect(client.url("sessions.list", {}, { buildId: "bld_1" })).toBe("http://127.0.0.1:17400/api/sessions?buildId=bld_1");
    expect(client.streamUrl("sessions.stream", { id: "ses_1" })).toBe("http://127.0.0.1:17400/api/sessions/ses_1/stream");
  });
});
