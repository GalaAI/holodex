import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { ForgeApiError, createForgeClient, makeId, type SessionEvent } from "@4ge/contracts";
import { DESIGN_SYSTEM_PRESETS } from "@4ge/kit/design";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { createApp, type ForgeApp } from "../app.js";

const root = mkdtempSync(join(tmpdir(), "4ge-design-"));
afterAll(() => rmSync(root, { recursive: true, force: true }));

const buildId = makeId("build", "design");
const bareBuild = makeId("build", "bare");
let forge: ForgeApp;
let client: ReturnType<typeof createForgeClient>;
let app: string;
const events: SessionEvent[] = [];

const HERO = `import { useState } from "react";

export function Hero() {
  const [open] = useState(false);
  return (
    <section data-4ge-id="hero" className="hero" onClick={() => open && 1 > 0}>
      <h1 data-4ge-id="hero-title">Ink and Iron</h1>
      <img data-4ge-id="hero-image" src="/old.jpg" alt="Old" />
      <p data-4ge-id="hero-copy">Walk-ins welcome.</p>
    </section>
  );
}
`;

beforeAll(async () => {
  const data = join(root, "data");
  forge = await createApp({ dataRoot: data, studioDist: join(root, "no-studio"), heartbeatMs: 50, env: { PATH: "" } });
  forge.ctx.events.subscribe(() => true, (event) => events.push(event));
  client = createForgeClient({ baseUrl: "http://4ge.test", fetch: ((input: string | URL | Request, init?: RequestInit) => forge.app.request(String(input), init)) as typeof fetch });
  const paths = forge.ctx.paths.build(buildId);
  app = paths.app;
  mkdirSync(join(app, "public"), { recursive: true });
  mkdirSync(join(app, "src", "components"), { recursive: true });
  writeFileSync(join(app, "index.html"), '<!doctype html>\n<html><head><title>Ink</title></head>\n<body>\n  <div id="root"></div>\n</body>\n</html>\n');
  writeFileSync(join(app, "src", "Hero.tsx"), HERO);
  writeFileSync(join(app, "src", "components", "Badge.tsx"), "export default function Badge() { return <span>New</span>; }\n");
  writeFileSync(join(app, "public", "new.jpg"), "jpg");
  mkdirSync(forge.ctx.paths.build(bareBuild).root, { recursive: true });
});

const read = (rel: string) => readFileSync(join(app, rel), "utf8");
const target = (id: string) => ({ selector: `[data-4ge-id="${id}"]`, page: "/", label: id });

