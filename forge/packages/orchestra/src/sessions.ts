import { readdirSync } from "node:fs";
import { join } from "node:path";
import {
  buildSessionTree,
  chatMessageSchema,
  sessionSchema,
  type AgentId,
  type ChatMessage,
  type ChatRole,
  type ModelUsage,
  type Session,
  type SessionKind,
  type SessionNode,
  type SessionStatus,
  type SessionSubject,
} from "@4ge/contracts";
import { z } from "zod";
import { cancelled, notFound } from "./errors.js";
import { appendJsonl, clip, newId, now, plainText, readJson, writeJson, type EventDraft, type EventSink } from "./util.js";

/**
 * The nested session tree, like Claude Code subagents: Smith's build
 * session is the root, every layer runs in a child session (parentId), and
 * cancelling a session cancels its whole subtree.
 *
 * Persisted under dataRoot()/sessions: <id>.json holds the session and its
 * messages, <id>.events.jsonl journals the session's events (deltas are
 * left out; they stream live and the completed message is journaled).
 */

const sessionFileSchema = z.object({ session: sessionSchema, messages: z.array(chatMessageSchema) });

const FINISHED: ReadonlySet<SessionStatus> = new Set(["done", "failed", "cancelled"]);

export interface CreateSessionInput {
  agent: AgentId;
  kind: SessionKind;
  task: string;
  parentId?: string | undefined;
  subject?: SessionSubject | undefined;
  buildId?: string | undefined;
  status?: SessionStatus;
  model?: string;
  summary?: string;
}

export interface AddMessageInput {
  role: ChatRole;
  content: string;
  agent?: AgentId | undefined;
  usage?: ModelUsage | undefined;
  id?: string;
}

interface Entry {
  session: Session;
  messages: ChatMessage[];
  controller: AbortController;
}

export class SessionManager {
  private readonly entries = new Map<string, Entry>();

  constructor(
    private readonly dir: string,
    private readonly events: EventSink,
  ) {
    this.load();
  }

  private load(): void {
    let names: string[] = [];
    try {
      names = readdirSync(this.dir).filter((name) => name.endsWith(".json"));
    } catch {
      return;
    }
    for (const name of names) {
      const file = readJson(join(this.dir, name), sessionFileSchema);
      if (!file) continue;
      const session = file.session;
      if (session.status === "running" || session.status === "queued") {
        // Work that was in flight when the server stopped did not finish.
        session.status = "failed";
        session.error = "The 4GE server stopped while this session was running.";
        session.updatedAt = now();
        session.finishedAt = session.updatedAt;
      }
      this.entries.set(session.id, { session, messages: file.messages, controller: new AbortController() });
    }
  }

  private file(id: string): string {
    return join(this.dir, `${id}.json`);
  }

  private persist(entry: Entry): void {
    writeJson(this.file(entry.session.id), { session: entry.session, messages: entry.messages });
  }

  private emit(entry: Entry, draft: EventDraft, journal = true): void {
    this.events.publish(draft);
    if (journal) appendJsonl(join(this.dir, `${entry.session.id}.events.jsonl`), { at: now(), ...draft });
  }

  private entry(id: string): Entry {
    const found = this.entries.get(id);
    if (!found) throw notFound(`Session ${id}`);
    return found;
  }

  create(input: CreateSessionInput): Session {
    const parent = input.parentId ? this.entry(input.parentId) : undefined;
    const id = newId("session");
    const at = now();
    const session = sessionSchema.parse({
      id,
      parentId: parent ? parent.session.id : null,
      rootId: parent ? parent.session.rootId : id,
      agent: input.agent,
      kind: input.kind,
      task: clip(input.task.trim() || "Work", 4000),
      status: input.status ?? "queued",
      ...(input.subject ? { subject: input.subject } : {}),
      ...((input.buildId ?? parent?.session.buildId) ? { buildId: input.buildId ?? parent?.session.buildId } : {}),
      ...(input.model ? { model: input.model } : {}),
      ...(input.summary ? { summary: clip(input.summary, 600) } : {}),
      createdAt: at,
      updatedAt: at,
    });
    const controller = new AbortController();
    if (parent) {
      const parentSignal = parent.controller.signal;
      if (parentSignal.aborted) controller.abort(parentSignal.reason);
      else parentSignal.addEventListener("abort", () => controller.abort(parentSignal.reason), { once: true });
    }
    const entry: Entry = { session, messages: [], controller };
    this.entries.set(id, entry);
    this.persist(entry);
    this.emit(entry, {
      type: "session.created",
      sessionId: id,
      ...(session.buildId ? { buildId: session.buildId } : {}),
      data: { session },
    });
    return session;
  }

  has(id: string): boolean {
    return this.entries.has(id);
  }

  get(id: string): Session {
    return this.entry(id).session;
  }

  messages(id: string): ChatMessage[] {
    return [...this.entry(id).messages];
  }

  list(filter: { buildId?: string | undefined; kind?: SessionKind; agent?: AgentId } = {}): Session[] {
    return [...this.entries.values()]
      .map((entry) => entry.session)
      .filter(
        (session) =>
          (filter.buildId === undefined || session.buildId === filter.buildId) &&
          (filter.kind === undefined || session.kind === filter.kind) &&
          (filter.agent === undefined || session.agent === filter.agent),
      )
      .sort((left, right) => right.createdAt.localeCompare(left.createdAt));
  }

  children(id: string): Session[] {
    return [...this.entries.values()].map((entry) => entry.session).filter((session) => session.parentId === id);
  }

