import { join } from "node:path";
import {
  buildKindSchema,
  platformSchema,
  seedDraftSchema,
  seedReadiness,
  type AtlasEntry,
  type Blueprint,
  type BlueprintMatch,
  type ChatMessage,
  type Seed,
  type Session,
} from "@4ge/contracts";
import { z } from "zod";
import { AGENT_DEFINITIONS } from "./agents.js";
import { askJson } from "./ask.js";
import { MATCH_THRESHOLD } from "@4ge/atlas";
import { craftBlueprint, fillParameters, missingParameters } from "./blueprint.js";
import { OrchestraError, isOrchestraError, messageOf } from "./errors.js";
import type { ModelClient } from "./model.js";
import { MarkerSplitter, extractJson } from "./parse.js";
import { LayerNotConnected, type AtlasPort } from "./ports.js";
import type { SeedStore } from "./seeds.js";
import type { SessionManager } from "./sessions.js";
import { listStarterManifests } from "@4ge/atlas";
import { clip, readJson, writeJson } from "./util.js";

/**
 * Imagistation: the person brainstorms with Prometheus (SPEC flow 1 to 4).
 * Each turn streams Prometheus's reply and, after a hidden marker, the
 * state of the Seed draft so far. Writing the Seed keeps the person's own
 * framing verbatim, picks a Blueprint from their Atlas (or crafts one with
 * them) and asks: build now or later?
 */

const STATE_MARKER = "<<<STATE>>>";

const draftPartialSchema = z.object({
  title: z.string().max(160).optional(),
  problem: z.string().max(1200).optional(),
  audience: z.string().max(1200).optional(),
  successSignal: z.string().max(1200).optional(),
  kind: buildKindSchema.optional(),
  platforms: z.array(platformSchema).max(6).optional(),
  features: z.array(z.string().min(1).max(400)).max(60).optional(),
  constraints: z.array(z.string().min(1).max(400)).max(40).optional(),
  references: z.array(z.string().min(1).max(600)).max(40).optional(),
});
type DraftPartial = z.infer<typeof draftPartialSchema>;

const stateSchema = z.object({
  idea: z.string().default(""),
  draft: draftPartialSchema.default({}),
  questions: z.array(z.string()).default([]),
  answers: z.array(z.object({ question: z.string(), answer: z.string() })).default([]),
  ready: z.boolean().default(false),
  seedId: z.string().optional(),
});
type ImagistationState = z.infer<typeof stateSchema>;

/** Lenient read of the hidden state block: keep every field that parses, drop the rest. */
function readTurnState(hidden: string): { draft: DraftPartial; questions: string[]; ready: boolean } {
  let raw: Record<string, unknown> = {};
  try {
    const value = extractJson(hidden);
    if (value && typeof value === "object" && !Array.isArray(value)) raw = value as Record<string, unknown>;
  } catch {
    return { draft: {}, questions: [], ready: false };
  }
  const draftRaw = (raw.draft && typeof raw.draft === "object" ? raw.draft : {}) as Record<string, unknown>;
  const draft: Record<string, unknown> = {};
  for (const [key, field] of Object.entries(draftPartialSchema.shape)) {
    const parsed = (field as z.ZodType).safeParse(draftRaw[key]);
    if (parsed.success && parsed.data !== undefined) {
      if (typeof parsed.data === "string" && parsed.data.trim().length === 0) continue;
      if (Array.isArray(parsed.data) && parsed.data.length === 0) continue;
      draft[key] = parsed.data;
    }
  }
  const questions = Array.isArray(raw.questions)
    ? raw.questions.filter((item): item is string => typeof item === "string" && item.trim().length > 0).slice(0, 3)
    : [];
  return { draft: draft as DraftPartial, questions, ready: raw.ready === true };
}

const seedFieldsSchema = z.object({
  title: z.string().min(1).max(160),
  problem: z.string().max(1200).default(""),
  audience: z.string().max(1200).default(""),
  successSignal: z.string().max(1200).default(""),
  kind: buildKindSchema,
  platforms: z.array(platformSchema).min(1).max(6),
  features: z.array(z.string().min(1).max(400)).max(60).default([]),
  constraints: z.array(z.string().min(1).max(400)).max(40).default([]),
  references: z.array(z.string().min(1).max(600)).max(40).default([]),
});

const OPENING =
  "I am Prometheus, your guide in Imagistation. Tell me what you want to build, in your own words. I will ask a few questions, then write it up as a Seed Scroll you can build now or later.";

