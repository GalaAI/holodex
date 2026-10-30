import { existsSync, mkdtempSync, readdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createForgeClient, ForgeApiError } from "@4ge/contracts";
import { afterAll, describe, expect, it } from "vitest";
import { createApp } from "../app.js";

/**
 * The orchestra routes against the real @4ge/orchestra, with no model key
 * (the "no model" edge case) and no Atlas layer: what the person sees must
 * say plainly what is not connected, and nothing may need a model to fail
 * cleanly.
 */
const root = mkdtempSync(join(tmpdir(), "4ge-orchestra-routes-"));
afterAll(() => rmSync(root, { recursive: true, force: true }));

async function client() {
  const { app, ctx } = await createApp({
    dataRoot: join(root, "data"),
    studioDist: join(root, "no-studio"),
    env: { FORGE_AINBOX_QUEUE: join(root, "ainbox") },
    heartbeatMs: 50,
    layers: { atlas: () => {}, design: () => {}, sandbox: () => {} },
  });
  const api = createForgeClient({ baseUrl: "http://4ge.test", fetch: (input, init) => Promise.resolve(app.request(String(input), init)) });
  return { api, app, ctx };
}

describe("orchestra routes", () => {
  it("reports the model route as none with a way forward", async () => {
    const { api } = await client();
    const { route, models } = await api.call("models.list", {});
    expect(route.provider).toBe("none");
    expect(route.reason).toMatch(/Nebius Token Factory key/);
    expect(models).toEqual([]);
  });

  it("opens Imagistation, keeps the idea, and says no model is connected", async () => {
    const { api } = await client();
    const { session, messages } = await api.call("imagistation.sessions.create", { body: { idea: "a website for my tattoo shop" } });
    expect(session.agent).toBe("prometheus");
    expect(messages.find((message) => message.role === "user")?.content).toBe("a website for my tattoo shop");
    expect(messages.at(-1)?.content).toMatch(/No model is connected/);
    const error = await api
      .call("imagistation.messages.send", { params: { id: session.id }, body: { content: "hello" } })
      .catch((caught: unknown) => caught);
    expect(error).toBeInstanceOf(ForgeApiError);
    expect((error as ForgeApiError).code).toBe("model_unavailable");
    const listed = await api.call("imagistation.sessions.list", {});
    expect(listed.sessions.some((item) => item.id === session.id)).toBe(true);
  });

  it("saves a Seed and queues it in AInbox for later", async () => {
    const { api } = await client();
    const { seed } = await api.call("seeds.create", {
      body: { title: "Ink and Iron", idea: "a website for my tattoo shop", kind: "website", platforms: ["web"] },
    });
    expect(seed.status).toBe("draft");
    const decided = await api.call("seeds.decide", { params: { id: seed.id }, body: { when: "later", reminderNote: "Saturday" } });
    expect(decided.seed.status).toBe("queued");
    expect(readdirSync(join(root, "ainbox", "pending"))).toContain(decided.seed.ainboxItem);
  });

  it("refuses to build a Seed without a Blueprint, in plain words", async () => {
    const { api } = await client();
    const { seed } = await api.call("seeds.create", {
      body: { title: "Ink", idea: "a website for my tattoo shop", kind: "website", platforms: ["web"] },
    });
    const error = (await api.call("seeds.decide", { params: { id: seed.id }, body: { when: "now" } }).catch((caught: unknown) => caught)) as ForgeApiError;
    expect(error.code).toBe("bad_request");
    expect(error.message).toMatch(/no Blueprint yet/);
    const missing = (await api.call("builds.get", { params: { id: "bld_missing" } }).catch((caught: unknown) => caught)) as ForgeApiError;
    expect(missing.code).toBe("not_found");
  });

  it("opens a Library session for Smith and cancels it with its subtree", async () => {
    const { api } = await client();
    const { session } = await api.call("sessions.create", {
      body: { agent: "smith", kind: "mod", task: "Upgrade the Dev Mod", subject: { type: "mod", id: "mod:dev", label: "Dev Mod" } },
    });
    const { tree } = await api.call("sessions.tree", { params: { id: session.id } });
    expect(tree.id).toBe(session.id);
    expect(tree.children).toEqual([]);
    const cancelled = await api.call("sessions.cancel", { params: { id: session.id } });
    expect(["cancelled", "waiting"]).toContain(cancelled.session.status);
    const listed = await api.call("sessions.list", { query: {} });
    expect(listed.sessions.some((item) => item.id === session.id)).toBe(true);
  });

  it("serves build files only for a known build and a path inside it", async () => {
    const { app, ctx } = await client();
    expect(ctx.implemented.has("builds.files.get")).toBe(true);
    const missing = await app.request("/api/builds/bld_missing/files?path=previews/site.md");
    expect(missing.status).toBe(404);
    expect(((await missing.json()) as { error: { code: string } }).error.code).toBe("not_found");
    const escape = await app.request("/api/builds/bld_missing/files?path=../../secrets.txt");
    expect(escape.status).toBe(400);
  });

  it("streams a session's events over SSE", async () => {
    const { app, api } = await client();
    const { session } = await api.call("imagistation.sessions.create", { body: {} });
    const response = await app.request(`/api/sessions/${session.id}/stream`);
    expect(response.headers.get("content-type")).toMatch(/text\/event-stream/);
    const reader = response.body?.getReader();
    const first = await reader?.read();
    expect(new TextDecoder().decode(first?.value)).toMatch(/event: ready/);
    await reader?.cancel();
    expect(existsSync(join(root, "data", "sessions", `${session.id}.json`))).toBe(true);
  });
});
