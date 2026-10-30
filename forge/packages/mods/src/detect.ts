import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { NEBIUS_BASE_URL, adapterSchema, type Adapter, type ConnectionStatus } from "@4ge/contracts";
import { ADAPTERS, type AdapterDefinition } from "./catalog/adapters.js";
import { maskEmail, redactOutput, type CommandRunner, type RunResult } from "./cli.js";
import { adapterCredentialTarget, type CredentialStore } from "./credentials.js";

/**
 * Detection: is each provider's CLI installed on this PC, and is it signed
 * in? Every probe is a read-only status command from the provider's own
 * docs (whoami, auth list, status). Probes run in parallel with a timeout,
 * and results are cached briefly so the Library does not re-run them on
 * every view. Only names and states leave this module: account labels are
 * team or user names, and emails are masked.
 */

export const DEFAULT_HIVEMIND_URL = "https://hivemind-public-production.up.railway.app/mcp";
export const HOLODEX_NEBIUS_TARGET = "Omniwand:NebiusTokenFactory";

export interface DetectDeps {
  runner: CommandRunner;
  credentials: CredentialStore;
  env: Readonly<Record<string, string | undefined>>;
  fetch?: typeof fetch;
  now?: () => Date;
  probeTimeoutMs?: number;
}

interface Probe {
  status: ConnectionStatus;
  account?: string;
  message: string;
}

