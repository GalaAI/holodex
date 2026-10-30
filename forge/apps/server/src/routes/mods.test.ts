import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { LIBRARY_ORIGINS, libraryResponseSchema } from "@4ge/contracts";
import { MemoryCredentialStore, type CommandRunner, type RunResult } from "@4ge/mods";
import { afterAll, describe, expect, it } from "vitest";
import { createApp } from "../app.js";
import { createLibraryLayer } from "./library.js";
import { createModsLayer } from "./mods.js";

const root = mkdtempSync(join(tmpdir(), "4ge-mods-routes-"));
afterAll(() => rmSync(root, { recursive: true, force: true }));

/** Vercel installed and signed in; nothing else installed. Records opened windows. */
const opened: string[] = [];
const runner: CommandRunner = {
  which: (bin) => bin === "vercel",
  run: async (bin, args): Promise<RunResult> => {
    const line = [bin, ...args].join(" ");
    if (line === "vercel whoami --format json") return { exitCode: 0, stdout: '{"username":"jo"}', stderr: "", timedOut: false };
    if (line === "vercel project add ink") return { exitCode: 0, stdout: "Success! Project ink added", stderr: "", timedOut: false };
    return { exitCode: 1, stdout: "", stderr: "", timedOut: false };
  },
  open: async (bin, args) => {
    opened.push([bin, ...args].join(" "));
  },
};
const credentials = new MemoryCredentialStore();
const fakeFetch = (async () => new Response(JSON.stringify({ data: [{ id: "m" }] }), { status: 200 })) as typeof fetch;
const overrides = { runner, credentials, fetch: fakeFetch };

async function app() {
  const forge = await createApp({
    dataRoot: join(root, "data"),
    studioDist: join(root, "no-studio"),
    env: {},
    layers: { mods: createModsLayer(overrides), library: createLibraryLayer(overrides) },
  });
  const call = (method: string, path: string, body?: unknown) =>
    forge.app.request(`/api${path}`, {
      method,
      ...(body !== undefined ? { body: JSON.stringify(body), headers: { "content-type": "application/json" } } : {}),
    });
  return { forge, call };
}

describe("mods and library routes", () => {
  it("lists the built-in mods and finds one by its encoded ref", async () => {
    const { call } = await app();
    const list = await call("GET", "/mods");
    expect(list.status).toBe(200);
    const body = (await list.json()) as { mods: Array<{ id: string }> };
    expect(body.mods.map((mod) => mod.id)).toEqual(["mod:dev", "mod:pc", "mod:launch"]);
    const one = await call("GET", `/mods/${encodeURIComponent("mod:launch")}`);
    expect(((await one.json()) as { mod: { name: string } }).mod.name).toBe("Launch Mod");
    const missing = await call("GET", "/mods/mod%3Anowhere");
    expect(missing.status).toBe(404);
  });

  it("creates a mod with the plus tile and refuses a duplicate", async () => {
    const { call } = await app();
    const created = await call("POST", "/mods", {
      name: "Shop Launch",
      description: "Vercel for the shop site",
      adapters: ["adapter:vercel"],
      augments: ["augment:vercel-mcp"],
    });
    expect(created.status).toBe(201);
    expect(((await created.json()) as { mod: { status: string } }).mod.status).toBe("ready");
    const again = await call("POST", "/mods", { name: "Shop Launch", description: "x" });
    expect(again.status).toBe(409);
  });

  it("connects a token adapter without echoing the token and opens the CLI login for a CLI adapter", async () => {
    const { call } = await app();
    const token = await call("POST", "/adapters/adapter%3Anebius/connect", { token: "nb-route-test" });
    const text = await token.text();
    expect(token.status).toBe(200);
    expect(text).not.toContain("nb-route-test");
    expect(JSON.parse(text).action.kind).toBe("done");
    expect(await credentials.read("4GE:adapter:nebius")).toBe("nb-route-test");
    const cli = await call("POST", "/adapters/adapter%3Avercel/connect", {});
    expect(((await cli.json()) as { action: { kind: string } }).action.kind).toBe("done");
    const missing = await call("POST", "/adapters/adapter%3Arailway/connect", {});
    expect(((await missing.json()) as { action: { kind: string } }).action.kind).toBe("install-cli");
  });

  it("runs adapter actions, refusing a purchase until confirmed", async () => {
    const { call } = await app();
    const project = await call("POST", "/adapters/adapter%3Avercel/actions/create-project", { params: { name: "ink" } });
    expect(((await project.json()) as { run: { status: string } }).run.status).toBe("done");
    const buy = await call("POST", "/adapters/adapter%3Avercel/actions/buy-domain", { params: { domain: "ink-shop.com" } });
    expect(((await buy.json()) as { run: { status: string } }).run.status).toBe("refused");
    const confirmed = await call("POST", "/adapters/adapter%3Avercel/actions/buy-domain", { params: { domain: "ink-shop.com" }, confirm: true });
    expect(((await confirmed.json()) as { run: { status: string } }).run.status).toBe("opened");
    expect(opened).toContain("vercel domains buy ink-shop.com");
    const nobuild = await call("POST", "/adapters/adapter%3Avercel/actions/create-project", { params: { name: "ink", buildId: "bld_none" } });
    expect(nobuild.status).toBe(404);
    const unknown = await call("POST", "/adapters/adapter%3Avercel/actions/launch-rocket", {});
    expect(unknown.status).toBe(404);
  });

  it("answers the Library grouped by origin and manages repo shortcuts", async () => {
    const { call } = await app();
    const added = await call("POST", "/library/repos", { url: "https://github.com/GalaAI/holodex" });
    expect(added.status).toBe(200);
    const bad = await call("POST", "/library/repos", { url: "file:///C:/secret" });
    expect(bad.status).toBe(400);
    const library = libraryResponseSchema.parse(await (await call("GET", "/library")).json());
    expect(library.groups.map((group) => group.origin)).toEqual([...LIBRARY_ORIGINS]);
    expect(library.groups[1]?.items.map((item) => item.title)).toEqual(["GalaAI/holodex"]);
    expect(library.groups[0]?.items.some((item) => item.title === "Ryozan")).toBe(true);
    const removed = await call("DELETE", `/library/repos/${encodeURIComponent("repo:galaai-holodex")}`);
    expect(await removed.json()).toEqual({ removed: true });
  });
});
