import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { createModelClient, modelTimeoutMs, pickModels } from "./model.js";
import { hivemindMessages, modelIdsFrom, readModelInvocation } from "./hivemind.js";
import { MarkerSplitter, block, extractJson, fileBlocks } from "./parse.js";
import { handoffSeal, queueSeedInAinbox, seedHandoff } from "./seeds.js";
import { OrchestraError, redact } from "./errors.js";
import { plainText } from "./util.js";
import { isStarterTooling } from "./worker.js";
import type { Seed } from "@4ge/contracts";

const dirs: string[] = [];
afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

describe("parse", () => {
  it("never leaks a marker split across chunks", () => {
    const splitter = new MarkerSplitter("<<<STATE>>>");
    const shown = ["Hello the", "re. <<<ST", "ATE>>> {\"ready\":", "true}"].map((chunk) => splitter.push(chunk)).join("");
    const end = splitter.finish();
    expect(shown + end.flushed).toBe("Hello there. ");
    expect(JSON.parse(end.hidden)).toEqual({ ready: true });
  });

  it("flushes a held tail when no marker arrives", () => {
    const splitter = new MarkerSplitter("<<<STATE>>>");
    const shown = splitter.push("ends with <<<");
    expect(shown).toBe("ends with ");
    expect(splitter.finish().flushed).toBe("<<<");
  });

  it("reads file blocks, fenced or not, last one wins", () => {
    const text = [
      "<<<FILE src/a.js>>>",
      "```js",
      "export const a = 1;",
      "```",
      "<<<END FILE>>>",
      "<<<FILE ./src/b.txt>>>",
      "first",
      "<<<END FILE>>>",
      "<<<FILE src/b.txt>>>",
      "second",
      "<<<END FILE>>>",
    ].join("\n");
    expect(fileBlocks(text)).toEqual([
      { path: "src/a.js", content: "export const a = 1;\n" },
      { path: "src/b.txt", content: "second\n" },
    ]);
  });

  it("reads blocks a model closes with two angle brackets (seen live from Nemotron 3 Super)", () => {
    const text = ["<<<SUMMARY>>>", "Tokens and styles.", "<<<END SUMMARY>>", "<<<FILE src/style.css>>>", ":root { --bg: #000; }", "<<<END FILE>>", "<<<FILE src/a.css>>", "a {}", "<<<END FILE>>>"].join("\n");
    expect(fileBlocks(text)).toEqual([
      { path: "src/style.css", content: ":root { --bg: #000; }\n" },
      { path: "src/a.css", content: "a {}\n" },
    ]);
    expect(block(text, "SUMMARY")).toBe("Tokens and styles.");
  });

  it("treats the starter's compiler, bundler and lockfile setup as kept", () => {
    expect(["tsconfig.json", "tsconfig.node.json", "vite.config.ts", "pnpm-lock.yaml"].every(isStarterTooling)).toBe(true);
    expect(["package.json", "src/tsconfig.json", "src/main.ts", "Dockerfile"].some(isStarterTooling)).toBe(false);
  });

  it("finds JSON after reasoning and inside fences", () => {
    expect(extractJson("<think>hmm {not json}</think>\n```json\n{\"a\": 1,}\n```")).toEqual({ a: 1 });
    expect(extractJson("Sure: [1, 2]")).toEqual([1, 2]);
  });

  it("writes plain words without em or en dashes", () => {
    expect(plainText(`fast ${String.fromCodePoint(0x2014)} and cheap, 2${String.fromCodePoint(0x2013)}3 days`)).toBe("fast, and cheap, 2-3 days");
  });
});

describe("redact", () => {
  it("hides credentials but keeps the provider's name readable", () => {
    expect(redact("Nebius Token Factory answered 500")).toBe("Nebius Token Factory answered 500");
    expect(redact("api_key=abc123secretvalue")).toBe("api_key=[redacted]");
    expect(redact('{"token": "abcd1234efgh5678"}')).not.toContain("abcd1234efgh5678");
    expect(redact("token abcd1234efgh5678ijkl")).toBe("token [redacted]");
    expect(redact("Authorization: Bearer abcdefghijklmnop")).not.toContain("abcdefghijklmnop");
  });
});

describe("model timeout", () => {
  it("reads FORGE_MODEL_TIMEOUT_MS and falls back to four minutes", () => {
    expect(modelTimeoutMs({})).toBe(240_000);
    expect(modelTimeoutMs({ FORGE_MODEL_TIMEOUT_MS: "3000" })).toBe(3000);
    expect(modelTimeoutMs({ FORGE_MODEL_TIMEOUT_MS: "soon" })).toBe(240_000);
  });
});

