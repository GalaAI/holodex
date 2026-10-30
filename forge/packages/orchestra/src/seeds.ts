import { createHash } from "node:crypto";
import { readdirSync } from "node:fs";
import { join } from "node:path";
import {
  blueprintSchema,
  seedDraftSchema,
  seedReadiness,
  seedSchema,
  type Blueprint,
  type Seed,
  type SeedDraftInput,
} from "@4ge/contracts";
import { notFound } from "./errors.js";
import { newId, now, readJson, writeJson, type EventSink } from "./util.js";

/**
 * Seed Scrolls under dataRoot()/seeds/<id>.json, one Seed per file in the
 * contract's shape so the Atlas layer can read the Seed it is asked to
 * match. A Blueprint Prometheus crafted while the Atlas layer was not
 * connected waits beside its Seed as <id>.blueprint.json until it can be
 * registered.
 */
export class SeedStore {
  constructor(
    private readonly dir: string,
    private readonly events: EventSink,
  ) {}

  private file(id: string): string {
    return join(this.dir, `${id}.json`);
  }

  create(draft: SeedDraftInput, extras: Partial<Pick<Seed, "imagistationSessionId">> = {}): Seed {
    const parsed = seedDraftSchema.parse(draft);
    const at = now();
    const seed = seedSchema.parse({
      ...parsed,
      id: newId("seed"),
      status: seedReadiness(parsed).ready ? "ready" : "draft",
      decision: "pending",
      ...extras,
      createdAt: at,
      updatedAt: at,
    });
    writeJson(this.file(seed.id), seed);
    this.publish(seed);
    return seed;
  }

  get(id: string): Seed {
    const seed = readJson(this.file(id), seedSchema);
    if (!seed) throw notFound(`Seed ${id}`);
    return seed;
  }

  find(id: string): Seed | undefined {
    return readJson(this.file(id), seedSchema);
  }

  list(): Seed[] {
    let names: string[] = [];
    try {
      names = readdirSync(this.dir).filter((name) => /^seed_[A-Za-z0-9_-]+\.json$/.test(name));
    } catch {
      return [];
    }
    return names
      .map((name) => readJson(join(this.dir, name), seedSchema))
      .filter((seed): seed is Seed => seed !== undefined)
      .sort((left, right) => right.createdAt.localeCompare(left.createdAt));
  }

  update(id: string, patch: Partial<Omit<Seed, "id" | "createdAt">>, sessionId?: string): Seed {
    const current = this.get(id);
    const merged = { ...current, ...patch, updatedAt: now() };
    const seed = seedSchema.parse(merged);
    writeJson(this.file(id), seed);
    this.publish(seed, sessionId);
    return seed;
  }

  private publish(seed: Seed, sessionId = seed.imagistationSessionId): void {
    this.events.publish({
      type: "seed.updated",
      ...(sessionId ? { sessionId } : {}),
      ...(seed.buildId ? { buildId: seed.buildId } : {}),
      data: { seed },
    });
  }

  /** A crafted Blueprint kept with its Seed while the Atlas is not connected. */
  heldBlueprint(seedId: string): Blueprint | undefined {
    return readJson(join(this.dir, `${seedId}.blueprint.json`), blueprintSchema);
  }

  holdBlueprint(seedId: string, blueprint: Blueprint): void {
    writeJson(join(this.dir, `${seedId}.blueprint.json`), blueprintSchema.parse(blueprint));
  }
}

/* ------------------------------------------------------------------ */
/* AInbox: the Seed "later" path.                                       */
/* ------------------------------------------------------------------ */

/**
 * Holodex's AInbox queue item, written in AInbox's own format: a
 * StackHandoff record serialized by System.Text.Json with default options
 * (PascalCase property names), dropped into <queue>/pending/<ScrollId>.json
 * where OutboundQueue drains it (Holodex AInboxStack.cs: StackHandoff,
 * QueuedItem, ScrollField, HandoffSeal.Seal, OutboundQueue.EnqueueAsync).
 */
export interface StackHandoffFile {
  ScrollId: string;
  Group: string;
  GroupLabel: string;
  PodId: string;
  IntentId: string;
  IntentLabel: string;
  Ask: string;
  Fields: Array<{ Label: string; Value: string; Source: string }>;
  Items: Array<{
    Id: string;
    Name: string;
    Kind: string;
    Channel: string;
    MediaType: string;
    FilePath: string | null;
    Text: string | null;
    Url: string | null;
    Sha256: string | null;
    Origin: Record<string, string>;
    AInboxItemId: string | null;
    Error: string | null;
  }>;
  Sha256: string;
  CreatedAt: string;
}

