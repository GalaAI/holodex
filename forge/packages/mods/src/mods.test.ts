import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { LAUNCH_MOD_ADAPTERS, LIBRARY_ORIGINS, adapterSchema, makeId } from "@4ge/contracts";
import { dataPaths, ensureDataDirs } from "@4ge/contracts/node";
import { afterAll, describe, expect, it } from "vitest";
import { ADAPTERS, findAdapter } from "./catalog/adapters.js";
import { BUILT_IN_AUGMENTS, type AugmentDefinition } from "./catalog/augments.js";
import type { CommandRunner, RunOptions, RunResult } from "./cli.js";
import { maskEmail, redactOutput } from "./cli.js";
import { MemoryCredentialStore, WindowsCredentialStore, assertWritableTarget } from "./credentials.js";
import { AdapterDetector, HOLODEX_NEBIUS_TARGET, adapterEnvironment, interpretProbe } from "./detect.js";
import { buildCommand, connectAdapter, disconnectAdapter, runAdapterAction } from "./actions.js";
import { checkSkillFolder, toMcpJson } from "./manifest.js";
import { ModService } from "./service.js";
import { addRepo, buildLibrary, removeRepo, repoIdentity } from "./library.js";

const root = mkdtempSync(join(tmpdir(), "4ge-mods-"));
afterAll(() => rmSync(root, { recursive: true, force: true }));

const DASHES = new RegExp(`[${String.fromCodePoint(0x2013)}${String.fromCodePoint(0x2014)}]`);

/** A runner that answers from a table and records what it was asked to run or open. */
class FakeRunner implements CommandRunner {
  readonly runs: Array<{ bin: string; args: string[]; options: RunOptions | undefined }> = [];
  readonly opened: Array<{ bin: string; args: string[]; options: RunOptions | undefined }> = [];
  constructor(
    private readonly installed: Set<string>,
    private readonly answers: Record<string, Partial<RunResult>> = {},
  ) {}
  which(bin: string): boolean {
    return this.installed.has(bin);
  }
  async run(bin: string, args: string[], options?: RunOptions): Promise<RunResult> {
    this.runs.push({ bin, args, options });
    const answer = this.answers[[bin, ...args].join(" ")] ?? this.answers[bin] ?? { exitCode: 1 };
    return { exitCode: 0, stdout: "", stderr: "", timedOut: false, ...answer };
  }
  async open(bin: string, args: string[], options?: RunOptions): Promise<void> {
    this.opened.push({ bin, args, options });
  }
}

const okFetch = (status = 200, body: unknown = { data: [{ id: "a" }, { id: "b" }] }) =>
  (async () => new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } })) as typeof fetch;

function detectorWith(runner: FakeRunner, credentials = new MemoryCredentialStore(), env: Record<string, string> = {}, fetcher = okFetch()) {
  return new AdapterDetector({ runner, credentials, env, fetch: fetcher }, 60_000);
}