describe("model routing", () => {
  it("prefers the default ids the provider lists, then the closest family", () => {
    expect(
      pickModels([
        "nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B",
        "nvidia/nemotron-3-super-120b-a12b",
        "nvidia/Nemotron-3.5-Super-VL",
      ]),
    ).toEqual({
      lead: "nvidia/nemotron-3-super-120b-a12b",
      worker: "nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B",
      vision: "nvidia/Nemotron-3.5-Super-VL",
    });
    expect(pickModels(["acme/nemotron-3-super-v2", "acme/tiny"])).toEqual({
      lead: "acme/nemotron-3-super-v2",
      worker: "acme/nemotron-3-super-v2",
      vision: "acme/nemotron-3-super-v2",
    });
  });

  it("routes to none with plain words when no key is set", async () => {
    const client = createModelClient({});
    const route = await client.route();
    expect(route.provider).toBe("none");
    expect(route.reason).toMatch(/Nebius/);
    expect(await client.models()).toEqual([]);
    await expect(client.chat({ tier: "lead", purpose: "t", messages: [] })).rejects.toMatchObject({
      code: "model_unavailable",
    });
  });

  it("uses Hivemind when only Hivemind is configured", async () => {
    const calls: unknown[] = [];
    const client = createModelClient(
      { HIVEMIND_URL: "https://hivemind.example/mcp", HIVEMIND_TOKEN: "test-token" },
      {
        hivemind: () => ({
          listModels: async () => ({ ids: ["ark/nemotron-3-super", "ark/nemotron-3-nano"], fromProvider: true }),
          invoke: async (request) => {
            calls.push(request);
            return { text: "<think>x</think>hi", model: request.model, promptTokens: 3 };
          },
        }),
      },
    );
    const route = await client.route();
    expect(route.provider).toBe("hivemind");
    expect(route.models.worker).toBe("ark/nemotron-3-nano");
    const result = await client.chat({ tier: "worker", purpose: "t", messages: [{ role: "user", content: "hello" }] });
    expect(result.text).toBe("hi");
    expect(result.model).toBe("ark/nemotron-3-nano");
    expect(calls).toHaveLength(1);
  });

  it("reads model_invoke envelopes like Holodex does", () => {
    expect(
      readModelInvocation(
        JSON.stringify({ ok: true, data: { model: "ark/x", choices: [{ message: { content: "yes" } }], usage: { prompt_tokens: 2 } } }),
        false,
        "ark/y",
      ),
    ).toEqual({ text: "yes", model: "ark/x", promptTokens: 2 });
    expect(() => readModelInvocation(JSON.stringify({ ok: false, code: "scope-denied", error: "missing model.invoke" }), true, "m")).toThrow(
      OrchestraError,
    );
    const many = Array.from({ length: 20 }, (_, index) => ({ role: index === 0 ? "system" : "user", content: `m${index}` }));
    const kept = hivemindMessages([...many, { role: "user", content: "x".repeat(9000) }]);
    expect(kept).toHaveLength(16);
    expect(kept[0]?.role).toBe("system");
    expect(kept.at(-1)?.content.length).toBe(8000);
    expect(modelIdsFrom({ models: [{ id: "ark/nemotron-3-super" }, { alias: "ark/nemotron-3-nano" }] })).toEqual([
      "ark/nemotron-3-super",
      "ark/nemotron-3-nano",
    ]);
  });
});

describe("AInbox handoff", () => {
  const seed: Seed = {
    id: "seed_test1",
    title: "Ink and Iron site",
    idea: "a website for my tattoo shop",
    problem: "People cannot see our work or book online",
    audience: "Walk-in and returning clients",
    successSignal: "Ten online bookings a week",
    kind: "website",
    platforms: ["web"],
    features: ["portfolio", "booking"],
    constraints: [],
    references: [],
    answers: [],
    parameters: {},
    status: "ready",
    decision: "pending",
    createdAt: "2026-10-30T12:00:00.000Z",
    updatedAt: "2026-10-30T12:00:00.000Z",
  };

  it("writes AInbox's own StackHandoff shape into pending", () => {
    const dir = mkdtempSync(join(tmpdir(), "4ge-ainbox-"));
    dirs.push(dir);
    const name = queueSeedInAinbox(dir, seed, "Saturday morning");
    expect(name).toBe("scroll_seed_seed_test1.json");
    const written = JSON.parse(readFileSync(join(dir, "pending", name), "utf8"));
    expect(Object.keys(written)).toEqual([
      "ScrollId",
      "Group",
      "GroupLabel",
      "PodId",
      "IntentId",
      "IntentLabel",
      "Ask",
      "Fields",
      "Items",
      "Sha256",
      "CreatedAt",
    ]);
    expect(written.Items[0].Text).toContain("a website for my tattoo shop");
    expect(written.Sha256).toMatch(/^[0-9a-f]{64}$/);
  });

  it("seals like HandoffSeal: same content, same seal, whatever the time", () => {
    const one = seedHandoff(seed, "note", "2026-10-30T12:00:00.000Z");
    const two = seedHandoff(seed, "note", "2026-10-31T12:00:00.000Z");
    expect(one.Sha256).toBe(two.Sha256);
    expect(handoffSeal("4ge", "build-later", one.Ask, one.Fields, [one.Items[0]?.Sha256 ?? ""])).toBe(one.Sha256);
    expect(seedHandoff(seed, "other note").Sha256).not.toBe(one.Sha256);
  });
});
