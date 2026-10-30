import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { craftBlueprint } from "@4ge/atlas";
import {
  ROUTES,
  atlasEntrySchema,
  blueprintMatchResponseSchema,
  buildPath,
  craftResultSchema,
  makeId,
} from "@4ge/contracts";
import { afterAll, describe, expect, it } from "vitest";
import { createApp } from "../app.js";

const root = mkdtempSync(join(tmpdir(), "4ge-atlas-routes-"));
afterAll(() => rmSync(root, { recursive: true, force: true }));
let counter = 0;

/** En dash and em dash, built from code points so this file contains neither. */
const DASHES = new RegExp(`[${String.fromCodePoint(0x2013)}${String.fromCodePoint(0x2014)}]`);

async function atlasApp() {
  counter += 1;
  const dataRoot = join(root, `data-${counter}`);
  const { app, ctx } = await createApp({ dataRoot, studioDist: join(root, "no-studio"), heartbeatMs: 50 });
  const call = async (method: string, path: string, body?: unknown) => {
    const response = await app.request(`/api${path}`, {
      method,
      headers: { "content-type": "application/json" },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });
    return { status: response.status, body: (await response.json()) as any };
  };
  return { call, ctx, dataRoot };
}

const ANSWERS = {
  name: "Aftercare tracker",
  kind: "mobile-app",
  platforms: ["ios", "android"],
  purpose: "Helps tattoo clients follow aftercare day by day and lets the artist check healing photos.",
  records: "clients, tattoos, healing photos",
  actions: "log a care step, upload a healing photo",
  roles: "client, artist",
};

