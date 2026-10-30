import { STAGE_LABELS, type ChatMessage, type CreateSessionRequest, type Session } from "@4ge/contracts";
import type { DataPaths } from "@4ge/contracts/node";
import { AGENT_DEFINITIONS } from "./agents.js";
import type { BuildRunner } from "./builds.js";
import { readOrchestration } from "./design.js";
import { OrchestraError, isOrchestraError, messageOf } from "./errors.js";
import type { Imagistation } from "./imagistation.js";
import type { ModelClient } from "./model.js";
import { MarkerSplitter, extractJson } from "./parse.js";
import type { SessionManager } from "./sessions.js";
import { clip } from "./util.js";

/**
 * Conversation in any session that is not Imagistation: the IDE talking to
 * Smith about the build, and Library sessions about a mod, adapter, augment,
 * repo or project. Smith can act from the conversation by ending a reply
 * with an ACTIONS block (rebuild a layer with a note, cancel the build);
 * the action runs through the build runner and its result is posted back.
 */

const ACTIONS_MARKER = "<<<ACTIONS>>>";

export interface ChatDeps {
  paths: DataPaths;
  sessions: SessionManager;
  models: ModelClient;
  builds: BuildRunner;
  imagistation: Imagistation;
}

type Action = { type: "rebuild-layer"; layer: string; note: string } | { type: "cancel-build" };

function readActions(hidden: string): Action[] {
  if (!hidden.trim()) return [];
  try {
    const value = extractJson(hidden);
    const list = Array.isArray(value) ? value : [];
    return list.flatMap((item): Action[] => {
      if (!item || typeof item !== "object") return [];
      const record = item as Record<string, unknown>;
      if (record.type === "rebuild-layer" && typeof record.layer === "string") {
        return [{ type: "rebuild-layer", layer: record.layer, note: typeof record.note === "string" ? record.note : "" }];
      }
      if (record.type === "cancel-build") return [{ type: "cancel-build" }];
      return [];
    });
  } catch {
    return [];
  }
}

export class Conversations {
  constructor(private readonly deps: ChatDeps) {}

  private buildDigest(buildId: string): string {
    const build = this.deps.builds.get(buildId);
    const receipts = this.deps.builds.receipts(buildId);
    const checkpoint = build.openCheckpointId
      ? this.deps.builds.checkpoints(buildId).find((item) => item.id === build.openCheckpointId)
      : undefined;
    const proposals = readOrchestration(this.deps.paths, buildId);
    return JSON.stringify(
      {
        build: { id: build.id, title: build.title, status: build.status, error: build.error },
        stages: build.stages.map((stage) => ({ stage: STAGE_LABELS[stage.stage], status: stage.status })),
        layers: build.layers.map((layer) => ({
          key: layer.key,
          title: layer.title,
          stage: layer.stage,
          agent: layer.agent,
          status: layer.status,
          error: layer.error ? clip(layer.error, 300) : undefined,
          receipt: layer.receiptId,
        })),
        gate: receipts.gate.map((decision) => ({ stage: decision.stage, admitted: decision.admitted, reason: decision.reason })),
        openCheckpoint: checkpoint ? { title: checkpoint.title, question: checkpoint.question, summary: checkpoint.summary } : null,
        orchestrationProposals: proposals.map((proposal) => proposal.title),
      },
      null,
      1,
    );
  }

  private systemFor(session: Session): string {
    const agent = AGENT_DEFINITIONS[session.agent];
    const parts = [agent.system];
    if (session.buildId && this.deps.builds.has(session.buildId)) {
      parts.push(`The build right now, as JSON (only state what this says):\n${this.buildDigest(session.buildId)}`);
      if (session.agent === "smith") {
        parts.push(
          `You can act. When the person asks you to change or redo a layer, or to stop the build, reply first, then on its own line write ${ACTIONS_MARKER} followed by a JSON array, e.g. [{"type":"rebuild-layer","layer":"<layer key>","note":"what to change, in the person's words"}] or [{"type":"cancel-build"}]. Only act when the person asked for it. The person never sees the part after the marker.`,
        );
      }
    }
    if (session.subject) {
      parts.push(`This session is about ${session.subject.type} ${session.subject.label ?? session.subject.id} (${session.subject.id}). Task: ${session.task}`);
    }
    return parts.join("\n\n");
  }

  /** Open a session (a Library click, a question about a build). Smith's opening streams in the background. */
  create(request: CreateSessionRequest, background: (work: Promise<unknown>) => void): Session {
    if (request.kind === "imagistation") {
      throw new OrchestraError("bad_request", "Open Imagistation sessions with POST /imagistation/sessions");
    }
    if (request.buildId && !this.deps.builds.has(request.buildId)) throw new OrchestraError("not_found", `Build ${request.buildId} was not found`);
    const session = this.deps.sessions.create({
      agent: request.agent,
      kind: request.kind,
      task: request.task,
      parentId: request.parentId,
      subject: request.subject,
      buildId: request.buildId,
      status: "running",
      summary: "Opening",
    });
    background(
      this.respond(session.id, `Open this session: ${request.task}. Say in two or three sentences what you can do here, then ask what the person wants first.`, {
        hiddenPrompt: true,
      }).catch((error: unknown) => {
        this.deps.sessions.addMessage(session.id, { role: "system", content: messageOf(error) });
        this.deps.sessions.update(session.id, { status: "waiting", summary: "No answer yet", error: messageOf(error) });
      }),
    );
    return session;
  }