describe("design layer", () => {
  it("reports itself connected", async () => {
    const health = await client.call("health");
    expect(health.layers.design).toBe(true);
  });

  it("saves a design system and writes its tokens into the build", async () => {
    const preset = DESIGN_SYSTEM_PRESETS.find((candidate) => candidate.key === "ink-studio")!;
    const id = makeId("designSystem", "ink");
    const { system } = await client.call("design.systems.save", {
      params: { id },
      body: { id, buildId, name: preset.name, source: "kit", tokens: preset.tokens, principles: preset.principles, updatedAt: new Date(0).toISOString() },
    });
    expect(system.updatedAt).not.toBe(new Date(0).toISOString());
    expect(read("index.html")).toContain('<link rel="stylesheet" href="/4ge-tweaks.css" data-4ge-tweaks />\n</body>');
    expect(read("public/4ge-tweaks.css")).toContain("--color-primary: #d4402b;");
    const { systems } = await client.call("design.systems.list", { query: { buildId } });
    expect(systems.map((candidate) => candidate.id)).toEqual([id]);
    expect(events.some((event) => event.type === "log" && event.buildId === buildId && event.data.message.includes("Ink studio"))).toBe(true);
  });

  it("refuses a token value that would inject CSS, and writes nothing", async () => {
    const id = makeId("designSystem", "bad");
    const before = read("public/4ge-tweaks.css");
    await expect(
      client.call("design.systems.save", {
        params: { id },
        body: {
          id,
          buildId,
          name: "Bad",
          source: "generated",
          tokens: { colors: [{ name: "x", value: "red; } body { display: none", role: "other" }] },
          updatedAt: new Date().toISOString(),
        },
      }),
    ).rejects.toMatchObject({ code: "bad_request" });
    expect(read("public/4ge-tweaks.css")).toBe(before);
  });

  it("applies a CSS tweak and undoes exactly that tweak", async () => {
    const { tweak: color } = await client.call("design.tweaks.apply", {
      body: { buildId, kind: "color", property: "background", value: "#101010", target: target("hero") },
    });
    const { tweak: motion } = await client.call("design.tweaks.apply", {
      body: { buildId, kind: "motion", preset: "fade-up", target: target("hero") },
    });
    expect(color.status).toBe("applied");
    expect(color.changedFiles).toContain("public/4ge-tweaks.css");
    let sheet = read("public/4ge-tweaks.css");
    expect(sheet).toContain(`/* 4ge:begin ${color.id} */`);
    expect(sheet).toContain("fge-motion-fade-up");
    const { tweak: undone } = await client.call("design.tweaks.revert", { params: { id: color.id } });
    expect(undone.status).toBe("reverted");
    sheet = read("public/4ge-tweaks.css");
    expect(sheet).not.toContain(color.id);
    expect(sheet).toContain(motion.id);
    expect(sheet).toContain("--color-primary"); // tokens untouched
    await expect(client.call("design.tweaks.revert", { params: { id: color.id } })).rejects.toMatchObject({ code: "conflict" });
  });

  it("edits text in the real source and undoes it", async () => {
    const { tweak } = await client.call("design.tweaks.apply", {
      body: { buildId, kind: "text", text: "Ink & Steel", target: target("hero-title") },
    });
    expect(tweak.changedFiles).toEqual(["src/Hero.tsx"]);
    expect(read("src/Hero.tsx")).toContain(">Ink & Steel</h1>");
    await client.call("design.tweaks.revert", { params: { id: tweak.id } });
    expect(read("src/Hero.tsx")).toBe(HERO);
  });

  it("swaps an image source and alt from a public asset, and undoes both", async () => {
    const { tweak } = await client.call("design.tweaks.apply", {
      body: { buildId, kind: "asset", assetRef: "public/new.jpg", alt: "Flash sheet", target: target("hero-image") },
    });
    const source = read("src/Hero.tsx");
    expect(source).toContain('src="/new.jpg"');
    expect(source).toContain('alt="Flash sheet"');
    await client.call("design.tweaks.revert", { params: { id: tweak.id } });
    expect(read("src/Hero.tsx")).toBe(HERO);
  });

  it("inserts an inventory component with its import, removes one, and undoes", async () => {
    const componentId = makeId("component", "badge");
    await client.call("design.inventory.save", {
      params: { id: componentId },
      body: {
        id: componentId,
        buildId,
        name: "Badge",
        category: "display",
        status: "built",
        description: "New badge",
        source: "generated",
        sourceRef: "src/components/Badge.tsx",
        updatedAt: new Date().toISOString(),
      },
    });
    const { tweak: insert } = await client.call("design.tweaks.apply", {
      body: { buildId, kind: "component", action: "insert", componentId, target: target("hero-title") },
    });
    let source = read("src/Hero.tsx");
    expect(source).toContain('import Badge from "./components/Badge";');
    expect(source).toContain("</h1>\n      <Badge />");
    const { tweak: remove } = await client.call("design.tweaks.apply", {
      body: { buildId, kind: "component", action: "remove", target: target("hero-copy") },
    });
    expect(read("src/Hero.tsx")).not.toContain("Walk-ins welcome");
    await client.call("design.tweaks.revert", { params: { id: remove.id } });
    await client.call("design.tweaks.revert", { params: { id: insert.id } });
    source = read("src/Hero.tsx");
    expect(source).toBe(HERO);
    const { tweaks } = await client.call("design.tweaks.list", { query: { buildId } });
    expect(tweaks.find((candidate) => candidate.id === insert.id)?.status).toBe("reverted");
  });

  it("refuses an undo when the edited region changed since", async () => {
    const { tweak: first } = await client.call("design.tweaks.apply", {
      body: { buildId, kind: "text", text: "One", target: target("hero-title") },
    });
    const { tweak: second } = await client.call("design.tweaks.apply", {
      body: { buildId, kind: "text", text: "Two", target: target("hero-title") },
    });
    const failure = await client.call("design.tweaks.revert", { params: { id: first.id } }).catch((error: unknown) => error);
    expect(failure).toBeInstanceOf(ForgeApiError);
    expect((failure as ForgeApiError).code).toBe("conflict");
    expect((failure as ForgeApiError).hint).toMatch(/later tweaks/);
    await client.call("design.tweaks.revert", { params: { id: second.id } });
    await client.call("design.tweaks.revert", { params: { id: first.id } });
    expect(read("src/Hero.tsx")).toBe(HERO);
  });

  it("says plainly when a source target is missing, ambiguous or not an element name", async () => {
    await expect(
      client.call("design.tweaks.apply", { body: { buildId, kind: "text", text: "x", target: target("nope") } }),
    ).rejects.toMatchObject({ code: "not_found" });
    await expect(
      client.call("design.tweaks.apply", { body: { buildId, kind: "text", text: "x", target: { selector: ".hero h1" } } }),
    ).rejects.toMatchObject({ code: "bad_request" });
    await expect(
      client.call("design.tweaks.apply", { body: { buildId: bareBuild, kind: "text", text: "x", target: target("hero") } }),
    ).rejects.toMatchObject({ code: "conflict" });
    await expect(
      client.call("design.tweaks.apply", {
        body: { buildId: makeId("build", "ghost"), kind: "text", text: "x", target: target("hero") },
      }),
    ).rejects.toMatchObject({ code: "not_found" });
  });

  it("pins annotations, takes replies from Smith, resolves and reopens", async () => {
    const { annotation } = await client.call("design.annotations.create", {
      body: { buildId, target: { ...target("hero"), x: 0.4, y: 0.2 }, body: "Make the hero darker" },
    });
    expect(annotation).toMatchObject({ status: "open", author: "user", replies: [] });
    const replied = await client.call("design.annotations.update", {
      params: { id: annotation.id },
      body: { reply: "Darkened the hero background.", author: "smith" },
    });
    expect(replied.annotation.replies[0]).toMatchObject({ author: "smith" });
    const resolved = await client.call("design.annotations.update", { params: { id: annotation.id }, body: { status: "resolved" } });
    expect(resolved.annotation.resolvedAt).toBeDefined();
    const reopened = await client.call("design.annotations.update", { params: { id: annotation.id }, body: { status: "open" } });
    expect(reopened.annotation.resolvedAt).toBeUndefined();
    const { annotations } = await client.call("design.annotations.list", { query: { buildId } });
    expect(annotations).toHaveLength(1);
    await expect(
      client.call("design.annotations.update", { params: { id: makeId("annotation", "none") }, body: { status: "resolved" } }),
    ).rejects.toMatchObject({ code: "not_found" });
  });

  it("offers kit and 21st candidates with their source, and says the CLI is missing", async () => {
    const all = await client.call("design.inventory.candidates", { query: {} });
    expect(all.registry.live).toBe(false);
    expect(all.registry.message).toMatch(/21st CLI is not installed/);
    expect(all.candidates.some((candidate) => candidate.via === "kit")).toBe(true);
    expect(all.candidates.some((candidate) => candidate.via === "21st-snapshot" && candidate.sourceUrl)).toBe(true);
    const booking = await client.call("design.inventory.candidates", { query: { buildId, q: "booking" } });
    expect(booking.candidates[0]?.category).toBe("booking");
    const { items } = await client.call("design.inventory.list", { query: { buildId } });
    expect(items.map((item) => item.name)).toEqual(["Badge"]);
  });
});
