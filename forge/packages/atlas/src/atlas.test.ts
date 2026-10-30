import { mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { planBlueprint, validateBamfDocument } from "@4ge/bamf";
import { STAGE_ORDER, blueprintSchema, connectionsByOrigin, type Blueprint } from "@4ge/contracts";
import { afterAll, describe, expect, it } from "vitest";
import {
  AtlasError,
  AtlasRegistry,
  MATCH_THRESHOLD,
  STARTER_BLUEPRINTS,
  STARTER_FOR_KIND,
  ScaffoldError,
  WEBSITE_BLUEPRINT,
  blueprintFromAnswers,
  craftBlueprint,
  craftQuestions,
  freeBlueprintId,
  inferKind,
  instantiateBlueprint,
  listStarterManifests,
  matchBlueprints,
  normalizeBlueprintId,
  scaffoldStarter,
  splitList,
  startersDir,
} from "./index.js";

const root = mkdtempSync(join(tmpdir(), "4ge-atlas-"));
afterAll(() => rmSync(root, { recursive: true, force: true }));
let counter = 0;
const fresh = (name: string) => join(root, `${name}-${(counter += 1)}`);

/** En dash and em dash, built from code points so this file contains neither. */
const DASHES = new RegExp(`[${String.fromCodePoint(0x2013)}${String.fromCodePoint(0x2014)}]`);

const entriesOf = (blueprints: readonly Blueprint[]) =>
  blueprints.map((blueprint) => ({ blueprint, source: "starter" as const, registeredAt: new Date(0).toISOString(), uses: 0 }));

const TATTOO_ANSWERS = {
  name: "Aftercare tracker",
  kind: "mobile app",
  platforms: ["iphone", "android"],
  purpose: "Helps tattoo clients follow aftercare day by day and lets the artist check healing photos.",
  records: "clients, tattoos, healing photos, care steps",
  actions: ["log a care step", "upload a healing photo", "message the artist"],
  roles: "client\nartist",
  integrations: "Supabase database, bookings",
  afterLaunch: ["daily care reminders", "weekly healing report", "crash watch"],
  look: "Soft dark, big type, calm.",
};

describe("starter Blueprints", () => {
  const { starters: manifests, problems } = listStarterManifests(startersDir());

  it("ship a website, web app, mobile app, game, CRM, ERP and desktop app", () => {
    expect(STARTER_BLUEPRINTS.map((blueprint) => blueprint.kind)).toEqual(["website", "web-app", "mobile-app", "game", "crm", "erp", "desktop-app"]);
    expect(new Set(STARTER_BLUEPRINTS.map((blueprint) => blueprint.blueprintId)).size).toBe(STARTER_BLUEPRINTS.length);
  });

  it.each(STARTER_BLUEPRINTS.map((blueprint) => [blueprint.blueprintId, blueprint] as const))("%s is complete and plans as .bamf", (_id, blueprint) => {
    expect(blueprintSchema.parse(blueprint)).toEqual(blueprint);
    expect(blueprint.stages.map((stage) => stage.stage)).toEqual([...STAGE_ORDER]);
    expect(blueprint.origin).toBe("starter");
    expect(blueprint.designSystem?.name).toBeTruthy();
    expect(blueprint.orchestration.length).toBeGreaterThan(0);
    expect(blueprint.orchestrationSuggestions.length).toBe(blueprint.orchestration.length);
    expect(blueprint.suggestedConnections).toContain("mod:launch");
    const deploy = blueprint.stages.find((stage) => stage.stage === "deploy");
    expect(deploy?.layers.filter((layer) => layer.kind === "test-flight").length).toBeGreaterThanOrEqual(2);

    const manifest = manifests.find((entry) => entry.name === blueprint.starter);
    expect(manifest, `starter ${blueprint.starter}`).toBeDefined();
    expect(manifest?.platforms.some((platform) => blueprint.platforms.includes(platform))).toBe(true);

    const plan = planBlueprint(blueprint);
    const layerCount = blueprint.stages.reduce((sum, stage) => sum + stage.layers.length, 0);
    expect(plan.steps).toHaveLength(layerCount + blueprint.stages.length);
    expect(validateBamfDocument(plan.steps.map((step) => step.command)).ok).toBe(true);
    expect(JSON.stringify(blueprint)).not.toMatch(DASHES);
  });

  it("the website suggests Ryozan for bookings and groups its connections by Library origin", () => {
    expect(WEBSITE_BLUEPRINT.orchestration.find((job) => job.key === "bookings")?.connections).toContain("app:ryozan");
    const groups = connectionsByOrigin(WEBSITE_BLUEPRINT.suggestedConnections);
    expect(groups.map((group) => group.origin)).toEqual(["project", "repo", "mod", "augment", "adapter"]);
    expect(groups[0]?.refs).toEqual(["app:ryozan"]);
    expect(groups[2]?.refs).toEqual(["mod:launch"]);
    expect(groups[4]?.refs).toContain("adapter:cloudflare");
  });

  it("every starter folder has a valid manifest and no dashes", () => {
    expect(problems).toEqual([]);
    expect(manifests.map((manifest) => manifest.name).sort()).toEqual(["desktop-app", "game", "mobile-app", "records-app", "web-app", "website"]);
    const scan = (dir: string): string[] =>
      readdirSync(dir).flatMap((name) => {
        const path = join(dir, name);
        if (name === "node_modules" || name === "dist" || name === "assets") return [];
        return statSync(path).isDirectory() ? scan(path) : [path];
      });
    for (const file of scan(startersDir())) expect(readFileSync(file, "utf8"), file).not.toMatch(DASHES);
  });
});

describe("instantiateBlueprint", () => {
  it("fills parameters, derives appSlug and leaves no placeholders", () => {
    const result = instantiateBlueprint(WEBSITE_BLUEPRINT, { appName: "Ink & Iron", contactEmail: "book@ink.example" });
    expect(result.ok).toBe(true);
    expect(result.values.appSlug).toBe("ink-iron");
    const text = JSON.stringify(result.blueprint?.stages);
    expect(text).toContain("Ink & Iron");
    expect(text).toContain("book@ink.example");
    expect(text).not.toContain("{{");
  });

  it("needs appName and refuses undeclared parameters", () => {
    const result = instantiateBlueprint(WEBSITE_BLUEPRINT, { favoriteColor: "red" });
    expect(result.ok).toBe(false);
    expect(result.issues.map((issue) => issue.field)).toEqual(expect.arrayContaining(["parameters.favoriteColor", "parameters.appName"]));
  });
});

describe("matching an idea", () => {
  const entries = entriesOf(STARTER_BLUEPRINTS);
  const top = (idea: string) => matchBlueprints(entries, { idea });

  it.each([
    ["a website for my tattoo shop", "blueprint:small-business-site"],
    ["a SaaS dashboard where my clients log in and track their projects", "blueprint:saas-web-app"],
    ["an iPhone and Android app to track my sourdough feedings", "blueprint:expo-mobile-app"],
    ["a browser arcade game with high scores", "blueprint:web-game"],
    ["a sales pipeline to follow up on leads", "blueprint:crm-pipeline"],
    ["inventory, purchase orders and invoicing for my bakery", "blueprint:erp-operations"],
    ["a desktop app for windows and mac that keeps my notes offline", "blueprint:desktop-app"],
  ])("%s matches %s", (idea, expected) => {
    const outcome = top(idea);
    expect(outcome.matches[0]?.blueprintId).toBe(expected);
    expect(outcome.matches[0]?.score).toBeGreaterThanOrEqual(MATCH_THRESHOLD);
    expect(outcome.needsCrafting).toBe(false);
    expect(outcome.matches[0]?.reason).not.toMatch(DASHES);
  });

  it("asks for crafting when nothing fits", () => {
    const outcome = top("turn my podcast episodes into a newsletter automatically");
    expect(outcome.needsCrafting).toBe(true);
    expect(inferKind("turn my podcast episodes into a newsletter automatically")).toBeUndefined();
  });

  it("uses a Seed's kind and platforms over the words", () => {
    const outcome = matchBlueprints(entries, { idea: "something for my shop", kind: "erp", platforms: ["web"] });
    expect(outcome.matches[0]?.blueprintId).toBe("blueprint:erp-operations");
    expect(outcome.inferred).toEqual({ kind: "erp", platforms: ["web"] });
  });

  it("leaves deprecated Blueprints out", () => {
    const deprecated = entriesOf([{ ...WEBSITE_BLUEPRINT, state: "deprecated" }]);
    expect(matchBlueprints(deprecated, { idea: "a website for my tattoo shop" }).matches).toEqual([]);
  });
});

describe("Prometheus's crafting method", () => {
  it("asks the name first and suggests answers from the idea", () => {
    const outcome = craftBlueprint({}, { idea: "I want to build a phone app that reminds tattoo clients about aftercare" });
    expect(outcome.complete).toBe(false);
    expect(outcome.next?.field).toBe("name");
    const kind = outcome.remaining.find((question) => question.field === "kind");
    expect(kind?.suggestion).toBe("mobile-app");
    expect(outcome.remaining.find((question) => question.field === "purpose")?.suggestion).toContain("reminds tattoo clients");
    expect(outcome.remaining.find((question) => question.field === "starter")?.suggestion).toBe(STARTER_FOR_KIND["mobile-app"]);
  });

  it("turns answers into a valid Blueprint with a .bamf plan whose register step waits for the user", () => {
    const outcome = craftBlueprint(TATTOO_ANSWERS, { now: new Date("2026-10-30T12:00:00Z") });
    expect(outcome.issues).toEqual([]);
    expect(outcome.complete).toBe(true);
    const blueprint = outcome.blueprint as Blueprint;
    expect(blueprint.blueprintId).toBe("blueprint:aftercare-tracker");
    expect(blueprint).toMatchObject({ kind: "mobile-app", platforms: ["ios", "android"], origin: "crafted", state: "draft", starter: "mobile-app" });
    expect(blueprintSchema.parse(blueprint)).toEqual(blueprint);
    const keys = blueprint.stages.flatMap((stage) => stage.layers.map((layer) => layer.key));
    expect(keys).toEqual(expect.arrayContaining(["access", "api", "integrations", "test-flight-client", "test-flight-artist", "go-live"]));
    expect(blueprint.suggestedConnections).toEqual(expect.arrayContaining(["mod:launch", "adapter:supabase", "app:ryozan"]));
    expect(blueprint.orchestration.map((job) => [job.key, job.agent, job.cadence])).toEqual([
      ["daily-care-reminders", "athena", "daily"],
      ["weekly-healing-report", "metis", "weekly"],
      ["crash-watch", "icarus", "continuous"],
    ]);
    const bamf = validateBamfDocument(outcome.bamf);
    expect(bamf.ok).toBe(true);
    expect(outcome.bamf[0]).toMatchObject({ action: "blueprint.craft", targetLayer: "atlas" });
    expect(outcome.bamf[1]).toMatchObject({ action: "blueprint.register", operatorGate: { required: true } });
    expect(outcome.steps.length).toBe(outcome.bamf.length - 2);
    expect(JSON.stringify(blueprint)).not.toMatch(DASHES);
  });

  it("reports a bad answer in plain words and holds the Blueprint back", () => {
    const outcome = craftBlueprint({ ...TATTOO_ANSWERS, kind: "spaceship", starter: "nope" });
    expect(outcome.complete).toBe(false);
    expect(outcome.blueprint).toBeUndefined();
    expect(outcome.issues.map((issue) => issue.field)).toEqual(["kind", "starter"]);
    expect(outcome.next?.field).toBe("kind");
  });

  it("refuses too-short answers field by field", () => {
    const outcome = craftBlueprint({ ...TATTOO_ANSWERS, name: "A", purpose: "short" });
    expect(outcome.issues.map((issue) => issue.field).sort()).toEqual(["name", "purpose"]);
  });

  it("splits list answers and finds a free id", () => {
    expect(splitList("- clients\n- tattoos, photos; clients")).toEqual(["clients", "tattoos", "photos"]);
    expect(freeBlueprintId("site", new Set(["blueprint:site", "blueprint:site-2"]))).toBe("blueprint:site-3");
    expect(craftBlueprint(TATTOO_ANSWERS, { takenIds: new Set(["blueprint:aftercare-tracker"]) }).blueprint?.blueprintId).toBe("blueprint:aftercare-tracker-2");
  });

  it("offers every question with the starters on disk", () => {
    const questions = craftQuestions({ starters: ["website", "game"] });
    expect(questions.map((question) => question.field)).toHaveLength(11);
    expect(questions.find((question) => question.field === "starter")?.options.map((option) => option.value)).toEqual(["website", "game", "none"]);
  });

  it("a website with bookings and no server stays serverless", () => {
    const blueprint = blueprintFromAnswers({
      name: "Barber site",
      kind: "website",
      platforms: ["web"],
      purpose: "A site where people see cuts and book a chair.",
      records: ["services", "gallery"],
      actions: ["see services", "book a chair"],
      roles: ["visitor"],
      integrations: [],
      starter: "website",
      afterLaunch: [],
      look: "",
    });
    const keys = blueprint.stages.flatMap((stage) => stage.layers.map((layer) => layer.key));
    expect(keys).not.toContain("api");
    expect(keys).not.toContain("access");
    expect(blueprint.suggestedConnections).toContain("app:ryozan");
  });
});

describe("the user's Atlas", () => {
  const crafted = () => craftBlueprint(TATTOO_ANSWERS).blueprint as Blueprint;

  it("starts with the starters, in order", async () => {
    const atlas = new AtlasRegistry(fresh("atlas"));
    const entries = await atlas.list();
    expect(entries.map((entry) => entry.blueprint.blueprintId)).toEqual(STARTER_BLUEPRINTS.map((blueprint) => blueprint.blueprintId));
    expect(entries.every((entry) => entry.source === "starter")).toBe(true);
    expect((await atlas.get("small-business-site"))?.blueprint.name).toBe("Small business website");
    expect((await atlas.get(encodeURIComponent("blueprint:web-game")))?.blueprint.kind).toBe("game");
  });

  it("registers a crafted Blueprint once, and replaces it only with a higher version", async () => {
    const atlas = new AtlasRegistry(fresh("atlas"));
    const entry = await atlas.register(crafted());
    expect(entry).toMatchObject({ source: "user", uses: 0 });
    expect((await atlas.list()).at(-1)?.blueprint.blueprintId).toBe("blueprint:aftercare-tracker");
    await expect(atlas.register(crafted())).rejects.toMatchObject({ code: "conflict" });
    await atlas.recordUse("blueprint:aftercare-tracker");
    const bumped = await atlas.register({ ...crafted(), version: "0.2.0" });
    expect(bumped.blueprint.version).toBe("0.2.0");
    expect(bumped.uses).toBe(1);
  });

  it("refuses starter ids, starter origin and invalid Blueprints in plain words", async () => {
    const atlas = new AtlasRegistry(fresh("atlas"));
    await expect(atlas.register({ ...crafted(), blueprintId: "blueprint:web-game" })).rejects.toMatchObject({ code: "conflict" });
    await expect(atlas.register({ ...crafted(), origin: "starter" })).rejects.toMatchObject({ code: "bad_request" });
    await expect(atlas.register({ name: "nothing" })).rejects.toBeInstanceOf(AtlasError);
    expect(() => normalizeBlueprintId("Not An Id!")).toThrow(AtlasError);
  });

  it("refreshes an older starter, keeps its uses, and reports broken files", async () => {
    const dir = fresh("atlas");
    const old = new AtlasRegistry(dir, { starters: [{ ...WEBSITE_BLUEPRINT, version: "0.9.0" }] });
    await old.list();
    await old.recordUse("blueprint:small-business-site");
    writeFileSync(join(dir, "broken.json"), "{ not json", "utf8");
    const current = new AtlasRegistry(dir, { starters: [WEBSITE_BLUEPRINT] });
    const { entries, problems } = await current.scan();
    expect(entries[0]?.blueprint.version).toBe(WEBSITE_BLUEPRINT.version);
    expect(entries[0]?.uses).toBe(1);
    expect(problems.map((problem) => problem.file)).toEqual(["broken.json"]);
  });

  it("matches against what is in the Atlas, crafted Blueprints included", async () => {
    const atlas = new AtlasRegistry(fresh("atlas"));
    await atlas.register(crafted());
    const outcome = await atlas.match({ idea: "an app for tattoo clients to track aftercare and healing photos", kind: "mobile-app" });
    expect(outcome.matches[0]?.blueprintId).toBe("blueprint:aftercare-tracker");
  });
});

describe("scaffolding a starter", () => {
  it("copies the website starter and sets the user's values safely", () => {
    const target = fresh("app");
    const result = scaffoldStarter({ starter: "website", target, values: { appName: `Ink & Iron's <Studio>`, contactEmail: "book@ink.example" } });
    expect(result.warnings).toEqual([]);
    expect(result.values.appSlug).toBe("ink-iron-s-studio");
    expect(result.files).toEqual(expect.arrayContaining(["package.json", "index.html", "src/site.json", "src/main.ts", ".gitignore"]));
    expect(result.files).not.toContain("starter.json");
    const pkg = JSON.parse(readFileSync(join(target, "package.json"), "utf8"));
    expect(pkg.name).toBe("ink-iron-s-studio");
    const site = JSON.parse(readFileSync(join(target, "src", "site.json"), "utf8"));
    expect(site).toMatchObject({ name: `Ink & Iron's <Studio>`, email: "book@ink.example", phone: "+1 555 0100" });
    const html = readFileSync(join(target, "index.html"), "utf8");
    expect(html).toContain("<title>Ink &amp; Iron&#39;s &lt;Studio&gt;</title>");
    expect(html).not.toContain("My Studio");
  });

  it.each(["web-app", "records-app", "game", "desktop-app", "mobile-app"])("scaffolds %s with no warnings", (starter) => {
    const target = fresh(starter);
    const result = scaffoldStarter({ starter, target, values: { appName: "North Star" } });
    expect(result.warnings).toEqual([]);
    expect(JSON.parse(readFileSync(join(target, "package.json"), "utf8")).name).toBe("north-star");
  });

  it("refuses a folder with files and an unknown starter", () => {
    const target = fresh("busy");
    mkdirSync(target, { recursive: true });
    writeFileSync(join(target, "keep.txt"), "mine", "utf8");
    expect(() => scaffoldStarter({ starter: "website", target })).toThrow(ScaffoldError);
    expect(() => scaffoldStarter({ starter: "spaceship", target: fresh("none") })).toThrow(/no starter called spaceship/);
  });
});
