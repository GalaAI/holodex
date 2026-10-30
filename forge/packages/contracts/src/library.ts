import { z } from "zod";
import { agentIdSchema } from "./agents.js";
import { isoDateTimeSchema } from "./common.js";
import { sessionKindSchema, sessionSubjectSchema } from "./session.js";

/**
 * The Holodex Library, grouped by origin (operator rulings 2026-10-30).
 * Every item carries one origin and GET /library returns the five groups in
 * this order: My projects, Repos, Mods, Augments, Adapters. Clicking an item
 * opens the matching Smith session.
 */
export const LIBRARY_ORIGINS = ["project", "repo", "mod", "augment", "adapter"] as const;
export const libraryOriginSchema = z.enum(LIBRARY_ORIGINS);
export type LibraryOrigin = z.infer<typeof libraryOriginSchema>;

export const LIBRARY_GROUPS: Record<
  LibraryOrigin,
  { label: string; holds: string; canCreate: boolean }
> = {
  project: {
    label: "My projects",
    holds: "Apps you built in 4GE, finished and in progress, and Ryozan",
    canCreate: false,
  },
  repo: {
    label: "Repos",
    holds: "Shortcuts to third-party repositories that open as launchers",
    canCreate: false,
  },
  mod: {
    label: "Mods",
    holds: "Dev Mod, PC Mod, Launch Mod and your own mods",
    canCreate: true,
  },
  augment: {
    label: "Augments",
    holds: "MCP servers, skills, prompts and tool packs your mods and builds use",
    canCreate: false,
  },
  adapter: {
    label: "Adapters",
    holds: "Connections to your accounts and services",
    canCreate: false,
  },
};

/** What a click on the item opens. */
export const libraryOpenSchema = z.object({
  agent: agentIdSchema,
  kind: sessionKindSchema,
  task: z.string().min(1).max(600),
  subject: sessionSubjectSchema,
});
export type LibraryOpen = z.infer<typeof libraryOpenSchema>;

export const libraryItemSchema = z.object({
  /** Unique across the Library: project:<buildId>, repo:<slug>, mod:dev, ... */
  id: z.string().min(3).max(200),
  origin: libraryOriginSchema,
  title: z.string().min(1).max(160),
  subtitle: z.string().max(300).optional(),
  /** Short status word for the tile, e.g. Building, Live, Connected. */
  status: z.string().max(60).optional(),
  tone: z.enum(["live", "ok", "warn", "err", "rest"]).default("rest"),
  /** lucide-react icon name. */
  icon: z.string().max(60).optional(),
  /** External launcher target for repos and shortcuts. */
  url: z.string().url().optional(),
  open: libraryOpenSchema,
  updatedAt: isoDateTimeSchema.optional(),
});
export type LibraryItem = z.infer<typeof libraryItemSchema>;

/** A repository shortcut the user adds to the Repos group (library builder addition). */
export const libraryRepoDraftSchema = z.object({
  /** An https or ssh:// git remote, e.g. https://github.com/owner/repo. */
  url: z
    .string()
    .trim()
    .max(400)
    .url()
    .refine((value) => value.startsWith("https://") || value.startsWith("ssh://"), {
      message: "use an https:// or ssh:// repository address",
    }),
  title: z.string().trim().min(1).max(160).optional(),
});
export type LibraryRepoDraft = z.infer<typeof libraryRepoDraftSchema>;

export const libraryGroupSchema = z.object({
  origin: libraryOriginSchema,
  label: z.string(),
  canCreate: z.boolean(),
  items: z.array(libraryItemSchema),
});
export type LibraryGroup = z.infer<typeof libraryGroupSchema>;

export const libraryResponseSchema = z.object({
  groups: z.array(libraryGroupSchema).length(LIBRARY_ORIGINS.length),
  total: z.number().int().nonnegative(),
});
export type LibraryResponse = z.infer<typeof libraryResponseSchema>;

/** Group items by origin in the canonical order; empty groups stay listed. */
export function groupLibrary(items: readonly LibraryItem[]): LibraryResponse {
  const groups = LIBRARY_ORIGINS.map((origin) => ({
    origin,
    label: LIBRARY_GROUPS[origin].label,
    canCreate: LIBRARY_GROUPS[origin].canCreate,
    items: items
      .filter((item) => item.origin === origin)
      .sort((left, right) => left.title.localeCompare(right.title)),
  }));
  return { groups, total: items.length };
}
