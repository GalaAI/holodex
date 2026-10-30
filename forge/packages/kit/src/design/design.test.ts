import { componentCandidateSchema, makeId, tweakOpSchema, type TweakOp } from "@4ge/contracts";
import { describe, expect, it } from "vitest";
import {
  DESIGN_SYSTEM_PRESETS,
  DesignInputError,
  TOKENS_BLOCK_ID,
  TWEAK_SHEET_HEADER,
  componentNameFromPath,
  composeSheet,
  ensureImport,
  ensureStylesheetLink,
  filterCandidates,
  findElements,
  insertAfter,
  kitCandidates,
  listBlocks,
  phosphorTokens,
  registrySnapshotCandidates,
  relativeSpecifier,
  removeBlock,
  removeElement,
  replaceText,
  revertPatch,
  setAttribute,
  targetAttr,
  tokensToCss,
  tweakToCss,
  upsertBlock,
} from "./index.js";

const buildId = makeId("build", "design1");
const op = (input: Record<string, unknown>): TweakOp =>
  tweakOpSchema.parse({ buildId, target: { selector: '[data-4ge-id="hero"]' }, ...input });

describe("design tokens", () => {
  it("Phosphor preset is read from the kit token mirror", () => {
    const tokens = phosphorTokens();
    expect(tokens.colors.find((color) => color.name === "background")?.value).toBe("#050607");
    expect(DESIGN_SYSTEM_PRESETS.map((preset) => preset.key)).toEqual(["phosphor", "clean-light", "ink-studio"]);
  });

  it("compiles every token kind to custom properties", () => {
    const css = tokensToCss(phosphorTokens());
    expect(css.startsWith(":root {")).toBe(true);
    expect(css).toContain("--color-primary: #79C4B7;");
    expect(css).toContain("--font-display-family:");
    expect(css).toContain("--space-4: 16px;");
    expect(css).toContain("--radius-card: 18px;");
    expect(css).toContain("--motion-step-duration: 320ms;");
  });

  it("refuses a token value that could inject CSS", () => {
    expect(() =>
      tokensToCss({ ...phosphorTokens(), colors: [{ name: "x", value: "red; } body { display:none", role: "other" }] }),
    ).toThrow(DesignInputError);
  });
});

describe("tweaks compile to CSS", () => {
  it("color, including a token reference and a token change", () => {
    expect(tweakToCss(op({ kind: "color", property: "background", value: "#112233" }))).toBe(
      '[data-4ge-id="hero"] {\n  background: #112233;\n}',
    );
    expect(tweakToCss(op({ kind: "color", property: "color", value: "--color-primary" }))).toContain("color: var(--color-primary);");
    expect(tweakToCss(op({ kind: "color", property: "token", tokenName: "Primary", value: "#ff0000" }))).toBe(
      ":root {\n  --color-primary: #ff0000;\n}",
    );
  });

  it("placement, layout, transition", () => {
    expect(tweakToCss(op({ kind: "placement", x: 12, y: -4, order: 2 }))).toContain("translate: 12px -4px;");
    const layout = tweakToCss(op({ kind: "layout", columns: 3, gap: "24px" })) ?? "";
    expect(layout).toContain("display: grid;");
    expect(layout).toContain("grid-template-columns: repeat(3, minmax(0, 1fr));");
    expect(tweakToCss(op({ kind: "transition", property: "opacity", durationMs: 200 }))).toContain("transition: opacity 200ms ease;");
    expect(() => tweakToCss(op({ kind: "layout" }))).toThrow(/at least one change/);
  });

  it("animation ships its keyframes; motion respects reduced motion", () => {
    const animation = tweakToCss(op({ kind: "animation", name: "fade-up", durationMs: 600 })) ?? "";
    expect(animation).toContain("@keyframes fge-fade-up");
    expect(animation).toContain("animation: fge-fade-up 600ms ease-out 0ms 1 both;");
    const motion = tweakToCss(op({ kind: "motion", preset: "hover-lift", intensity: 1 })) ?? "";
    expect(motion.startsWith("@media (prefers-reduced-motion: no-preference)")).toBe(true);
    expect(motion).toContain(':hover {');
    expect(() => tweakToCss(op({ kind: "motion", preset: "wobble" }))).toThrow(/no motion preset/);
  });

  it("source tweaks are not CSS, and hostile selectors are refused", () => {
    expect(tweakToCss(op({ kind: "text", text: "Hi" }))).toBeNull();
    expect(() => tweakToCss(op({ kind: "color", property: "color", value: "red", target: { selector: "a{}" } }))).toThrow(
      DesignInputError,
    );
  });
});

