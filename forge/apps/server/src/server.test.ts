import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { PORTS, createForgeClient, healthSchema, makeId, type SessionEvent } from "@4ge/contracts";
import { afterAll, describe, expect, it } from "vitest";
import { createApp } from "./app.js";
import { forSession } from "./events.js";
import { implement, implementStream } from "./implement.js";
import { PortTakenError, startServer } from "./serve.js";

const root = mkdtempSync(join(tmpdir(), "4ge-server-"));
const emptyDist = join(root, "no-studio");
afterAll(() => rmSync(root, { recursive: true, force: true }));

/** En dash and em dash, built from code points so this file contains neither. */
const DASHES = new RegExp(`[${String.fromCodePoint(0x2013)}${String.fromCodePoint(0x2014)}]`);
const seedId = makeId("seed", "s1");
const sessionId = makeId("session", "s1");

async function testApp() {
  return createApp({
    dataRoot: join(root, "data"),
    studioDist: emptyDist,
    heartbeatMs: 50,
    layers: {
      // Library stays empty here so the 501 path keeps a route to prove it on.
      library: () => {},
      orchestra: (app, ctx) => {
        implement(app, ctx, "seeds.get", ({ params }) => {
          if (params.id === "seed_drift") return { seed: { id: "wrong" } } as never;
          return {
            seed: {
              id: params.id,
              title: "Ink site",
              idea: "A website for my tattoo shop",
              problem: "",
              audience: "",
              successSignal: "",
              kind: "website",
              platforms: ["web"],
              features: [],
              constraints: [],
              references: [],
              answers: [],
              parameters: {},
              status: "draft",
              decision: "pending",
              createdAt: ctx.startedAt,
              updatedAt: ctx.startedAt,
            },
          };
        });
        implement(app, ctx, "seeds.create", ({ body }) => {
          throw new Error(`unreachable for ${body.title} with token=sk-abcdefghijklmnop`);
        });
        implementStream(app, ctx, "sessions.stream", ({ params }) => forSession(params.id));
      },
    },
  });
}

describe("core routes", () => {
  it("answers health at /api/health and /health with layer status", async () => {
    const { app } = await testApp();
    const response = await app.request("/api/health");
    expect(response.status).toBe(200);
    const health = healthSchema.parse(await response.json());
    expect(health.ports.server).toBe(PORTS.server);
    expect(health.layers).toMatchObject({ core: true, orchestra: true, library: false });
    expect(health.studio.served).toBe(false);
    expect((await app.request("/health")).status).toBe(200);
  });

  it("answers an unimplemented layer route with 501 in plain words", async () => {
    const { app } = await testApp();
    const response = await app.request("/api/library");
    expect(response.status).toBe(501);
    const body = (await response.json()) as { error: { code: string; message: string } };
    expect(body.error.code).toBe("not_implemented");
    expect(body.error.message).toContain("the library layer is not connected yet");
    expect(body.error.message).not.toMatch(DASHES);
  });

  it("returns the JSON error shape for unknown API paths and bad bodies", async () => {
    const { app } = await testApp();
    const missing = await app.request("/api/nope");
    expect(missing.status).toBe(404);
    expect(await missing.json()).toMatchObject({ error: { code: "not_found" } });
    const bad = await app.request("/api/seeds", { method: "POST", body: "{", headers: { "content-type": "application/json" } });
    expect(bad.status).toBe(400);
    expect(await bad.json()).toMatchObject({ error: { code: "bad_request", message: "The request body is not valid JSON" } });
    const empty = await app.request("/api/seeds", { method: "POST", body: JSON.stringify({ title: "x", idea: " " }) });
    expect(empty.status).toBe(400);
    expect(((await empty.json()) as { error: { message: string } }).error.message).toContain("Say what you want to build");
  });

  it("serves an implemented route and catches contract drift and leaked secrets", async () => {
    const { app } = await testApp();
    const ok = await app.request(`/api/seeds/${seedId}`);
    expect(ok.status).toBe(200);
    expect(await ok.json()).toMatchObject({ seed: { id: seedId, title: "Ink site" } });
    const drift = await app.request("/api/seeds/seed_drift");
    expect(drift.status).toBe(500);
    expect(await drift.json()).toMatchObject({ error: { code: "internal", message: expect.stringContaining("does not match the contract") } });
    const crash = await app.request("/api/seeds", {
      method: "POST",
      body: JSON.stringify({ title: "Ink", idea: "Shop site", kind: "website", platforms: ["web"] }),
    });
    expect(crash.status).toBe(500);
    const text = await crash.text();
    expect(text).not.toContain("sk-abcdefghijklmnop");
    expect(text).toContain("[redacted]");
  });

  it("allows the studio dev origin and no other", async () => {
    const { app } = await testApp();
    const allowed = await app.request("/api/health", { headers: { origin: `http://127.0.0.1:${PORTS.studioDev}` } });
    expect(allowed.headers.get("access-control-allow-origin")).toBe(`http://127.0.0.1:${PORTS.studioDev}`);
    const other = await app.request("/api/health", { headers: { origin: "http://evil.example" } });
    expect(other.headers.get("access-control-allow-origin")).toBeNull();
  });

  it("says plainly when the studio is not built, and serves it when it is", async () => {
    const { app } = await testApp();
    const page = await app.request("/imagistation");
    expect(await page.text()).toContain("The studio is not built yet");
    const dist = join(root, "studio-dist");
    mkdirSync(join(dist, "assets"), { recursive: true });
    writeFileSync(join(dist, "index.html"), "<!doctype html><div id=root></div>");
    writeFileSync(join(dist, "assets", "app.js"), "console.log(1)");
    const built = await createApp({ dataRoot: join(root, "data"), studioDist: dist });
    expect(await (await built.app.request("/library")).text()).toContain("id=root");
    expect(await (await built.app.request("/assets/app.js")).text()).toBe("console.log(1)");
  });
});

