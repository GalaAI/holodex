import {
  blueprintMatchSchema,
  type AtlasEntry,
  type Blueprint,
  type BlueprintMatch,
  type BuildKind,
  type Platform,
} from "@4ge/contracts";

/**
 * Rank Atlas Blueprints for an idea. Deterministic and explainable, so
 * Prometheus can say why it picked one: the build kind counts most, then
 * platforms, then the words the idea shares with the Blueprint. Below
 * MATCH_THRESHOLD nothing fits well enough and Prometheus crafts a new
 * Blueprint with the user.
 */
export const MATCH_THRESHOLD = 0.6;

const WEIGHTS = { kind: 0.55, platforms: 0.15, words: 0.3 } as const;

/** Kinds that can stand in for each other, with how well. */
const RELATED_KINDS: Partial<Record<BuildKind, Partial<Record<BuildKind, number>>>> = {
  website: { "web-app": 0.4 },
  "web-app": { website: 0.3, crm: 0.3, erp: 0.3 },
  crm: { erp: 0.6, "web-app": 0.4 },
  erp: { crm: 0.6, "web-app": 0.4 },
  "mobile-app": { "web-app": 0.2 },
  "desktop-app": { "web-app": 0.2 },
};

/**
 * Words that say what kind of build an idea is. The kind with the most cue
 * hits wins; a tie goes to the earlier entry (more specific kinds first).
 */
const KIND_CUES: Array<[BuildKind, RegExp]> = [
  ["game", /\b(video ?games?|games?|platformer|arcade|rpg|shooter|multiplayer|phaser|three\.?js|godot|levels?|high ?scores?)\b/g],
  ["erp", /\b(erp|inventory|stock levels?|warehouse|purchase orders?|invoic\w*|suppliers?|back office|operations|bill of materials)\b/g],
  ["crm", /\b(crm|sales pipeline|pipeline|leads|prospects|customer relationships?|client list|follow[- ]ups?)\b/g],
  ["desktop-app", /\b(desktop|electron|tauri|windows app|mac app|macos app|linux app|system tray|my (?:pc|computer))\b/g],
  ["mobile-app", /\b(mobile|phones?|iphone|ipad|android|ios|app store|play store|expo|react native)\b/g],
  ["web-app", /\b(web ?app|saas|dashboard|portal|platform|log ?in|sign ?up|accounts?|subscriptions?|members? area|admin panel)\b/g],
  ["website", /\b(website|web ?site|site|landing page|homepage|home page|portfolio|blog|marketing page|online presence)\b/g],
];

const PLATFORM_CUES: Array<[Platform, RegExp]> = [
  ["ios", /\b(ios|iphone|ipad|app store)\b/],
  ["android", /\b(android|play store)\b/],
  ["windows", /\b(windows|pc)\b/],
  ["macos", /\b(mac|macos|macbook)\b/],
  ["linux", /\b(linux|ubuntu)\b/],
  ["web", /\b(web|browser|website|site|online)\b/],
];

const DEFAULT_PLATFORMS: Record<BuildKind, Platform[]> = {
  website: ["web"],
  "web-app": ["web"],
  "mobile-app": ["ios", "android"],
  game: ["web"],
  erp: ["web"],
  crm: ["web"],
  "desktop-app": ["windows", "macos", "linux"],
  other: ["web"],
};

const STOP_WORDS = new Set(
  "a an and are as at be but by can do for from get have i in into is it its me my of on or our so that the their them this to up us we with you your want wants need needs make build like some something thing things app apps".split(
    " ",
  ),
);