describe("managed sheet blocks", () => {
  it("upserts, lists, replaces and removes one block at a time", () => {
    let sheet = upsertBlock("", "twk_a", "a { color: red; }");
    sheet = upsertBlock(sheet, "twk_b", "b { color: blue; }");
    sheet = upsertBlock(sheet, TOKENS_BLOCK_ID, ":root { --x: 1; }", { first: true });
    expect(sheet.startsWith(TWEAK_SHEET_HEADER)).toBe(true);
    expect(listBlocks(sheet).map((block) => block.id)).toEqual([TOKENS_BLOCK_ID, "twk_a", "twk_b"]);
    sheet = upsertBlock(sheet, "twk_a", "a { color: green; }");
    expect(listBlocks(sheet).map((block) => block.id)).toEqual([TOKENS_BLOCK_ID, "twk_b", "twk_a"]);
    sheet = removeBlock(sheet, "twk_b");
    expect(listBlocks(sheet).map((block) => [block.id, block.css])).toEqual([
      [TOKENS_BLOCK_ID, ":root { --x: 1; }"],
      ["twk_a", "a { color: green; }"],
    ]);
    expect(composeSheet(":root{}", [{ id: "twk_c", css: "c{}" }])).toContain("/* 4ge:begin twk_c */");
  });
});

