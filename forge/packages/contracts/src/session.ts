import { z } from "zod";
import { agentIdSchema } from "./agents.js";
import {
  buildIdSchema,
  isoDateTimeSchema,
  messageIdSchema,
  sessionIdSchema,
} from "./common.js";
import {
  buildStatusSchema,
  checkpointSchema,
  layerSchema,
  previewSchema,
  receiptSchema,
  sprintSchema,
} from "./build.js";
import { seedSchema } from "./atlas.js";
import { modelUsageSchema } from "./models.js";
import { sandboxRunSchema, testFlightSchema } from "./sandbox.js";

/**
 * Sessions, chat messages and the SSE event stream.
 *
 * UX reference (SPEC "Design rules"): Claude Code. One conversation drives
 * the work and nested agents show as a collapsible tree with live status.
 * A session is one agent working on one task; parentId nests it.
 */

export const sessionKindSchema = z.enum([
  "imagistation",
  "build",
  "layer",
  "mod",
  "library",
  "chat",
]);
export type SessionKind = z.infer<typeof sessionKindSchema>;

export const sessionStatusSchema = z.enum([
  "idle",
  "queued",
  "running",
  "waiting",
  "done",
  "failed",
  "cancelled",
]);
export type SessionStatus = z.infer<typeof sessionStatusSchema>;

/** What a session is about, so a Library click can open the right one. */
export const sessionSubjectSchema = z.object({
  type: z.enum(["seed", "build", "layer", "mod", "adapter", "augment", "repo", "project"]),
  id: z.string().min(1).max(200),
  label: z.string().max(200).optional(),
});
export type SessionSubject = z.infer<typeof sessionSubjectSchema>;

export const chatRoleSchema = z.enum(["user", "assistant", "system", "tool"]);
export type ChatRole = z.infer<typeof chatRoleSchema>;

export const toolCallSchema = z.object({
  id: z.string().min(1).max(200),
  name: z.string().min(1).max(200),
  /** Arguments as the model sent them, already parsed. */
  args: z.unknown(),
});
export type ToolCall = z.infer<typeof toolCallSchema>;

export const chatMessageSchema = z.object({
  id: messageIdSchema,
  sessionId: sessionIdSchema,
  role: chatRoleSchema,
  agent: agentIdSchema.optional(),
  content: z.string().max(200_000),
  toolCalls: z.array(toolCallSchema).optional(),
  /** For role tool: which call this answers. */
  toolCallId: z.string().max(200).optional(),
  usage: modelUsageSchema.optional(),
  createdAt: isoDateTimeSchema,
});
export type ChatMessage = z.infer<typeof chatMessageSchema>;

export const sendMessageRequestSchema = z.object({
  content: z.string().trim().min(1, "Write a message first").max(20_000),
});
export type SendMessageRequest = z.infer<typeof sendMessageRequestSchema>;

/* ------------------------------------------------------------------ */
/* Events. Every SSE frame is one SessionEvent: `event: <type>` and     */
/* `data: <json of the whole envelope>`.                               */
/* ------------------------------------------------------------------ */

const envelope = {
  /** Monotonic per stream; also sent as the SSE id for Last-Event-ID. */
  id: z.string().min(1).max(120),
  at: isoDateTimeSchema,
  sessionId: sessionIdSchema.optional(),
  buildId: buildIdSchema.optional(),
};

function event<T extends string, D extends z.ZodType>(type: T, data: D) {
  return z.object({ ...envelope, type: z.literal(type), data });
}

// Sessions are recursive in the tree view but flat on the wire, so the
// session schema is declared before the event union and the events carry
// flat sessions.
export const sessionSchema = z.object({
  id: sessionIdSchema,
  parentId: sessionIdSchema.nullable(),
  /** The top of this session's tree (itself for a root). */
  rootId: sessionIdSchema,
  agent: agentIdSchema,
  kind: sessionKindSchema,
  /** The task in plain words, as the parent gave it. */
  task: z.string().min(1).max(4000),
  status: sessionStatusSchema,
  subject: sessionSubjectSchema.optional(),
  buildId: buildIdSchema.optional(),
  model: z.string().max(200).optional(),
  /** Latest one-line status, shown in the tree. */
  summary: z.string().max(600).optional(),
  createdAt: isoDateTimeSchema,
  updatedAt: isoDateTimeSchema,
  finishedAt: isoDateTimeSchema.optional(),
  error: z.string().max(2000).optional(),
});
export type Session = z.infer<typeof sessionSchema>;