export interface ImagistationDeps {
  sessions: SessionManager;
  seeds: SeedStore;
  models: ModelClient;
  atlas: AtlasPort;
  sessionsDir: string;
  startersDir: string;
}

export class Imagistation {
  constructor(private readonly deps: ImagistationDeps) {}

  private stateFile(sessionId: string): string {
    return join(this.deps.sessionsDir, `${sessionId}.imagistation.json`);
  }

  private state(sessionId: string): ImagistationState {
    return readJson(this.stateFile(sessionId), stateSchema) ?? stateSchema.parse({});
  }

  private saveState(sessionId: string, state: ImagistationState): void {
    writeJson(this.stateFile(sessionId), state);
  }

  private session(sessionId: string): Session {
    const session = this.deps.sessions.get(sessionId);
    if (session.kind !== "imagistation") {
      throw new OrchestraError("bad_request", `Session ${sessionId} is not an Imagistation session`);
    }
    return session;
  }

  list(): Session[] {
    return this.deps.sessions.list({ kind: "imagistation" });
  }

  get(sessionId: string): { session: Session; messages: ChatMessage[] } {
    return { session: this.session(sessionId), messages: this.deps.sessions.messages(sessionId) };
  }

  /** Open a session. With an idea, Prometheus answers it right away. */
  async create(idea?: string): Promise<{ session: Session; messages: ChatMessage[] }> {
    const trimmed = idea?.trim() ?? "";
    const session = this.deps.sessions.create({
      agent: "prometheus",
      kind: "imagistation",
      task: trimmed ? `Imagistation: ${clip(trimmed, 200)}` : "Imagistation: a new idea",
      status: "waiting",
      summary: "Waiting for your idea",
    });
    this.saveState(session.id, stateSchema.parse({}));
    this.deps.sessions.addMessage(session.id, { role: "assistant", agent: "prometheus", content: OPENING });
    if (trimmed) {
      try {
        await this.send(session.id, trimmed);
      } catch (error) {
        // The session and the person's words are kept; the reason shows in the thread.
        this.deps.sessions.addMessage(session.id, { role: "system", content: messageOf(error) });
        this.deps.sessions.update(session.id, { status: "waiting", summary: "No answer yet", error: messageOf(error) });
      }
    }
    return this.get(session.id);
  }

  /** One turn: the person's message, Prometheus's streamed reply, the updated draft. */
  async send(sessionId: string, content: string): Promise<{ message: ChatMessage; reply: ChatMessage }> {
    this.session(sessionId);
    const text = content.trim();
    if (!text) throw new OrchestraError("bad_request", "Write a message first");
    this.deps.sessions.revive(sessionId);
    const state = this.state(sessionId);
    const history = this.deps.sessions
      .messages(sessionId)
      .filter((message) => message.role === "user" || message.role === "assistant")
      .slice(-24);
    const message = this.deps.sessions.addMessage(sessionId, { role: "user", content: text });
    if (!state.idea) state.idea = text;
    else if (state.questions.length > 0) state.answers.push({ question: state.questions.join(" "), answer: text });
    this.saveState(sessionId, state);
    this.deps.sessions.update(sessionId, { status: "running", summary: "Prometheus is thinking" });

    const replyId = this.deps.sessions.reserveMessageId();
    const splitter = new MarkerSplitter(STATE_MARKER);
    const system = `${AGENT_DEFINITIONS.prometheus.system}

The Seed draft so far, as JSON:
${JSON.stringify({ idea: state.idea, ...state.draft, answers: state.answers }, null, 1)}

How to answer: reply to the person first, in plain words. Then, on its own line, write ${STATE_MARKER} followed by one JSON object the person never sees:
{"draft": {only the fields you learned or refined: "title", "problem", "audience", "successSignal", "kind" (one of website, web-app, mobile-app, game, erp, crm, desktop-app, other), "platforms" (from web, ios, android, windows, linux, macos), "features", "constraints", "references"}, "questions": [the questions you just asked, at most three], "ready": true only when problem, audience, success signal, kind and platforms are all clear}`;
    let result;
    try {
      result = await this.deps.models.chat({
        tier: "lead",
        purpose: "imagistation.turn",
        messages: [
          { role: "system", content: system },
          ...history.map((item) => ({ role: item.role as "user" | "assistant", content: item.content })),
          { role: "user", content: text },
        ],
        maxTokens: 1500,
        temperature: 0.5,
        signal: this.deps.sessions.signal(sessionId),
        onDelta: (piece) => this.deps.sessions.delta(sessionId, replyId, splitter.push(piece)),
      });
    } catch (error) {
      this.deps.sessions.update(sessionId, {
        status: isOrchestraError(error, "cancelled") ? "cancelled" : "waiting",
        summary: "Prometheus could not answer",
        error: messageOf(error),
      });
      throw error;
    }
    const tail = splitter.finish();
    if (tail.flushed) this.deps.sessions.delta(sessionId, replyId, tail.flushed);
    // Hivemind answers in one piece and some models skip the stream; split the final text too.
    const at = result.text.indexOf(STATE_MARKER);
    const visible = (at >= 0 ? result.text.slice(0, at) : tail.visible || result.text).trim();
    const hidden = at >= 0 ? result.text.slice(at + STATE_MARKER.length) : tail.hidden;
    const turn = readTurnState(hidden);
    const next = this.state(sessionId);
    next.draft = { ...next.draft, ...turn.draft };
    next.questions = turn.questions;
    next.ready = turn.ready;
    this.saveState(sessionId, next);
    const reply = this.deps.sessions.addMessage(sessionId, {
      id: replyId,
      role: "assistant",
      agent: "prometheus",
      content: visible || turn.questions.join("\n") || "Tell me more about what you want to build.",
      usage: result.usage,
    });
    this.deps.sessions.update(sessionId, {
      status: "waiting",
      model: result.model,
      summary: next.ready ? "The Seed is ready to write" : "Waiting for your answer",
    });
    return { message, reply };
  }