describe("source edits", () => {
  const jsx = [
    'import { useState } from "react";',
    "",
    "export function Hero() {",
    "  return (",
    "    <section data-4ge-id=\"hero\" onClick={() => go(1 > 0)}>",
    '      <h1 data-4ge-id="hero-title" className="title">Ink and Iron</h1>',
    '      <img data-4ge-id="hero-image" src="/old.jpg" alt="old" />',
    "      <div><div>nested</div></div>",
    "    </section>",
    "  );",
    "}",
  ].join("\n");

  it("names targets by data-4ge-id or id, and leaves class selectors to CSS", () => {
    expect(targetAttr('[data-4ge-id="hero"]')).toEqual({ attr: "data-4ge-id", value: "hero" });
    expect(targetAttr("#cta")).toEqual({ attr: "id", value: "cta" });
    expect(targetAttr("hero-title")).toEqual({ attr: "data-4ge-id", value: "hero-title" });
    expect(targetAttr(".btn > span")).toBeNull();
    expect(targetAttr("section")).toBeNull();
  });

  it("finds elements past JSX arrow functions and nested tags", () => {
    const [section] = findElements(jsx, { attr: "data-4ge-id", value: "hero" });
    expect(section?.tag).toBe("section");
    expect(jsx.slice(section?.closeStart, section?.end)).toBe("</section>");
    const [image] = findElements(jsx, { attr: "data-4ge-id", value: "hero-image" });
    expect(image?.selfClosing).toBe(true);
  });

  it("changes text with an undo patch that survives other edits", () => {
    const [title] = findElements(jsx, { attr: "data-4ge-id", value: "hero-title" });
    const edited = replaceText("src/Hero.tsx", jsx, title!, "Ink {and} Steel");
    expect(edited.source).toContain('>{"Ink {and} Steel"}</h1>');
    const [image] = findElements(edited.source, { attr: "data-4ge-id", value: "hero-image" });
    const swapped = setAttribute("src/Hero.tsx", edited.source, image!, "src", "/new.jpg");
    expect(swapped.source).toContain('src="/new.jpg"');
    // Undo the text edit while the image edit stays.
    const undone = revertPatch(swapped.source, edited.patch);
    expect(undone.ok).toBe(true);
    if (undone.ok) {
      expect(undone.source).toContain(">Ink and Iron</h1>");
      expect(undone.source).toContain('src="/new.jpg"');
    }
  });

  it("refuses to undo a region that changed since", () => {
    const [title] = findElements(jsx, { attr: "data-4ge-id", value: "hero-title" });
    const first = replaceText("src/Hero.tsx", jsx, title!, "One");
    const [again] = findElements(first.source, { attr: "data-4ge-id", value: "hero-title" });
    const second = replaceText("src/Hero.tsx", first.source, again!, "Two");
    const result = revertPatch(second.source, first.patch);
    expect(result.ok).toBe(false);
  });

  it("refuses text edits on elements that hold markup", () => {
    const [section] = findElements(jsx, { attr: "data-4ge-id", value: "hero" });
    expect(() => replaceText("src/Hero.tsx", jsx, section!, "x")).toThrow(/markup or code/);
  });

  it("removes and inserts elements, and adds imports once", () => {
    const [image] = findElements(jsx, { attr: "data-4ge-id", value: "hero-image" });
    const removed = removeElement("src/Hero.tsx", jsx, image!);
    expect(removed.source).not.toContain("hero-image");
    expect(revertPatch(removed.source, removed.patch)).toEqual({ ok: true, source: jsx });
    const [title] = findElements(jsx, { attr: "data-4ge-id", value: "hero-title" });
    const inserted = insertAfter("src/Hero.tsx", jsx, title!, "<Badge />");
    expect(inserted.source).toContain('</h1>\n      <Badge />');
    const imported = ensureImport("src/Hero.tsx", inserted.source, "Badge", "./components/Badge");
    expect(imported?.source.split("\n")[1]).toBe('import Badge from "./components/Badge";');
    expect(ensureImport("src/Hero.tsx", imported!.source, "Badge", "./components/Badge")).toBeNull();
  });

  it("links the tweak sheet before </body> once", () => {
    const html = "<html><head></head><body><div id=app></div></body></html>";
    const linked = ensureStylesheetLink("index.html", html, "/4ge-tweaks.css");
    expect(linked?.source).toContain('<link rel="stylesheet" href="/4ge-tweaks.css" data-4ge-tweaks />\n</body>');
    expect(ensureStylesheetLink("index.html", linked!.source, "/4ge-tweaks.css")).toBeNull();
  });

  it("module paths and component names", () => {
    expect(relativeSpecifier("src/pages/Home.tsx", "src/components/Hero.tsx")).toBe("../components/Hero");
    expect(relativeSpecifier("src/App.tsx", "src/Hero.tsx")).toBe("./Hero");
    expect(componentNameFromPath("src/components/hero-banner.tsx")).toBe("HeroBanner");
  });
});

describe("inventory candidates record their source", () => {
  it("every candidate parses and names where it came from", () => {
    const all = [...kitCandidates(), ...registrySnapshotCandidates()];
    for (const candidate of all) expect(componentCandidateSchema.safeParse(candidate).success).toBe(true);
    expect(all.filter((candidate) => candidate.via === "kit").every((candidate) => candidate.source === "kit")).toBe(true);
    const registry = all.filter((candidate) => candidate.source === "21st");
    expect(registry.length).toBeGreaterThan(10);
    for (const candidate of registry) {
      expect(candidate.sourceUrl).toMatch(/^https:\/\/21st\.dev\/@/);
      expect(candidate.fetchedAt).toBeDefined();
      expect(candidate.installCommand).toContain("$API_KEY_21ST");
    }
  });

  it("filters by need", () => {
    const booking = filterCandidates(registrySnapshotCandidates(), "booking calendar");
    expect(booking[0]?.category).toBe("booking");
    expect(filterCandidates(kitCandidates(), "").length).toBe(kitCandidates().length);
  });
});