describe("atlas routes", () => {
  it("marks the atlas layer connected and lists the starters", async () => {
    const { call } = await atlasApp();
    expect((await call("GET", "/health")).body.layers.atlas).toBe(true);
    const list = await call("GET", ROUTES["atlas.blueprints.list"].path);
    expect(list.status).toBe(200);
    expect(list.body.entries.map((entry: { blueprint: { kind: string } }) => entry.blueprint.kind)).toEqual([
      "website",
      "web-app",
      "mobile-app",
      "game",
      "crm",
      "erp",
      "desktop-app",
    ]);
  });

  it("gets one Blueprint by encoded id or bare slug, and 404s in plain words", async () => {
    const { call } = await atlasApp();
    const byId = await call("GET", buildPath(ROUTES["atlas.blueprints.get"].path, { id: "blueprint:small-business-site" }));
    expect(byId.status).toBe(200);
    expect(atlasEntrySchema.parse(byId.body.entry).blueprint.starter).toBe("website");
    expect((await call("GET", "/atlas/blueprints/web-game")).status).toBe(200);
    const missing = await call("GET", "/atlas/blueprints/blueprint%3Anothing-here");
    expect(missing.status).toBe(404);
    expect(missing.body.error.message).toContain("not in your Atlas");
    expect(missing.body.error.message).not.toMatch(DASHES);
  });

  it("matches a bare idea and says when to craft", async () => {
    const { call } = await atlasApp();
    const tattoo = await call("POST", "/atlas/match", { idea: "a website for my tattoo shop" });
    expect(tattoo.status).toBe(200);
    const parsed = blueprintMatchResponseSchema.parse(tattoo.body);
    expect(parsed.matches[0]?.blueprintId).toBe("blueprint:small-business-site");
    expect(parsed.needsCrafting).toBe(false);
    expect(parsed.inferred).toEqual({ kind: "website", platforms: ["web"] });
    const odd = await call("POST", "/atlas/match", { idea: "turn my podcast episodes into a newsletter automatically" });
    expect(odd.body.needsCrafting).toBe(true);
  });

  it("matches a Seed Scroll by id and refuses an empty or missing request", async () => {
    const { call, ctx } = await atlasApp();
    const seedId = makeId("seed", "gamejam");
    mkdirSync(ctx.paths.seeds, { recursive: true });
    writeFileSync(
      join(ctx.paths.seeds, `${seedId}.json`),
      JSON.stringify({
        id: seedId,
        title: "Neon catcher",
        idea: "A quick arcade game I can share with friends",
        problem: "",
        audience: "",
        successSignal: "",
        kind: "game",
        platforms: ["web"],
        features: ["high scores"],
        constraints: [],
        references: [],
        answers: [],
        parameters: {},
        status: "draft",
        decision: "pending",
        createdAt: ctx.startedAt,
        updatedAt: ctx.startedAt,
      }),
    );
    const bySeed = await call("POST", "/atlas/match", { seedId });
    expect(bySeed.status).toBe(200);
    expect(bySeed.body.matches[0].blueprintId).toBe("blueprint:web-game");

    const missingSeed = await call("POST", "/atlas/match", { seedId: makeId("seed", "nope") });
    expect(missingSeed.status).toBe(404);
    expect(missingSeed.body.error.hint).toBeTruthy();
    expect((await call("POST", "/atlas/match", { idea: "   " })).status).toBe(400);
    expect((await call("POST", "/atlas/match", {})).status).toBe(400);
  });

  it("registers a crafted Blueprint, refuses a duplicate and a starter id", async () => {
    const { call } = await atlasApp();
    const blueprint = craftBlueprint(ANSWERS).blueprint;
    const created = await call("POST", "/atlas/blueprints", blueprint);
    expect(created.status).toBe(201);
    expect(created.body.entry.source).toBe("user");
    const again = await call("POST", "/atlas/blueprints", blueprint);
    expect(again.status).toBe(409);
    expect(again.body.error.hint).toContain("Raise the version");
    const starterId = await call("POST", "/atlas/blueprints", { ...blueprint, blueprintId: "blueprint:web-game" });
    expect(starterId.status).toBe(409);
    const invalid = await call("POST", "/atlas/blueprints", { name: "no stages" });
    expect(invalid.status).toBe(400);
    expect((await call("GET", "/atlas/blueprints")).body.entries).toHaveLength(8);
  });

  it("lists the starters with the Blueprints that use them", async () => {
    const { call } = await atlasApp();
    const response = await call("GET", "/atlas/starters");
    expect(response.status).toBe(200);
    const records = response.body.starters.find((starter: { name: string }) => starter.name === "records-app");
    expect(records.blueprintIds).toEqual(["blueprint:crm-pipeline", "blueprint:erp-operations"]);
    expect(response.body.starters).toHaveLength(6);
  });

  it("runs the crafting method: questions, an incomplete turn, then a registered Blueprint", async () => {
    const { call } = await atlasApp();
    const questions = await call("GET", `/atlas/craft/questions?idea=${encodeURIComponent("a phone app for aftercare reminders")}`);
    expect(questions.status).toBe(200);
    expect(questions.body.questions.find((question: { field: string }) => question.field === "kind").suggestion).toBe("mobile-app");

    const first = craftResultSchema.parse((await call("POST", "/atlas/craft", { idea: "a phone app for aftercare reminders", answers: {} })).body);
    expect(first.complete).toBe(false);
    expect(first.next?.field).toBe("name");

    const done = await call("POST", "/atlas/craft", { answers: ANSWERS, register: true });
    expect(done.status).toBe(200);
    const result = craftResultSchema.parse(done.body);
    expect(result.complete).toBe(true);
    expect(result.entry?.blueprint.blueprintId).toBe("blueprint:aftercare-tracker");
    expect(result.bamf.find((command) => command.action === "blueprint.register")).toBeTruthy();
    expect(result.steps.length).toBeGreaterThan(10);

    const second = craftResultSchema.parse((await call("POST", "/atlas/craft", { answers: ANSWERS })).body);
    expect(second.blueprint?.blueprintId).toBe("blueprint:aftercare-tracker-2");
    expect(second.entry).toBeUndefined();
  });
});