describe("SSE", () => {
  it("streams published events for the session and replays after Last-Event-ID", async () => {
    const { app, ctx } = await testApp();
    const response = await app.request(`/api/sessions/${sessionId}/stream`);
    expect(response.headers.get("content-type")).toContain("text/event-stream");
    const reader = response.body!.getReader();
    const decoder = new TextDecoder();
    let text = "";
    const publish = (): SessionEvent =>
      ctx.events.publish({
        type: "session.status",
        sessionId,
        data: { sessionId, status: "running", summary: "Reading the Seed" },
      });
    ctx.events.publish({ type: "log", sessionId: makeId("session", "other"), data: { level: "info", message: "not mine" } });
    const first = publish();
    while (!text.includes("Reading the Seed") || !text.includes("event: heartbeat")) {
      const chunk = await reader.read();
      if (chunk.done) break;
      text += decoder.decode(chunk.value);
    }
    await reader.cancel();
    expect(text).toContain("event: ready");
    expect(text).toContain(`id: ${first.id}`);
    expect(text).not.toContain("not mine");

    const second = publish();
    const replay = await app.request(`/api/sessions/${sessionId}/stream`, { headers: { "last-event-id": first.id } });
    const replayReader = replay.body!.getReader();
    let replayText = "";
    while (!replayText.includes(`id: ${second.id}`)) {
      const chunk = await replayReader.read();
      if (chunk.done) break;
      replayText += decoder.decode(chunk.value);
    }
    await replayReader.cancel();
    expect(replayText).toContain(`id: ${second.id}`);
    expect(replayText).not.toContain(`id: ${first.id}\n`);
  });

  it("refuses an event that does not match the contract", async () => {
    const { ctx } = await testApp();
    expect(() => ctx.events.publish({ type: "log", data: { level: "loud", message: "x" } } as never)).toThrow();
  });
});

describe("startServer", () => {
  it("listens on 127.0.0.1, answers the typed client, and stops", async () => {
    // Mods stays empty here so the typed client's 501 path keeps a route to prove it on.
    const running = await startServer({ port: 17499, dataRoot: join(root, "data"), studioDist: emptyDist, layers: { mods: () => {} } });
    try {
      expect(running.url).toBe("http://127.0.0.1:17499");
      const client = createForgeClient({ baseUrl: running.url });
      await expect(client.call("health")).resolves.toMatchObject({ ok: true, name: "4ge", ports: { server: 17499 } });
      await expect(client.call("mods.list")).rejects.toMatchObject({ code: "not_implemented", status: 501 });
    } finally {
      await running.stop();
    }
  });

  it("explains a taken port and refuses Ark ports", async () => {
    const blocker = createServer();
    await new Promise<void>((resolve) => blocker.listen(17498, "127.0.0.1", resolve));
    try {
      const failure = await startServer({ port: 17498, dataRoot: join(root, "data"), studioDist: emptyDist }).catch((error: unknown) => error);
      expect(failure).toBeInstanceOf(PortTakenError);
      expect((failure as Error).message).toContain("Port 17498 is already in use");
    } finally {
      await new Promise<void>((resolve) => blocker.close(() => resolve()));
    }
    await expect(startServer({ port: 8788, dataRoot: join(root, "data") })).rejects.toThrow(/belongs to Ark/);
  });
});