function parseJson(text: string): unknown {
  const start = text.search(/[[{]/);
  if (start < 0) return undefined;
  try {
    return JSON.parse(text.slice(start));
  } catch {
    return undefined;
  }
}

function label(value: unknown): string | undefined {
  if (typeof value !== "string" || value.trim().length === 0) return undefined;
  return redactOutput(value.trim()).slice(0, 200);
}

const NOT_RUNNING = /cannot connect|error during connect|is the docker daemon running|pipe\/docker|The system cannot find the file specified/i;

/** Read the probe output for each CLI. Exported for tests. */
export function interpretProbe(slug: string, result: RunResult): Probe {
  const out = `${result.stdout}\n${result.stderr}`;
  if (result.timedOut) return { status: "error", message: "The CLI did not answer in time. Try again." };
  switch (slug) {
    case "northflank": {
      if (result.exitCode === 0 && result.stdout.trim().length > 0 && !/no contexts?/i.test(out)) {
        return { status: "connected", message: "Signed in: the Northflank CLI has a saved context." };
      }
      return { status: "disconnected", message: "The Northflank CLI has no signed-in context." };
    }
    case "cloudflare": {
      const data = parseJson(result.stdout) as { loggedIn?: boolean; accounts?: Array<{ name?: string }> } | undefined;
      if (data?.loggedIn === true) {
        const account = label(data.accounts?.[0]?.name);
        return { status: "connected", ...(account ? { account } : {}), message: "Signed in with Wrangler." };
      }
      return { status: "disconnected", message: "Wrangler is not signed in." };
    }
    case "gcp": {
      const active = result.stdout.split(/\r?\n/).map((line) => line.trim()).find(Boolean);
      if (result.exitCode === 0 && active) {
        return { status: "connected", account: active.includes("@") ? maskEmail(active) : active, message: "Signed in with gcloud." };
      }
      return { status: "disconnected", message: "gcloud has no active account." };
    }
    case "railway": {
      const data = parseJson(result.stdout) as { name?: string } | undefined;
      if (result.exitCode === 0 && data) {
        const account = label(data.name);
        return { status: "connected", ...(account ? { account } : {}), message: "Signed in with the Railway CLI." };
      }
      return { status: "disconnected", message: "The Railway CLI is not signed in." };
    }
    case "vercel": {
      const data = parseJson(result.stdout) as { username?: string; team?: { name?: string; slug?: string } | null } | undefined;
      if (result.exitCode === 0 && data?.username) {
        const team = label(data.team?.name ?? data.team?.slug);
        const account = label(data.username);
        return {
          status: "connected",
          ...(account ? { account: team ? `${account} (${team})` : account } : {}),
          message: "Signed in with the Vercel CLI.",
        };
      }
      return { status: "disconnected", message: "The Vercel CLI is not signed in." };
    }
    case "supabase": {
      if (result.exitCode === 0) return { status: "connected", message: "Signed in with the Supabase CLI." };
      if (/access token|supabase login/i.test(out)) return { status: "disconnected", message: "The Supabase CLI is not signed in." };
      return { status: "error", message: "The Supabase CLI could not list projects." };
    }
    case "docker": {
      if (result.exitCode === 0 && result.stdout.trim().length > 0) {
        return { status: "connected", message: `Docker Engine ${label(result.stdout.trim()) ?? ""} is running.`.replace("  ", " ") };
      }
      if (NOT_RUNNING.test(out)) return { status: "error", message: "Docker is installed but not running. Start Docker Desktop." };
      return { status: "error", message: "Docker did not answer." };
    }
    case "tailscale": {
      const data = parseJson(result.stdout) as { BackendState?: string; CurrentTailnet?: { Name?: string } | null } | undefined;
      const state = data?.BackendState;
      if (state === "Running") {
        const account = label(data?.CurrentTailnet?.Name);
        return { status: "connected", ...(account ? { account } : {}), message: "Signed in and connected to your tailnet." };
      }
      if (state === "Stopped") return { status: "disconnected", message: "Tailscale is signed in but turned off." };
      if (state === "NeedsLogin" || state === "NoState") return { status: "disconnected", message: "Tailscale is not signed in." };
      return { status: "error", message: "The Tailscale service did not answer." };
    }
    default:
      return result.exitCode === 0
        ? { status: "connected", message: "Signed in." }
        : { status: "disconnected", message: "Not signed in." };
  }
}

/** Docker Hub sign-in, read from Docker's own credential helper (usernames only). */
async function dockerHubAccount(deps: DetectDeps): Promise<string | undefined> {
  const config = join(deps.env.USERPROFILE ?? deps.env.HOME ?? homedir(), ".docker", "config.json");
  if (!existsSync(config)) return undefined;
  let store: string | undefined;
  try {
    store = (JSON.parse(readFileSync(config, "utf8")) as { credsStore?: string }).credsStore;
  } catch {
    return undefined;
  }
  if (!store || !/^[a-z0-9-]+$/.test(store)) return undefined;
  const helper = `docker-credential-${store}`;
  if (!deps.runner.which(helper)) return undefined;
  const listed = await deps.runner.run(helper, ["list"], { timeoutMs: 8000 });
  const data = parseJson(listed.stdout) as Record<string, string> | undefined;
  const hub = data?.["https://index.docker.io/v1/"];
  return typeof hub === "string" && hub.length > 0 ? label(hub) : undefined;
}

type TokenSource = "env" | "store" | "holodex";

/** The secret for a token adapter and where it came from, for in-process use only. */
async function findAdapterToken(
  def: AdapterDefinition,
  deps: Pick<DetectDeps, "env" | "credentials">,
): Promise<{ value: string; source: TokenSource } | undefined> {
  if (def.auth.method !== "token") return undefined;
  const fromEnv = deps.env[def.auth.env]?.trim();
  if (fromEnv) return { value: fromEnv, source: "env" };
  const stored = await deps.credentials.read(adapterCredentialTarget(def.slug)).catch(() => undefined);
  if (stored) return { value: stored, source: "store" };
  if (def.slug === "nebius") {
    const holodex = await deps.credentials.read(HOLODEX_NEBIUS_TARGET).catch(() => undefined);
    if (holodex) return { value: holodex, source: "holodex" };
  }
  return undefined;
}

/** The secret for a token adapter, for in-process use (an env var for a CLI, an HTTP header). */
export async function readAdapterToken(def: AdapterDefinition, deps: Pick<DetectDeps, "env" | "credentials">): Promise<string | undefined> {
  return (await findAdapterToken(def, deps))?.value;
}

/**
 * Environment variables for the token adapters that hold a credential, so
 * the orchestra and the CLIs can use them without a file. Values never leave
 * the process.
 */
export async function adapterEnvironment(deps: Pick<DetectDeps, "env" | "credentials">): Promise<Record<string, string>> {
  const out: Record<string, string> = {};
  for (const def of ADAPTERS) {
    if (def.auth.method !== "token") continue;
    const value = await readAdapterToken(def, deps);
    if (value) out[def.auth.env] = value;
  }
  if (!deps.env.HIVEMIND_URL && out.HIVEMIND_TOKEN) out.HIVEMIND_URL = DEFAULT_HIVEMIND_URL;
  return out;
}

const SOURCE_WORDS = { env: "the 4GE server's environment", store: "Windows Credential Manager", holodex: "Holodex's vault" } as const;

async function probeToken(def: AdapterDefinition, deps: DetectDeps): Promise<Probe> {
  const found = await findAdapterToken(def, deps);
  if (!found) {
    return { status: "disconnected", message: `No ${def.name} key yet. Create one, then connect it here.` };
  }
  const { value: token, source } = found;
  const doFetch = deps.fetch ?? fetch;
  const timeout = AbortSignal.timeout(deps.probeTimeoutMs ?? 10_000);
  if (def.slug === "nebius") {
    try {
      const response = await doFetch(`${NEBIUS_BASE_URL}/models`, {
        headers: { authorization: `Bearer ${token}` },
        signal: timeout,
      });
      if (response.status === 401 || response.status === 403) {
        return { status: "expired", message: `Nebius rejected the key in ${SOURCE_WORDS[source]}. Make a new one and connect it.` };
      }
      if (!response.ok) return { status: "error", message: `Nebius answered ${response.status}. Try again later.` };
      const body = (await response.json().catch(() => ({}))) as { data?: unknown[] };
      const count = Array.isArray(body.data) ? body.data.length : 0;
      return { status: "connected", account: `${count} models`, message: `Key from ${SOURCE_WORDS[source]} works.` };
    } catch {
      return { status: "error", message: "Nebius did not answer. Check your connection." };
    }
  }
  if (def.slug === "hivemind") {
    const url = deps.env.HIVEMIND_URL?.trim() || DEFAULT_HIVEMIND_URL;
    let origin: string;
    try {
      origin = new URL(url).origin;
    } catch {
      return { status: "error", message: "HIVEMIND_URL is not a valid address." };
    }
    try {
      const response = await doFetch(`${origin}/health`, { signal: timeout });
      if (!response.ok) return { status: "error", message: `Your Hivemind answered ${response.status}.` };
      return { status: "connected", account: new URL(origin).host, message: `Key from ${SOURCE_WORDS[source]}; Hivemind is reachable.` };
    } catch {
      return { status: "error", message: "Your Hivemind did not answer. Check HIVEMIND_URL." };
    }
  }
  return { status: "connected", message: `Key from ${SOURCE_WORDS[source]}.` };
}

export async function detectAdapter(def: AdapterDefinition, deps: DetectDeps): Promise<Adapter> {
  const now = (deps.now ?? (() => new Date()))().toISOString();
  const installed = def.cli ? deps.runner.which(def.cli.bin) : false;
  let probe: Probe;
  if (def.auth.method === "token") {
    probe = await probeToken(def, deps);
  } else if (def.cli && !installed) {
    probe = { status: "needs-cli", message: `The ${def.cli.bin} CLI is not installed. Install it with: ${def.cli.install}` };
  } else if (def.cli) {
    try {
      const result = await deps.runner.run(def.cli.bin, def.cli.statusArgs, { timeoutMs: deps.probeTimeoutMs ?? 45_000 });
      probe = interpretProbe(def.slug, result);
      if (def.slug === "docker" && probe.status === "connected") {
        const hub = await dockerHubAccount(deps).catch(() => undefined);
        probe = hub
          ? { ...probe, account: hub, message: `${probe.message} Signed in to Docker Hub.` }
          : { ...probe, message: `${probe.message} Not signed in to Docker Hub; local builds still work.` };
      }
    } catch {
      probe = { status: "error", message: `The ${def.cli.bin} CLI could not be run.` };
    }
  } else {
    probe = { status: "disconnected", message: "Not connected." };
  }
  return {
    id: def.id,
    name: def.name,
    provider: def.provider,
    category: def.category,
    description: def.description,
    auth: def.auth,
    signUpUrl: def.signUpUrl,
    docsUrl: def.docsUrl,
    status: probe.status,
    ...(probe.account ? { account: probe.account } : {}),
    statusMessage: probe.message.slice(0, 600),
    lastCheckedAt: now,
    ...(def.cli
      ? { cli: { bin: def.cli.bin, install: def.cli.install, installUrl: def.cli.installUrl, login: def.cli.login, installed } }
      : {}),
    ...(def.mcpAugment ? { mcpAugment: def.mcpAugment } : {}),
    mcpNote: def.mcpNote,
    actions: def.actions,
  };
}

/**
 * Cached, parallel detection of every adapter. Some CLIs take many seconds
 * to answer (gcloud took 24 s on gala), so the last result is also kept in
 * a small file (names, states and account labels only, never a secret).
 * `list()` answers from a fresh cache; `snapshot()` answers at once from any
 * cache, stale or saved, and refreshes in the background.
 */
export class AdapterDetector {
  private cache: { at: number; adapters: Adapter[] } | undefined;
  private inflight: Promise<Adapter[]> | undefined;

  constructor(
    private readonly deps: DetectDeps,
    private readonly ttlMs = 60_000,
    private readonly catalog: AdapterDefinition[] = ADAPTERS,
    private readonly cacheFile?: string,
  ) {
    this.cache = this.loadSaved();
  }

  private loadSaved(): { at: number; adapters: Adapter[] } | undefined {
    if (!this.cacheFile || !existsSync(this.cacheFile)) return undefined;
    try {
      const saved = JSON.parse(readFileSync(this.cacheFile, "utf8")) as { adapters?: unknown[] };
      const adapters = (saved.adapters ?? []).map((item) => adapterSchema.parse(item));
      const ids = new Set(adapters.map((adapter) => adapter.id));
      if (!this.catalog.every((def) => ids.has(def.id))) return undefined;
      // Saved results count as stale: they show at once and refresh behind.
      return { at: 0, adapters: this.catalog.map((def) => adapters.find((adapter) => adapter.id === def.id) as Adapter) };
    } catch {
      return undefined;
    }
  }

  private save(adapters: Adapter[]): void {
    if (!this.cacheFile) return;
    try {
      mkdirSync(dirname(this.cacheFile), { recursive: true });
      writeFileSync(this.cacheFile, `${JSON.stringify({ savedAt: new Date().toISOString(), adapters }, null, 2)}
`, "utf8");
    } catch {
      // The cache is a convenience; detection still answers without it.
    }
  }

  private refresh(): Promise<Adapter[]> {
    if (this.inflight) return this.inflight;
    this.inflight = Promise.all(this.catalog.map((def) => detectAdapter(def, this.deps)))
      .then((adapters) => {
        this.cache = { at: Date.now(), adapters };
        this.save(adapters);
        return adapters;
      })
      .finally(() => {
        this.inflight = undefined;
      });
    return this.inflight;
  }

  async list(refresh = false): Promise<Adapter[]> {
    if (!refresh && this.cache && Date.now() - this.cache.at < this.ttlMs) return this.cache.adapters;
    return this.refresh();
  }

  /** Answer at once from any cache (refreshing behind it when stale); wait only when there is none. */
  async snapshot(): Promise<Adapter[]> {
    if (!this.cache) return this.refresh();
    if (Date.now() - this.cache.at >= this.ttlMs) void this.refresh().catch(() => undefined);
    return this.cache.adapters;
  }

  async get(id: string, refresh = false): Promise<Adapter | undefined> {
    const def = this.catalog.find((entry) => entry.id === id);
    if (!def) return undefined;
    if (refresh) {
      const fresh = await detectAdapter(def, this.deps);
      if (this.cache) {
        this.cache.adapters = this.cache.adapters.map((item) => (item.id === id ? fresh : item));
        this.save(this.cache.adapters);
      }
      return fresh;
    }
    return (await this.list()).find((adapter) => adapter.id === id);
  }

  invalidate(): void {
    if (this.cache) this.cache.at = 0;
  }

  get dependencies(): DetectDeps {
    return this.deps;
  }
}
