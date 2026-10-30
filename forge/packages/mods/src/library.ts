import { existsSync, readdirSync, readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { z } from "zod";
import {
  buildSchema,
  groupLibrary,
  isoDateTimeSchema,
  libraryItemSchema,
  type Adapter,
  type Augment,
  type Build,
  type LibraryItem,
  type LibraryRepoDraft,
  type LibraryResponse,
  type Mod,
} from "@4ge/contracts";
import type { DataPaths } from "@4ge/contracts/node";

/**
 * The Holodex Library, grouped by origin (SPEC, operator rulings
 * 2026-10-30): My projects (4GE builds and Ryozan), Repos (shortcuts the
 * user adds), Mods, Augments and Adapters. Every item's `open` names the
 * Smith session a click starts.
 */

export const RYOZAN_DEFAULT_URL = "https://ryozan-production.up.railway.app";

const repoRecordSchema = z.object({
  id: z.string().regex(/^repo:[a-z0-9][a-z0-9._-]*$/),
  url: z.string().url(),
  title: z.string().min(1).max(160),
  addedAt: isoDateTimeSchema,
});
type RepoRecord = z.infer<typeof repoRecordSchema>;

export class LibraryError extends Error {
  override readonly name = "LibraryError";
  constructor(
    readonly code: "not_found" | "conflict",
    message: string,
  ) {
    super(message);
  }
}

const BUILD_TONE: Record<Build["status"], LibraryItem["tone"]> = {
  queued: "warn",
  running: "live",
  "awaiting-checkpoint": "warn",
  "test-flight": "live",
  passed: "ok",
  failed: "err",
  cancelled: "rest",
};

const BUILD_WORD: Record<Build["status"], string> = {
  queued: "Queued",
  running: "Building",
  "awaiting-checkpoint": "Waiting for you",
  "test-flight": "Test flight",
  passed: "Ready",
  failed: "Failed",
  cancelled: "Cancelled",
};

export function readBuilds(paths: DataPaths): Build[] {
  if (!existsSync(paths.builds)) return [];
  const out: Build[] = [];
  for (const id of readdirSync(paths.builds)) {
    const file = join(paths.builds, id, "build.json");
    if (!existsSync(file)) continue;
    try {
      const parsed = buildSchema.safeParse(JSON.parse(readFileSync(file, "utf8")));
      if (parsed.success) out.push(parsed.data);
    } catch {
      // A half-written build.json is skipped; the build shows once it is whole.
    }
  }
  return out;
}

function projectItems(paths: DataPaths, env: Readonly<Record<string, string | undefined>>): LibraryItem[] {
  const builds = readBuilds(paths).map(
    (build): LibraryItem => ({
      id: `project:${build.id}`,
      origin: "project",
      title: build.title,
      subtitle: `4GE build, ${build.layers.length} layer${build.layers.length === 1 ? "" : "s"}`,
      status: BUILD_WORD[build.status],
      tone: BUILD_TONE[build.status],
      icon: "Hammer",
      ...(build.previewUrl ? { url: build.previewUrl } : {}),
      open: {
        agent: "smith",
        kind: "build",
        task: `Open ${build.title} with me: where the build stands and what to do next.`,
        subject: { type: "build", id: build.id, label: build.title },
      },
      updatedAt: build.updatedAt,
    }),
  );
  const ryozanUrl = env.OMNIWAND_RYOZAN_URL?.trim() || RYOZAN_DEFAULT_URL;
  const ryozan: LibraryItem = {
    id: "project:ryozan",
    origin: "project",
    title: "Ryozan",
    subtitle: "Your publishing and promotion app",
    status: "App",
    tone: "rest",
    icon: "Megaphone",
    url: ryozanUrl,
    open: {
      agent: "smith",
      kind: "library",
      task: "Help me with Ryozan: what it can promote for my builds and how to connect a build to it.",
      subject: { type: "project", id: "ryozan", label: "Ryozan" },
    },
  };
  return [...builds, ryozan];
}

function repoFile(paths: DataPaths): string {
  return join(paths.library, "repos.json");
}

export function readRepos(paths: DataPaths): RepoRecord[] {
  const file = repoFile(paths);
  if (!existsSync(file)) return [];
  try {
    const parsed = z.array(repoRecordSchema).safeParse(JSON.parse(readFileSync(file, "utf8")));
    return parsed.success ? parsed.data : [];
  } catch {
    return [];
  }
}

function repoItem(repo: RepoRecord): LibraryItem {
  return {
    id: repo.id,
    origin: "repo",
    title: repo.title,
    subtitle: repo.url.replace(/^https:\/\//, "").replace(/\.git$/, ""),
    status: "Shortcut",
    tone: "rest",
    icon: "GitBranch",
    url: repo.url,
    open: {
      agent: "smith",
      kind: "library",
      task: `Talk me through the repository ${repo.title}: what it is and how it could help my builds.`,
      subject: { type: "repo", id: repo.id, label: repo.title },
    },
    updatedAt: repo.addedAt,
  };
}

/** A readable title and a stable id from a git remote. */
export function repoIdentity(url: string): { id: string; title: string } {
  const path = url
    .replace(/^ssh:\/\/[^/]+\//, "")
    .replace(/^https:\/\/[^/]+\//, "")
    .replace(/\.git$/, "")
    .replace(/\/+$/, "");
  const title = path || url;
  const slug = title.toLowerCase().replace(/[^a-z0-9._-]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 120) || "repo";
  return { id: `repo:${slug}`, title };
}

export function addRepo(paths: DataPaths, draft: LibraryRepoDraft, now = new Date()): LibraryItem {
  const repos = readRepos(paths);
  const identity = repoIdentity(draft.url);
  if (repos.some((repo) => repo.id === identity.id)) {
    throw new LibraryError("conflict", `${identity.title} is already in your Repos`);
  }
  const record = repoRecordSchema.parse({
    id: identity.id,
    url: draft.url,
    title: draft.title ?? identity.title,
    addedAt: now.toISOString(),
  });
  mkdirSync(paths.library, { recursive: true });
  writeFileSync(repoFile(paths), `${JSON.stringify([...repos, record], null, 2)}\n`, "utf8");
  return repoItem(record);
}

export function removeRepo(paths: DataPaths, id: string): boolean {
  const repos = readRepos(paths);
  const kept = repos.filter((repo) => repo.id !== id);
  if (kept.length === repos.length) return false;
  writeFileSync(repoFile(paths), `${JSON.stringify(kept, null, 2)}\n`, "utf8");
  return true;
}

const MOD_TONE: Record<Mod["status"], LibraryItem["tone"]> = { ready: "ok", "needs-setup": "warn", error: "err" };
const MOD_WORD: Record<Mod["status"], string> = { ready: "Ready", "needs-setup": "Needs setup", error: "Needs fixing" };

function modItem(mod: Mod): LibraryItem {
  return {
    id: mod.id,
    origin: "mod",
    title: mod.name,
    subtitle: mod.statusMessage ?? mod.description.slice(0, 300),
    status: MOD_WORD[mod.status],
    tone: MOD_TONE[mod.status],
    icon: mod.kind === "launch" ? "Rocket" : mod.kind === "pc" ? "Monitor" : mod.kind === "dev" ? "Wrench" : "Puzzle",
    open: {
      agent: "smith",
      kind: "mod",
      task: `Help me with ${mod.name}: what it does, what is not set up yet, and how to upgrade it.`,
      subject: { type: "mod", id: mod.id, label: mod.name },
    },
    ...(mod.builtIn ? {} : { updatedAt: mod.updatedAt }),
  };
}

const AUGMENT_ICON: Record<Augment["kind"], string> = {
  "mcp-server": "Server",
  skill: "BookOpen",
  prompt: "MessageSquareText",
  "tool-pack": "Wrench",
  plugin: "Plug",
};

function augmentItem(augment: Augment): LibraryItem {
  return {
    id: augment.id,
    origin: "augment",
    title: augment.name,
    subtitle: augment.description.slice(0, 300),
    status: augment.status === "ready" ? "Ready" : augment.status === "error" ? "Needs fixing" : "Needs setup",
    tone: augment.status === "ready" ? "ok" : augment.status === "error" ? "err" : "warn",
    icon: AUGMENT_ICON[augment.kind],
    ...(augment.sourceUrl ? { url: augment.sourceUrl } : {}),
    open: {
      agent: "smith",
      kind: "library",
      task: `Teach me the ${augment.name} augment: what it gives my mods and builds, and how to set it up.`,
      subject: { type: "augment", id: augment.id, label: augment.name },
    },
  };
}

const ADAPTER_WORD: Record<Adapter["status"], string> = {
  connected: "Connected",
  disconnected: "Not connected",
  "needs-cli": "CLI missing",
  expired: "Key expired",
  error: "Problem",
};
const ADAPTER_TONE: Record<Adapter["status"], LibraryItem["tone"]> = {
  connected: "ok",
  disconnected: "rest",
  "needs-cli": "warn",
  expired: "warn",
  error: "err",
};

function adapterItem(adapter: Adapter): LibraryItem {
  return {
    id: adapter.id,
    origin: "adapter",
    title: adapter.name,
    subtitle: (adapter.account ? `${adapter.account}. ${adapter.statusMessage ?? ""}` : (adapter.statusMessage ?? adapter.description)).slice(0, 300),
    status: ADAPTER_WORD[adapter.status],
    tone: ADAPTER_TONE[adapter.status],
    icon: "Cable",
    open: {
      agent: "smith",
      kind: "library",
      task: `Help me with my ${adapter.name} connection: what it lets my mods do and how to connect or fix it.`,
      subject: { type: "adapter", id: adapter.id, label: adapter.name },
    },
    ...(adapter.lastCheckedAt ? { updatedAt: adapter.lastCheckedAt } : {}),
  };
}

export interface LibrarySources {
  paths: DataPaths;
  env: Readonly<Record<string, string | undefined>>;
  mods: Mod[];
  augments: Augment[];
  adapters: Adapter[];
}

export function buildLibrary(sources: LibrarySources): LibraryResponse {
  const items = [
    ...projectItems(sources.paths, sources.env),
    ...readRepos(sources.paths).map(repoItem),
    ...sources.mods.map(modItem),
    ...sources.augments.map(augmentItem),
    ...sources.adapters.map(adapterItem),
  ].map((item) => libraryItemSchema.parse(item));
  return groupLibrary(items);
}