describe("catalog", () => {
  it("covers the Launch Mod adapters plus Nebius and Hivemind, each with sign-up, docs and plain words", () => {
    const ids = ADAPTERS.map((adapter) => adapter.id);
    for (const id of LAUNCH_MOD_ADAPTERS) expect(ids).toContain(id);
    expect(ids).toContain("adapter:nebius");
    expect(ids).toContain("adapter:hivemind");
    for (const adapter of ADAPTERS) {
      expect(adapter.signUpUrl).toMatch(/^https:\/\//);
      expect(adapter.mcpNote.length).toBeGreaterThan(10);
      expect(JSON.stringify(adapter)).not.toMatch(DASHES);
      if (adapter.auth.method === "token") expect(adapter.auth.credentialTarget.startsWith("4GE:")).toBe(true);
      if (adapter.mcpAugment) expect(BUILT_IN_AUGMENTS.some((augment) => augment.id === adapter.mcpAugment)).toBe(true);
    }
  });

  it("gives every Launch Mod adapter a CLI, a sign-in and at least one action beyond it", () => {
    for (const id of LAUNCH_MOD_ADAPTERS) {
      const def = findAdapter(id);
      expect(def?.cli?.login).toBeTruthy();
      expect(def?.actions.some((action) => action.id === "sign-in")).toBe(true);
      expect(def?.actions.length).toBeGreaterThan(1);
    }
    const kinds = new Set(ADAPTERS.flatMap((adapter) => adapter.actions.map((action) => action.kind)));
    for (const kind of ["domain", "project", "credentials", "profile"]) expect(kinds).toContain(kind);
  });
});

describe("credentials", () => {
  it("only writes 4GE targets and never reads arbitrary ones", async () => {
    expect(() => assertWritableTarget("Omniwand:NebiusTokenFactory")).toThrow(/4GE:/);
    const store = new MemoryCredentialStore();
    await expect(store.write("4GE:adapter:nebius", "k")).resolves.toBeUndefined();
    await expect(store.read("Some:Other")).rejects.toThrow();
    await expect(store.read(HOLODEX_NEBIUS_TARGET)).resolves.toBeUndefined();
  });

  it.runIf(process.platform === "win32")("round-trips a 4GE target through Windows Credential Manager", { timeout: 90_000 }, async () => {
    const store = new WindowsCredentialStore();
    const target = `4GE:selftest:${Date.now()}`;
    await store.write(target, "not-a-secret-selftest");
    expect((await store.has([target]))[target]).toBe(true);
    expect(await store.read(target)).toBe("not-a-secret-selftest");
    expect(await store.delete(target)).toBe(true);
    expect((await store.has([target]))[target]).toBe(false);
  });
});

describe("probes", () => {
  const result = (stdout: string, exitCode = 0, stderr = ""): RunResult => ({ stdout, stderr, exitCode, timedOut: false });

  it("reads each CLI's own status output", () => {
    expect(interpretProbe("cloudflare", result('{"loggedIn":true,"accounts":[{"name":"jo@example.com\'s Account"}]}'))).toMatchObject({
      status: "connected",
      account: "j***@example.com's Account",
    });
    expect(interpretProbe("cloudflare", result('{"loggedIn":false}')).status).toBe("disconnected");
    expect(interpretProbe("railway", result('{"name":"Jo","email":"jo@x.io"}'))).toMatchObject({ status: "connected", account: "Jo" });
    expect(interpretProbe("railway", result("", 1, "Unauthorized. Please login")).status).toBe("disconnected");
    expect(interpretProbe("vercel", result('{"username":"jo","team":{"name":"Ink"}}')).account).toBe("jo (Ink)");
    expect(interpretProbe("gcp", result("jo@gmail.com\n")).account).toBe("j***@gmail.com");
    expect(interpretProbe("gcp", result("")).status).toBe("disconnected");
    expect(interpretProbe("tailscale", result('{"BackendState":"Running","CurrentTailnet":{"Name":"ink.github"}}'))).toMatchObject({
      status: "connected",
      account: "ink.github",
    });
    expect(interpretProbe("tailscale", result('{"BackendState":"NeedsLogin"}')).status).toBe("disconnected");
    expect(interpretProbe("docker", result("29.0.1")).status).toBe("connected");
    expect(interpretProbe("docker", result("", 1, "error during connect: open //./pipe/dockerDesktopLinuxEngine")).message).toMatch(/not running/);
    expect(interpretProbe("supabase", result("", 1, "Access token not provided. Supply an access token by running supabase login")).status).toBe(
      "disconnected",
    );
    expect(interpretProbe("vercel", { ...result(""), timedOut: true }).status).toBe("error");
  });

  it("reports a missing CLI as needs-cli with the install command, and validates against the contract", async () => {
    const detector = detectorWith(new FakeRunner(new Set()));
    const adapters = await detector.list();
    const vercel = adapters.find((adapter) => adapter.id === "adapter:vercel");
    expect(vercel?.status).toBe("needs-cli");
    expect(vercel?.statusMessage).toContain("npm i -g vercel");
    expect(vercel?.cli?.installed).toBe(false);
    for (const adapter of adapters) expect(adapterSchema.safeParse(adapter).success).toBe(true);
  });

  it("checks a Nebius key live and calls a rejected key expired", async () => {
    const credentials = new MemoryCredentialStore({ "4GE:adapter:nebius": "nb-test-key" });
    const good = await detectorWith(new FakeRunner(new Set()), credentials).get("adapter:nebius");
    expect(good).toMatchObject({ status: "connected", account: "2 models" });
    const bad = await detectorWith(new FakeRunner(new Set()), credentials, {}, okFetch(401, {})).get("adapter:nebius");
    expect(bad?.status).toBe("expired");
    const none = await detectorWith(new FakeRunner(new Set())).get("adapter:nebius");
    expect(none?.status).toBe("disconnected");
  });

  it("hands token adapter keys to the orchestra as env vars without a file", async () => {
    const env = await adapterEnvironment({ env: {}, credentials: new MemoryCredentialStore({ "4GE:adapter:hivemind": "hm" }) });
    expect(env).toMatchObject({ HIVEMIND_TOKEN: "hm" });
    expect(env.HIVEMIND_URL).toMatch(/^https:\/\//);
  });
});

describe("connect and actions", () => {
  it("stores a pasted token in the credential store and confirms it live", async () => {
    const credentials = new MemoryCredentialStore();
    const result = await connectAdapter(detectorWith(new FakeRunner(new Set()), credentials), "adapter:nebius", "nb-new");
    expect(result.action.kind).toBe("done");
    expect(await credentials.read("4GE:adapter:nebius")).toBe("nb-new");
    expect(JSON.stringify(result)).not.toContain("nb-new");
  });

  it("reuses the key Holodex keeps instead of asking again", async () => {
    const credentials = new MemoryCredentialStore({ [HOLODEX_NEBIUS_TARGET]: "holodex-key" });
    const result = await connectAdapter(detectorWith(new FakeRunner(new Set()), credentials), "adapter:nebius");
    expect(result.adapter.status).toBe("connected");
    expect(await credentials.read("4GE:adapter:nebius")).toBe("holodex-key");
  });

  it("opens the provider's own login for a CLI adapter, or says to install it", async () => {
    const runner = new FakeRunner(new Set(["vercel"]), { "vercel whoami --format json": { exitCode: 1, stderr: "Error: not logged in" } });
    const result = await connectAdapter(detectorWith(runner), "adapter:vercel");
    expect(result.action).toMatchObject({ kind: "run-cli", command: "vercel login" });
    expect(runner.opened).toEqual([{ bin: "vercel", args: ["login"], options: undefined }]);
    const missing = await connectAdapter(detectorWith(new FakeRunner(new Set())), "adapter:railway");
    expect(missing.action.kind).toBe("install-cli");
    const out = await disconnectAdapter(detectorWith(new FakeRunner(new Set())), "adapter:railway");
    expect(out.statusMessage).toMatch(/stores nothing/);
  });

  it("refuses parameters a CLI should never see and drops empty optional ones", () => {
    const vercel = findAdapter("adapter:vercel");
    const buy = vercel?.actions.find((action) => action.id === "buy-domain");
    expect(buy && buildCommand(buy, { domain: "ink.com; del /s" })).toMatchObject({ ok: false });
    expect(buy && buildCommand(buy, {})).toMatchObject({ ok: false });
    expect(buy && buildCommand(buy, { domain: "ink-shop.com" })).toEqual({ ok: true, argv: ["vercel", "domains", "buy", "ink-shop.com"] });
    const domain = findAdapter("adapter:railway")?.actions.find((action) => action.id === "attach-domain");
    expect(domain && buildCommand(domain, {})).toEqual({ ok: true, argv: ["railway", "domain"] });
    const sa = findAdapter("adapter:gcp")?.actions.find((action) => action.id === "create-service-account");
    expect(sa && buildCommand(sa, { name: "deployer", projectId: "ink-app-4ge" })).toEqual({
      ok: true,
      argv: ["gcloud", "iam", "service-accounts", "create", "deployer", "--project=ink-app-4ge"],
    });
  });

  it("needs confirm before an action that spends, then opens the CLI to ask again", async () => {
    const runner = new FakeRunner(new Set(["vercel"]));
    const detector = detectorWith(runner);
    const refused = await runAdapterAction(detector, "adapter:vercel", "buy-domain", { params: { domain: "ink-shop.com" } });
    expect(refused.status).toBe("refused");
    expect(runner.opened).toHaveLength(0);
    const opened = await runAdapterAction(detector, "adapter:vercel", "buy-domain", { params: { domain: "ink-shop.com" }, confirm: true });
    expect(opened.status).toBe("opened");
    expect(runner.opened[0]).toMatchObject({ bin: "vercel", args: ["domains", "buy", "ink-shop.com"] });
  });

  it("runs a background action and redacts its output", async () => {
    const runner = new FakeRunner(new Set(["vercel"]), {
      "vercel project add ink": { exitCode: 0, stdout: "Success! Project ink added for jo@example.com token=abc123secretvalue" },
    });
    const run = await runAdapterAction(detectorWith(runner), "adapter:vercel", "create-project", { params: { name: "ink" } });
    expect(run.status).toBe("done");
    expect(run.output).toContain("j***@example.com");
    expect(run.output).not.toContain("abc123secretvalue");
    const missing = await runAdapterAction(detectorWith(new FakeRunner(new Set())), "adapter:vercel", "create-project", { params: { name: "ink" } });
    expect(missing.status).toBe("failed");
    expect(missing.message).toMatch(/not installed/);
  });

  it("masks emails and known token shapes", () => {
    expect(maskEmail("jo@example.com")).toBe("j***@example.com");
    expect(redactOutput("Authorization: Bearer abcdefghijklmnop")).not.toContain("abcdefghijklmnop");
  });
});

describe("manifests and skills", () => {
  it("ships skills that follow the Agent Skills specification", () => {
    for (const name of ["launch-mod", "dev-mod", "pc-mod"]) {
      const check = checkSkillFolder(fileURLToPath(new URL(`../skills/${name}`, import.meta.url)));
      expect(check.problems).toEqual([]);
      expect(check.name).toBe(name);
    }
    const bad = join(root, "skills", "Bad_Skill");
    mkdirSync(bad, { recursive: true });
    writeFileSync(join(bad, "SKILL.md"), "---\nname: Bad_Skill\n---\nbody\n");
    const check = checkSkillFolder(bad);
    expect(check.ok).toBe(false);
    expect(check.problems.join(" ")).toMatch(/description is required/);
  });

  it("exports MCP servers in the .mcp.json shape", () => {
    const json = toMcpJson([
      { ...(BUILT_IN_AUGMENTS.find((augment) => augment.id === "augment:vercel-mcp") as AugmentDefinition), status: "ready" },
      { ...(BUILT_IN_AUGMENTS.find((augment) => augment.id === "augment:railway-mcp") as AugmentDefinition), status: "ready" },
      {
        id: "augment:keyed",
        name: "Keyed",
        kind: "mcp-server",
        description: "x",
        mcp: { transport: "stdio", command: "node", args: ["s.js"], envKeys: ["API_TOKEN"] },
        requires: [],
        enabled: true,
        status: "ready",
      },
    ]);
    expect(json.mcpServers.vercel).toEqual({ type: "http", url: "https://mcp.vercel.com" });
    expect(json.mcpServers.railway).toEqual({ command: "railway", args: ["mcp"] });
    expect(json.mcpServers["augment:keyed".replace("augment:", "")]).toEqual({ command: "node", args: ["s.js"], env: { API_TOKEN: "${API_TOKEN}" } });
  });
});

describe("mods service and library", () => {
  const paths = ensureDataDirs(dataPaths(join(root, "data")));
  const runner = new FakeRunner(new Set(["vercel", "railway", "docker"]), {
    "vercel whoami --format json": { stdout: '{"username":"jo"}' },
    "railway whoami --json": { exitCode: 1, stderr: "Unauthorized" },
    "docker info --format {{.ServerVersion}}": { stdout: "29.0.1" },
  });
  const detector = detectorWith(runner);
  const service = new ModService(paths, detector, () => new Date("2026-10-30T12:00:00Z"));

  it("lists Dev Mod, PC Mod and Launch Mod with status from live detection", async () => {
    const mods = await service.listMods();
    expect(mods.map((mod) => mod.id)).toEqual(["mod:dev", "mod:pc", "mod:launch"]);
    const launch = mods.find((mod) => mod.id === "mod:launch");
    expect(launch?.adapters).toHaveLength(8);
    expect(launch?.status).toBe("needs-setup");
    expect(launch?.statusMessage).toBe("2 of 8 adapters connected.");
    const augments = await service.listAugments();
    expect(augments.find((augment) => augment.id === "augment:launch-mod-skill")?.status).toBe("ready");
    expect(augments.find((augment) => augment.id === "augment:vercel-mcp")?.status).toBe("ready");
    expect(augments.find((augment) => augment.id === "augment:railway-mcp")?.status).toBe("needs-setup");
  });

  it("creates a mod folder from adapters and augments (the plus tile)", async () => {
    const { mod, folder } = await service.createMod({
      name: "Ink Shop Launch",
      description: "Puts the tattoo shop site on Vercel with Supabase bookings.",
      adapters: ["adapter:vercel"],
      augments: ["augment:supabase-mcp", "augment:launch-mod-skill"],
    });
    expect(mod).toMatchObject({ id: "mod:ink-shop-launch", kind: "user", builtIn: false });
    expect(mod.adapters).toEqual(["adapter:vercel", "adapter:supabase"]);
    const manifest = JSON.parse(readFileSync(join(folder, "mod.json"), "utf8"));
    expect(manifest.manifestVersion).toBe(1);
    expect(JSON.parse(readFileSync(join(folder, ".mcp.json"), "utf8")).mcpServers.supabase.url).toBe("https://mcp.supabase.com/mcp");
    expect(JSON.parse(readFileSync(join(folder, ".claude-plugin", "plugin.json"), "utf8")).name).toBe("4ge-ink-shop-launch");
    expect(existsSync(join(folder, "skills", "launch-mod", "SKILL.md"))).toBe(true);
    await expect(service.createMod({ name: "Ink Shop Launch", description: "again", adapters: [], augments: [] })).rejects.toThrow(/already exists/);
    await expect(service.createMod({ name: "Nope", description: "x", adapters: ["adapter:nowhere"], augments: [] })).rejects.toThrow(/no adapter/);
  });

  it("adds a skill augment only when its SKILL.md checks out", async () => {
    const folder = join(paths.mods, "skills", "ink-care");
    mkdirSync(folder, { recursive: true });
    writeFileSync(join(folder, "SKILL.md"), "---\nname: ink-care\ndescription: Aftercare notes for tattoo clients. Use when writing aftercare pages.\n---\n# Aftercare\n");
    const augment = await service.createAugment({
      id: "augment:ink-care",
      name: "Ink care",
      kind: "skill",
      description: "Aftercare writing skill",
      skillPath: "skills/ink-care",
      requires: [],
      enabled: true,
    });
    expect(augment.status).toBe("ready");
    await expect(
      service.createAugment({ id: "augment:escape", name: "x", kind: "skill", description: "x", skillPath: "../../etc", requires: [], enabled: true }),
    ).rejects.toThrow(/inside the mods folder/);
  });

  it("groups the Library by origin with My projects, Repos, Mods, Augments and Adapters", async () => {
    const buildId = makeId("build", "ink1");
    const build = paths.build(buildId);
    mkdirSync(build.root, { recursive: true });
    writeFileSync(
      build.record,
      JSON.stringify({
        id: buildId,
        seedId: makeId("seed", "ink1"),
        blueprintId: "blueprint:website",
        title: "Ink Shop site",
        status: "running",
        stages: [],
        layers: [],
        sprints: [],
        createdAt: "2026-10-30T12:00:00.000Z",
        updatedAt: "2026-10-30T12:00:00.000Z",
      }),
    );
    const repo = addRepo(paths, { url: "https://github.com/GalaAI/holodex" });
    expect(repo).toMatchObject({ id: "repo:galaai-holodex", title: "GalaAI/holodex", origin: "repo" });
    expect(() => addRepo(paths, { url: "https://github.com/GalaAI/holodex" })).toThrow(/already/);
    expect(repoIdentity("ssh://git@github.com/a/b.git").title).toBe("a/b");

    const adapters = await detector.list();
    const library = buildLibrary({
      paths,
      env: {},
      mods: await service.listMods(),
      augments: await service.listAugments(adapters),
      adapters,
    });
    expect(library.groups.map((group) => group.origin)).toEqual([...LIBRARY_ORIGINS]);
    const projects = library.groups[0]?.items ?? [];
    expect(projects.map((item) => item.title)).toEqual(["Ink Shop site", "Ryozan"]);
    expect(projects[0]).toMatchObject({ status: "Building", tone: "live" });
    expect(library.groups[2]?.canCreate).toBe(true);
    expect(library.groups[2]?.items.map((item) => item.id)).toContain("mod:ink-shop-launch");
    expect(library.groups[4]?.items).toHaveLength(ADAPTERS.length);
    for (const group of library.groups) for (const item of group.items) expect(item.open.agent).toBe("smith");
    expect(JSON.stringify(library)).not.toMatch(DASHES);

    expect(removeRepo(paths, "repo:galaai-holodex")).toBe(true);
    expect(removeRepo(paths, "repo:galaai-holodex")).toBe(false);
  });
});