export const sessionEventSchema = z.discriminatedUnion("type", [
  event("session.created", z.object({ session: sessionSchema })),
  event(
    "session.status",
    z.object({
      sessionId: sessionIdSchema,
      status: sessionStatusSchema,
      summary: z.string().max(600).optional(),
      error: z.string().max(2000).optional(),
    }),
  ),
  event(
    "message.delta",
    z.object({ sessionId: sessionIdSchema, messageId: messageIdSchema, delta: z.string() }),
  ),
  event("message.completed", z.object({ message: chatMessageSchema })),
  event(
    "tool.call",
    z.object({ sessionId: sessionIdSchema, call: toolCallSchema }),
  ),
  event(
    "tool.result",
    z.object({
      sessionId: sessionIdSchema,
      callId: z.string().min(1).max(200),
      ok: z.boolean(),
      summary: z.string().max(2000),
    }),
  ),
  event("seed.updated", z.object({ seed: seedSchema })),
  event("build.status", z.object({ buildId: buildIdSchema, status: buildStatusSchema, error: z.string().max(2000).optional() })),
  event("layer.updated", z.object({ layer: layerSchema })),
  event("sprint.updated", z.object({ sprint: sprintSchema })),
  event("checkpoint.opened", z.object({ checkpoint: checkpointSchema })),
  event("checkpoint.resolved", z.object({ checkpoint: checkpointSchema })),
  event("preview.updated", z.object({ buildId: buildIdSchema, preview: previewSchema })),
  event("receipt.recorded", z.object({ receipt: receiptSchema })),
  event("sandbox.updated", z.object({ run: sandboxRunSchema })),
  event("testflight.updated", z.object({ testFlight: testFlightSchema })),
  event(
    "log",
    z.object({
      level: z.enum(["debug", "info", "warn", "error"]),
      message: z.string().max(4000),
    }),
  ),
  event(
    "error",
    z.object({
      code: z.string().min(1).max(80),
      message: z.string().max(2000),
    }),
  ),
  event("heartbeat", z.object({})),
]);
export type SessionEvent = z.infer<typeof sessionEventSchema>;
/** Every event type, in union order; the SSE client listens for each. */
export const SESSION_EVENT_TYPES = sessionEventSchema.options.map(
  (option) => option.shape.type.value,
) as Array<SessionEvent["type"]>;
export type SessionEventType = SessionEvent["type"];
export type SessionEventOf<T extends SessionEventType> = Extract<SessionEvent, { type: T }>;

/** A session with its children, for the nested tree. */
export type SessionNode = Session & { children: SessionNode[] };
export const sessionNodeSchema: z.ZodType<SessionNode> = z.lazy(() =>
  sessionSchema.extend({ children: z.array(sessionNodeSchema) }),
);

/** Build a tree from flat sessions; orphans become roots. */
export function buildSessionTree(sessions: readonly Session[]): SessionNode[] {
  const nodes = new Map<string, SessionNode>();
  for (const session of sessions) nodes.set(session.id, { ...session, children: [] });
  const roots: SessionNode[] = [];
  for (const node of nodes.values()) {
    const parent = node.parentId ? nodes.get(node.parentId) : undefined;
    if (parent) parent.children.push(node);
    else roots.push(node);
  }
  const byCreated = (left: SessionNode, right: SessionNode) =>
    left.createdAt.localeCompare(right.createdAt);
  const sortTree = (list: SessionNode[]) => {
    list.sort(byCreated);
    for (const node of list) sortTree(node.children);
  };
  sortTree(roots);
  return roots;
}

export const createSessionRequestSchema = z.object({
  agent: agentIdSchema,
  kind: sessionKindSchema,
  task: z.string().trim().min(1).max(4000),
  parentId: sessionIdSchema.optional(),
  subject: sessionSubjectSchema.optional(),
  buildId: buildIdSchema.optional(),
});
export type CreateSessionRequest = z.infer<typeof createSessionRequestSchema>;