  async send(sessionId: string, content: string): Promise<{ message: ChatMessage; reply: ChatMessage }> {
    const session = this.deps.sessions.get(sessionId);
    if (session.kind === "imagistation") return this.deps.imagistation.send(sessionId, content);
    const { message, reply } = await this.respond(sessionId, content, { hiddenPrompt: false });
    if (!message) throw new OrchestraError("internal", "The message was not recorded");
    return { message, reply };
  }

  private async respond(
    sessionId: string,
    content: string,
    options: { hiddenPrompt: boolean },
  ): Promise<{ message?: ChatMessage; reply: ChatMessage }> {
    const session = this.deps.sessions.get(sessionId);
    const text = content.trim();
    if (!text) throw new OrchestraError("bad_request", "Write a message first");
    this.deps.sessions.revive(sessionId);
    const history = this.deps.sessions
      .messages(sessionId)
      .filter((item) => item.role === "user" || item.role === "assistant")
      .slice(-30);
    const message = options.hiddenPrompt ? undefined : this.deps.sessions.addMessage(sessionId, { role: "user", content: text });
    this.deps.sessions.update(sessionId, { status: "running", summary: `${AGENT_DEFINITIONS[session.agent].profile.name} is answering` });
    const replyId = this.deps.sessions.reserveMessageId();
    const splitter = new MarkerSplitter(ACTIONS_MARKER);
    let result;
    try {
      result = await this.deps.models.chat({
        tier: AGENT_DEFINITIONS[session.agent].profile.tier === "worker" ? "worker" : "lead",
        purpose: options.hiddenPrompt ? "session.open" : "session.turn",
        messages: [
          { role: "system", content: this.systemFor(session) },
          ...history.map((item) => ({ role: item.role as "user" | "assistant", content: item.content })),
          { role: "user", content: text },
        ],
        maxTokens: 1500,
        temperature: 0.4,
        signal: this.deps.sessions.signal(sessionId),
        onDelta: (piece) => this.deps.sessions.delta(sessionId, replyId, splitter.push(piece)),
      });
    } catch (error) {
      this.deps.sessions.update(sessionId, {
        status: isOrchestraError(error, "cancelled") ? "cancelled" : "waiting",
        summary: "No answer",
        error: messageOf(error),
      });
      throw error;
    }
    const tail = splitter.finish();
    if (tail.flushed) this.deps.sessions.delta(sessionId, replyId, tail.flushed);
    const at = result.text.indexOf(ACTIONS_MARKER);
    const visible = (at >= 0 ? result.text.slice(0, at) : result.text).trim();
    const actions = session.agent === "smith" && session.buildId ? readActions(at >= 0 ? result.text.slice(at + ACTIONS_MARKER.length) : "") : [];
    const reply = this.deps.sessions.addMessage(sessionId, {
      id: replyId,
      role: "assistant",
      agent: session.agent,
      content: visible || "I am here. What should I do first?",
      usage: result.usage,
    });
    const outcomes: string[] = [];
    for (const action of actions) {
      try {
        if (action.type === "rebuild-layer") {
          const layer = await this.deps.builds.rebuildLayer(session.buildId as string, action.layer, action.note || text);
          outcomes.push(`Rebuilding ${layer.title}.`);
        } else {
          this.deps.builds.cancel(session.buildId as string);
          outcomes.push("The build is cancelled.");
        }
      } catch (error) {
        outcomes.push(`Could not ${action.type === "rebuild-layer" ? `rebuild ${action.layer}` : "cancel the build"}: ${messageOf(error)}`);
      }
    }
    if (outcomes.length > 0) this.deps.sessions.addMessage(sessionId, { role: "system", content: outcomes.join(" ") });
    const after = this.deps.sessions.get(sessionId);
    if (session.kind === "build" && session.buildId && this.deps.builds.has(session.buildId)) {
      // The root session mirrors its build, not the chat turn.
      const build = this.deps.builds.get(session.buildId);
      const running = build.status === "running" || build.status === "queued" || build.status === "test-flight";
      if (after.status === "running" || after.status === "waiting") {
        this.deps.sessions.update(sessionId, {
          status: running ? "running" : build.status === "passed" ? "done" : "waiting",
          summary: session.summary ?? "Waiting for you",
        });
      }
    } else if (after.status === "running") {
      this.deps.sessions.update(sessionId, { status: "waiting", model: result.model, summary: "Waiting for you" });
    }
    return { ...(message ? { message } : {}), reply };
  }
}