export function normalizeText(text: string): string {
  return text.toLowerCase().replace(/[^a-z0-9.+#]+/g, " ").replace(/\s+/g, " ").trim();
}

/** Content words, lightly stemmed (plural s dropped) so "bookings" meets "booking". */
export function words(text: string): Set<string> {
  const out = new Set<string>();
  for (const raw of normalizeText(text).split(" ")) {
    if (raw.length < 3 || STOP_WORDS.has(raw)) continue;
    out.add(raw.length > 4 && raw.endsWith("s") && !raw.endsWith("ss") ? raw.slice(0, -1) : raw);
  }
  return out;
}

export function inferKind(idea: string): BuildKind | undefined {
  const text = normalizeText(idea);
  let best: { kind: BuildKind; hits: number } | undefined;
  for (const [kind, cue] of KIND_CUES) {
    const hits = text.match(cue)?.length ?? 0;
    if (hits > 0 && (!best || hits > best.hits)) best = { kind, hits };
  }
  return best?.kind;
}

export function inferPlatforms(idea: string, kind?: BuildKind): Platform[] {
  const text = normalizeText(idea);
  const found = PLATFORM_CUES.filter(([, cue]) => cue.test(text)).map(([platform]) => platform);
  if (kind === "mobile-app") {
    const phone = found.filter((platform) => platform === "ios" || platform === "android");
    return phone.length > 0 ? phone : DEFAULT_PLATFORMS["mobile-app"];
  }
  if (kind === "desktop-app") {
    const desktop = found.filter((platform) => platform === "windows" || platform === "macos" || platform === "linux");
    return desktop.length > 0 ? desktop : DEFAULT_PLATFORMS["desktop-app"];
  }
  if (found.length > 0) return found;
  return kind ? DEFAULT_PLATFORMS[kind] : ["web"];
}

export interface MatchQuery {
  idea: string;
  title?: string;
  kind?: BuildKind;
  platforms?: Platform[];
  features?: string[];
}

export interface MatchOutcome {
  matches: BlueprintMatch[];
  needsCrafting: boolean;
  inferred: { kind?: BuildKind; platforms: Platform[] };
}

function blueprintWords(blueprint: Blueprint): Set<string> {
  const text = [
    blueprint.name,
    blueprint.description,
    ...blueprint.tags,
    ...blueprint.stages.flatMap((stage) => stage.layers.map((layer) => layer.title)),
  ].join(" ");
  return words(text);
}

function kindScore(wanted: BuildKind | undefined, offered: BuildKind): number {
  if (!wanted || wanted === "other") return 0;
  if (wanted === offered) return 1;
  return RELATED_KINDS[wanted]?.[offered] ?? 0;
}

const round = (value: number) => Math.round(value * 100) / 100;

/** Score one Blueprint for a query; the reason says what matched in plain words. */
export function scoreBlueprint(blueprint: Blueprint, query: MatchQuery): BlueprintMatch {
  const kind = query.kind ?? inferKind([query.title, query.idea].filter(Boolean).join(" "));
  const platforms = query.platforms && query.platforms.length > 0 ? query.platforms : inferPlatforms(query.idea, kind);
  const ideaWords = words([query.title ?? "", query.idea, ...(query.features ?? [])].join(" "));
  const offeredWords = blueprintWords(blueprint);
  const shared = [...ideaWords].filter((word) => offeredWords.has(word));

  const kindPart = kindScore(kind, blueprint.kind);
  const platformHits = platforms.filter((platform) => blueprint.platforms.includes(platform));
  const platformPart = platforms.length > 0 ? platformHits.length / platforms.length : 0;
  const wordPart = Math.min(1, shared.length / 3);
  const score = round(Math.min(1, WEIGHTS.kind * kindPart + WEIGHTS.platforms * platformPart + WEIGHTS.words * wordPart));

  const reasons: string[] = [];
  if (kindPart === 1) reasons.push(`it is a ${blueprint.kind} Blueprint`);
  else if (kindPart > 0) reasons.push(`a ${blueprint.kind} is close to a ${kind}`);
  else reasons.push(kind ? `it builds a ${blueprint.kind}, not a ${kind}` : `the kind of build is not clear yet`);
  if (platformHits.length > 0) reasons.push(`runs on ${platformHits.join(", ")}`);
  if (shared.length > 0) reasons.push(`shares ${shared.slice(0, 5).join(", ")}`);
  const reason = `${blueprint.name}: ${reasons.join("; ")}.`;

  return blueprintMatchSchema.parse({ blueprintId: blueprint.blueprintId, score, reason: reason.slice(0, 600) });
}

/** Rank every usable Atlas entry for a query, best first; deprecated Blueprints are left out. */
export function matchBlueprints(entries: readonly AtlasEntry[], query: MatchQuery, limit = 5): MatchOutcome {
  const kind = query.kind ?? inferKind([query.title, query.idea].filter(Boolean).join(" "));
  const platforms = query.platforms && query.platforms.length > 0 ? query.platforms : inferPlatforms(query.idea, kind);
  const matches = entries
    .filter((entry) => entry.blueprint.state !== "deprecated")
    .map((entry) => ({ entry, match: scoreBlueprint(entry.blueprint, { ...query, ...(kind ? { kind } : {}), platforms }) }))
    .filter(({ match }) => match.score > 0)
    .sort(
      (left, right) =>
        right.match.score - left.match.score ||
        right.entry.uses - left.entry.uses ||
        left.entry.blueprint.blueprintId.localeCompare(right.entry.blueprint.blueprintId),
    )
    .slice(0, limit)
    .map(({ match }) => match);
  const best = matches[0]?.score ?? 0;
  return { matches, needsCrafting: best < MATCH_THRESHOLD, inferred: { ...(kind ? { kind } : {}), platforms } };
}