  /** Write (or rewrite) the Seed Scroll from the session, then choose its Blueprint. */
  async writeSeed(sessionId: string): Promise<Seed> {
    this.session(sessionId);
    const state = this.state(sessionId);
    if (!state.idea.trim()) {
      throw new OrchestraError("bad_request", "Say what you want to build first", "Tell Prometheus your idea in your own words.");
    }
    const existing = state.seedId ? this.deps.seeds.find(state.seedId) : undefined;
    if (existing && (existing.status === "building" || existing.status === "built")) {
      throw new OrchestraError("conflict", "This Seed is already building, so its Scroll cannot be rewritten", "Open the build instead.");
    }
    this.deps.sessions.revive(sessionId);
    const signal = this.deps.sessions.signal(sessionId);
    this.deps.sessions.update(sessionId, { status: "running", summary: "Writing the Seed Scroll" });
    try {
      const transcript = this.deps.sessions
        .messages(sessionId)
        .filter((message) => message.role === "user" || message.role === "assistant")
        .map((message) => `${message.role === "user" ? "Person" : "Prometheus"}: ${message.content}`)
        .join("\n\n");
      const { value } = await askJson(this.deps.models, {
        tier: "lead",
        purpose: "seed.draft",
        system: `${AGENT_DEFINITIONS.prometheus.system}

You now write the Seed Scroll from the conversation. Organize what the person said; do not add features they did not ask for. Leave a field empty when they did not say it. Answer with one JSON object and nothing else:
{"title": short name, "problem": string, "audience": string, "successSignal": string, "kind": one of website, web-app, mobile-app, game, erp, crm, desktop-app, other, "platforms": [from web, ios, android, windows, linux, macos], "features": [string], "constraints": [string], "references": [string]}`,
        messages: [
          {
            role: "user",
            content: `Conversation:\n${clip(transcript, 30_000)}\n\nDraft so far:\n${JSON.stringify(state.draft, null, 1)}`,
          },
        ],
        schema: seedFieldsSchema,
        maxTokens: 2500,
        signal,
      });
      const draft = seedDraftSchema.parse({ ...value, idea: state.idea, answers: state.answers.slice(0, 40) });
      let seed = existing
        ? this.deps.seeds.update(existing.id, { ...draft, status: seedReadiness(draft).ready ? "ready" : "draft" }, sessionId)
        : this.deps.seeds.create(draft, { imagistationSessionId: sessionId });
      state.seedId = seed.id;
      this.saveState(sessionId, state);

      this.deps.sessions.update(sessionId, { summary: "Choosing a Blueprint" });
      const choice = await this.chooseBlueprint(seed, signal);
      let parameters: Record<string, string> = {};
      try {
        parameters = await fillParameters(this.deps.models, choice.blueprint, seed, signal);
      } catch (error) {
        if (signal.aborted) throw error;
        this.deps.sessions.log(sessionId, "warn", `Blueprint parameters were left for later: ${messageOf(error)}`);
      }
      seed = this.deps.seeds.update(
        seed.id,
        { blueprintId: choice.blueprint.blueprintId, blueprintMatch: choice.match, parameters },
        sessionId,
      );
      this.deps.sessions.addMessage(sessionId, {
        role: "assistant",
        agent: "prometheus",
        content: this.scrollMessage(seed, choice.blueprint, choice.note),
      });
      this.deps.sessions.update(sessionId, { status: "waiting", summary: "Seed Scroll written: build now or later?" });
      return seed;
    } catch (error) {
      this.deps.sessions.update(sessionId, {
        status: signal.aborted ? "cancelled" : "waiting",
        summary: "The Seed Scroll was not written",
        error: messageOf(error),
      });
      throw error;
    }
  }