const sha256 = (text: string) => createHash("sha256").update(text, "utf8").digest("hex");

/** HandoffSeal.Seal: canonical form joined by LF, ids and timestamps left out. */
export function handoffSeal(
  podId: string,
  intentId: string,
  ask: string,
  fields: ReadonlyArray<{ Label: string; Value: string }>,
  captureShas: readonly string[],
): string {
  const parts = ["ark.handoff-scroll.v2", podId, intentId, ask.trim()];
  for (const field of fields) parts.push(`${field.Label}=${field.Value}`);
  parts.push(...captureShas);
  return sha256(parts.join("\n"));
}

/** HandoffSeal's SafeName: anything outside [A-Za-z0-9._-] becomes an underscore. */
export const safeName = (value: string) => value.replace(/[^A-Za-z0-9._-]/g, "_");

export function seedScrollMarkdown(seed: Seed, blueprintName?: string): string {
  const lines = [
    `# Seed Scroll: ${seed.title}`,
    "",
    `- Seed: ${seed.id}`,
    `- Kind: ${seed.kind} (${seed.platforms.join(", ")})`,
    ...(blueprintName || seed.blueprintId ? [`- Blueprint: ${blueprintName ?? seed.blueprintId}`] : []),
    "",
    "## The idea, in your words",
    "",
    seed.idea,
    "",
  ];
  if (seed.problem) lines.push("## Problem", "", seed.problem, "");
  if (seed.audience) lines.push("## Who it serves", "", seed.audience, "");
  if (seed.successSignal) lines.push("## How success shows", "", seed.successSignal, "");
  if (seed.features.length > 0) lines.push("## Must have", "", ...seed.features.map((feature) => `- ${feature}`), "");
  if (seed.constraints.length > 0) lines.push("## Constraints", "", ...seed.constraints.map((item) => `- ${item}`), "");
  if (seed.answers.length > 0) {
    lines.push("## Questions Prometheus asked", "");
    for (const answer of seed.answers) lines.push(`- ${answer.question}`, `  ${answer.answer}`);
    lines.push("");
  }
  return `${lines.join("\n").trimEnd()}\n`;
}

export function seedHandoff(seed: Seed, reminderNote: string | undefined, createdAt = now()): StackHandoffFile {
  const scrollId = `scroll:seed:${seed.id}`;
  const note = reminderNote?.trim() || "Time-block this build in 4GE.";
  const ask = `Build later in 4GE: ${seed.title}. ${note}`;
  const fields = [
    { Label: "Seed", Value: seed.id, Source: "4ge" },
    { Label: "Title", Value: seed.title, Source: "4ge" },
    { Label: "Kind", Value: seed.kind, Source: "4ge" },
    { Label: "Blueprint", Value: seed.blueprintId ?? "not chosen yet", Source: "4ge" },
    { Label: "Reminder", Value: note, Source: "user" },
  ];
  const markdown = seedScrollMarkdown(seed);
  const markdownSha = sha256(markdown);
  return {
    ScrollId: scrollId,
    Group: "S",
    GroupLabel: "Seed",
    PodId: "4ge",
    IntentId: "build-later",
    IntentLabel: "Build later",
    Ask: ask,
    Fields: fields,
    Items: [
      {
        Id: "scroll",
        Name: "seed-scroll.md",
        Kind: "Scroll",
        Channel: "text",
        MediaType: "text/markdown",
        FilePath: null,
        Text: markdown,
        Url: null,
        Sha256: markdownSha,
        Origin: { source: "4ge", seedId: seed.id },
        AInboxItemId: null,
        Error: null,
      },
    ],
    Sha256: handoffSeal("4ge", "build-later", ask, fields, [markdownSha]),
    CreatedAt: createdAt,
  };
}

/** Write the handoff into the AInbox queue's pending folder; returns the file name. */
export function queueSeedInAinbox(queueDir: string, seed: Seed, reminderNote?: string): string {
  const handoff = seedHandoff(seed, reminderNote);
  const name = `${safeName(handoff.ScrollId)}.json`;
  writeJson(join(queueDir, "pending", name), handoff);
  return name;
}