  /** Every session below this one, at any depth. */
  descendants(id: string): Set<string> {
    const found = new Set<string>();
    const queue = [id];
    while (queue.length > 0) {
      const current = queue.shift() as string;
      for (const child of this.children(current)) {
        if (!found.has(child.id)) {
          found.add(child.id);
          queue.push(child.id);
        }
      }
    }
    return found;
  }

  tree(id: string): SessionNode {
    this.entry(id);
    const ids = new Set([id, ...this.descendants(id)]);
    const flat = [...ids].map((sessionId) => this.entry(sessionId).session);
    const roots = buildSessionTree(flat);
    const node = roots.find((root) => root.id === id);
    if (!node) throw notFound(`Session tree for ${id}`);
    return node;
  }

  signal(id: string): AbortSignal {
    return this.entry(id).controller.signal;
  }

  /** Throw cancelled when the session (or an ancestor) was cancelled. */
  assertActive(id: string): void {
    if (this.entry(id).controller.signal.aborted) throw cancelled("This session");
  }

  update(
    id: string,
    patch: { status?: SessionStatus; summary?: string; error?: string | undefined; model?: string; force?: boolean },
  ): Session {
    const entry = this.entry(id);
    const before = entry.session;
    if (!patch.force && before.status === "cancelled" && patch.status && patch.status !== "cancelled") {
      // A cancelled session stays cancelled; late results from its work are dropped.
      return before;
    }
    const at = now();
    const next: Session = {
      ...before,
      ...(patch.status ? { status: patch.status } : {}),
      ...(patch.summary !== undefined ? { summary: clip(plainText(patch.summary), 600) } : {}),
      ...(patch.model ? { model: patch.model } : {}),
      updatedAt: at,
    };
    if (patch.error !== undefined) next.error = clip(patch.error, 2000);
    else if (patch.status && !["failed"].includes(patch.status)) delete next.error;
    if (patch.status && FINISHED.has(patch.status)) next.finishedAt = at;
    else if (patch.status) delete next.finishedAt;
    entry.session = sessionSchema.parse(next);
    this.persist(entry);
    if (patch.status !== undefined || patch.summary !== undefined || patch.error !== undefined) {
      this.emit(entry, {
        type: "session.status",
        sessionId: id,
        ...(entry.session.buildId ? { buildId: entry.session.buildId } : {}),
        data: {
          sessionId: id,
          status: entry.session.status,
          ...(entry.session.summary ? { summary: entry.session.summary } : {}),
          ...(entry.session.error ? { error: entry.session.error } : {}),
        },
      });
    }
    return entry.session;
  }

  /** Reserve a message id before streaming, so deltas and the final message match. */
  reserveMessageId(): string {
    return newId("message");
  }

  delta(id: string, messageId: string, text: string): void {
    if (!text) return;
    const entry = this.entry(id);
    this.emit(
      entry,
      {
        type: "message.delta",
        sessionId: id,
        ...(entry.session.buildId ? { buildId: entry.session.buildId } : {}),
        data: { sessionId: id, messageId, delta: text },
      },
      false,
    );
  }

  addMessage(id: string, input: AddMessageInput): ChatMessage {
    const entry = this.entry(id);
    const message = chatMessageSchema.parse({
      id: input.id ?? newId("message"),
      sessionId: id,
      role: input.role,
      ...(input.agent ? { agent: input.agent } : {}),
      content: clip(input.role === "user" ? input.content : plainText(input.content), 200_000),
      ...(input.usage ? { usage: input.usage } : {}),
      createdAt: now(),
    });
    entry.messages.push(message);
    entry.session = { ...entry.session, updatedAt: message.createdAt };
    this.persist(entry);
    this.emit(entry, {
      type: "message.completed",
      sessionId: id,
      ...(entry.session.buildId ? { buildId: entry.session.buildId } : {}),
      data: { message },
    });
    return message;
  }

  log(id: string, level: "debug" | "info" | "warn" | "error", message: string): void {
    const entry = this.entry(id);
    this.emit(entry, {
      type: "log",
      sessionId: id,
      ...(entry.session.buildId ? { buildId: entry.session.buildId } : {}),
      data: { level, message: clip(plainText(message), 4000) },
    });
  }

  /** Cancel a session and every session below it. Finished sessions keep their status. */
  cancel(id: string, reason = "Cancelled by the person"): Session {
    const entry = this.entry(id);
    const ids = [id, ...this.descendants(id)];
    entry.controller.abort(cancelled("This session"));
    for (const sessionId of ids) {
      const target = this.entry(sessionId);
      target.controller.abort(cancelled("This session"));
      if (!FINISHED.has(target.session.status)) {
        this.update(sessionId, { status: "cancelled", summary: reason });
      }
    }
    return this.entry(id).session;
  }

  /**
   * Re-arm a cancelled or finished session so new work can run in it (a
   * person writing to a session after cancelling it). Children stay as
   * they are.
   */
  revive(id: string): void {
    const entry = this.entry(id);
    if (entry.controller.signal.aborted) {
      const parent = entry.session.parentId ? this.entries.get(entry.session.parentId) : undefined;
      if (parent?.controller.signal.aborted) return;
      entry.controller = new AbortController();
      if (parent) {
        const parentSignal = parent.controller.signal;
        parentSignal.addEventListener("abort", () => entry.controller.abort(parentSignal.reason), { once: true });
      }
    }
    if (entry.session.status === "cancelled") this.update(id, { status: "idle", force: true });
  }
}