  /** Best Atlas match, else a Blueprint crafted for this Seed and registered in the Atlas. */
  private async chooseBlueprint(
    seed: Seed,
    signal: AbortSignal,
  ): Promise<{ blueprint: Blueprint; match: BlueprintMatch; note?: string }> {
    let atlasConnected = true;
    let entries: AtlasEntry[] = [];
    try {
      const found = await this.deps.atlas.match(seed.id);
      const best = [...found.matches].sort((left, right) => right.score - left.score)[0];
      // The Atlas decides what fits (its MATCH_THRESHOLD); an Atlas that gives no verdict is held to the same threshold.
      const fits = found.needsCrafting === undefined ? (best?.score ?? 0) >= MATCH_THRESHOLD : !found.needsCrafting;
      if (best && fits) {
        const entry = await this.deps.atlas.get(best.blueprintId);
        if (entry) return { blueprint: entry.blueprint, match: best };
      }
      entries = await this.deps.atlas.list();
    } catch (error) {
      if (!(error instanceof LayerNotConnected)) throw error;
      atlasConnected = false;
    }
    const crafted = await craftBlueprint(this.deps.models, seed, {
      starters: listStarterManifests(this.deps.startersDir).starters.map((starter) => starter.name),
      takenIds: new Set(entries.map((entry) => entry.blueprint.blueprintId)),
      signal,
    });
    let blueprint = crafted;
    let note: string | undefined;
    if (atlasConnected) {
      try {
        blueprint = (await this.deps.atlas.register(crafted)).blueprint;
      } catch (error) {
        if (!(error instanceof LayerNotConnected)) throw error;
        atlasConnected = false;
      }
    }
    if (!atlasConnected) {
      this.deps.seeds.holdBlueprint(seed.id, crafted);
      note = "Your Atlas is not connected yet, so this Blueprint is kept with the Seed until it is.";
    }
    return {
      blueprint,
      match: {
        blueprintId: blueprint.blueprintId,
        score: 1,
        reason: "Nothing in your Atlas fit this Seed, so Prometheus crafted this Blueprint from your answers.",
      },
      ...(note ? { note } : {}),
    };
  }

  private scrollMessage(seed: Seed, blueprint: Blueprint, note?: string): string {
    const readiness = seedReadiness(seed);
    const lines = [`Your Seed Scroll is written: ${seed.title}.`, ""];
    if (seed.problem) lines.push(`Problem: ${seed.problem}`);
    if (seed.audience) lines.push(`Who it serves: ${seed.audience}`);
    if (seed.successSignal) lines.push(`Success shows when: ${seed.successSignal}`);
    lines.push(`Kind: ${seed.kind}, for ${seed.platforms.join(", ")}.`);
    if (seed.features.length > 0) lines.push(`Must have: ${seed.features.slice(0, 8).join("; ")}.`);
    lines.push(
      "",
      `Blueprint: ${blueprint.name}. ${seed.blueprintMatch?.reason ?? ""}`.trim(),
      `It runs ${blueprint.stages.length} stages: ${blueprint.stages.map((stage) => stage.stage).join(", ")}.`,
    );
    const missing = missingParameters(blueprint, seed.parameters);
    if (missing.length > 0) lines.push(`Still to decide during the build: ${missing.join(", ")}.`);
    if (!readiness.ready) {
      lines.push(`Not stated yet: ${readiness.missing.join(", ")}. You can build anyway, or tell me more and I will rewrite the Scroll.`);
    }
    if (note) lines.push(note);
    lines.push("", "Build it now, or later? Later puts it in your AInbox with a reminder to time-block it.");
    return lines.join("\n");
  }

  /** A note in the Imagistation thread after the person decides. */
  noteDecision(seed: Seed, text: string): void {
    if (seed.imagistationSessionId && this.deps.sessions.has(seed.imagistationSessionId)) {
      this.deps.sessions.addMessage(seed.imagistationSessionId, { role: "assistant", agent: "prometheus", content: text });
      this.deps.sessions.update(seed.imagistationSessionId, { status: "done", summary: text.slice(0, 200) });
    }
  }
}
