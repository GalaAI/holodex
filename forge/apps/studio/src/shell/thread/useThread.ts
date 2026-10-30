import type { ChatMessage, Session, SessionEvent, ToolCall } from "@4ge/contracts";
import { useCallback, useEffect, useRef, useState } from "react";
import { ForgeApiError, api } from "../../lib/api.js";
import { useRouteStream, type EventStreamState } from "../../lib/sse.js";

/**
 * One conversation with one agent session, kept live. Loads the session's
 * messages, follows /sessions/:id/stream for token deltas, tool calls and
 * status, and sends through the session's own route (Imagistation talks to
 * Prometheus on its own route; every other session uses /sessions/:id).
 *
 * Pattern borrowed from the 21st.dev "Message Thread" and "Subagent Tool"
 * components (follow streaming output, live tool rows), rebuilt on the
 * contracts and Phosphor rather than copied, since those ship Tailwind.
 */
export type ThreadSource = "imagistation" | "session";

export interface LiveTool {
  call: ToolCall;
  at: string;
  result?: { ok: boolean; summary: string };
}

export interface ThreadState {
  session: Session | undefined;
  messages: ChatMessage[];
  /** The user's message while the send is in flight. */
  pending: string | undefined;
  /** Streaming assistant text by message id. */
  drafts: Record<string, string>;
  liveTools: LiveTool[];
  loading: boolean;
  loadError: ForgeApiError | undefined;
  sendError: ForgeApiError | undefined;
  sending: boolean;
  stream: EventStreamState;
  send: (content: string) => Promise<boolean>;
  cancel: () => Promise<void>;
  reload: () => void;
}

function toApiError(failure: unknown, route: string): ForgeApiError {
  return failure instanceof ForgeApiError
    ? failure
    : new ForgeApiError({
        status: 0,
        code: "network",
        message: failure instanceof Error ? failure.message : String(failure),
        route,
      });
}

function upsert(list: ChatMessage[], message: ChatMessage): ChatMessage[] {
  const without = list.filter((item) => item.id !== message.id);
  without.push(message);
  return without.sort((left, right) => left.createdAt.localeCompare(right.createdAt));
}

/** Whether an event belongs to this session's own conversation. */
export function eventSessionId(event: SessionEvent): string | undefined {
  const data = event.data as { sessionId?: string; message?: { sessionId?: string }; session?: { id?: string } };
  return data.sessionId ?? data.message?.sessionId ?? data.session?.id ?? event.sessionId;
}

export function useThread(
  sessionId: string | null,
  source: ThreadSource,
  onEvent?: (event: SessionEvent) => void,
): ThreadState {
  const [session, setSession] = useState<Session>();
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [pending, setPending] = useState<string>();
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [liveTools, setLiveTools] = useState<LiveTool[]>([]);
  const [loading, setLoading] = useState(sessionId !== null);
  const [loadError, setLoadError] = useState<ForgeApiError>();
  const [sendError, setSendError] = useState<ForgeApiError>();
  const [sending, setSending] = useState(false);
  const [tick, setTick] = useState(0);
  const forward = useRef(onEvent);
  forward.current = onEvent;

  useEffect(() => {
    setMessages([]);
    setDrafts({});
    setLiveTools([]);
    setSession(undefined);
    setSendError(undefined);
    if (!sessionId) {
      setLoading(false);
      return;
    }
    const controller = new AbortController();
    setLoading(true);
    const request =
      source === "imagistation"
        ? api.call("imagistation.sessions.get", { params: { id: sessionId }, signal: controller.signal })
        : api.call("sessions.get", { params: { id: sessionId }, signal: controller.signal });
    request
      .then((result) => {
        setSession(result.session);
        setMessages([...result.messages].sort((left, right) => left.createdAt.localeCompare(right.createdAt)));
        setLoadError(undefined);
      })
      .catch((failure: unknown) => {
        if (!controller.signal.aborted) setLoadError(toApiError(failure, "sessions.get"));
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [sessionId, source, tick]);

  const handle = useCallback(
    (event: SessionEvent) => {
      forward.current?.(event);
      if (!sessionId || eventSessionId(event) !== sessionId) return;
      switch (event.type) {
        case "message.delta":
          setDrafts((previous) => ({
            ...previous,
            [event.data.messageId]: (previous[event.data.messageId] ?? "") + event.data.delta,
          }));
          break;
        case "message.completed": {
          const message = event.data.message;
          setMessages((previous) => upsert(previous, message));
          // The server echoes the user's own message; drop the optimistic copy.
          if (message.role === "user") setPending((current) => (current === message.content.trim() ? undefined : current));
          setDrafts((previous) => {
            const next = { ...previous };
            delete next[message.id];
            return next;
          });
          const done = new Set((message.toolCalls ?? []).map((call) => call.id));
          if (message.toolCallId) done.add(message.toolCallId);
          if (done.size > 0) setLiveTools((previous) => previous.filter((tool) => !done.has(tool.call.id)));
          break;
        }
        case "tool.call":
          setLiveTools((previous) => [
            ...previous.filter((tool) => tool.call.id !== event.data.call.id),
            { call: event.data.call, at: event.at },
          ]);
          break;
        case "tool.result":
          setLiveTools((previous) =>
            previous.map((tool) =>
              tool.call.id === event.data.callId
                ? { ...tool, result: { ok: event.data.ok, summary: event.data.summary } }
                : tool,
            ),
          );
          break;
        case "session.status":
          setSession((previous) =>
            previous
              ? {
                  ...previous,
                  status: event.data.status,
                  ...(event.data.summary !== undefined ? { summary: event.data.summary } : {}),
                  ...(event.data.error !== undefined ? { error: event.data.error } : {}),
                }
              : previous,
          );
          break;
        case "session.created":
          if (event.data.session.id === sessionId) setSession(event.data.session);
          break;
        default:
          break;
      }
    },
    [sessionId],
  );

  const stream = useRouteStream("sessions.stream", sessionId ? { id: sessionId } : null, handle);

  const send = useCallback(
    async (content: string): Promise<boolean> => {
      const text = content.trim();
      if (!sessionId || text.length === 0 || sending) return false;
      setSending(true);
      setPending(text);
      setSendError(undefined);
      try {
        const result =
          source === "imagistation"
            ? await api.call("imagistation.messages.send", { params: { id: sessionId }, body: { content: text } })
            : await api.call("sessions.messages.send", { params: { id: sessionId }, body: { content: text } });
        setMessages((previous) => upsert(upsert(previous, result.message), result.reply));
        setDrafts((previous) => {
          const next = { ...previous };
          delete next[result.reply.id];
          return next;
        });
        return true;
      } catch (failure) {
        setSendError(toApiError(failure, "messages.send"));
        return false;
      } finally {
        setPending(undefined);
        setSending(false);
      }
    },
    [sessionId, source, sending],
  );

  const cancel = useCallback(async () => {
    if (!sessionId) return;
    try {
      const result = await api.call("sessions.cancel", { params: { id: sessionId } });
      setSession(result.session);
    } catch (failure) {
      setSendError(toApiError(failure, "sessions.cancel"));
    }
  }, [sessionId]);

  const reload = useCallback(() => setTick((value) => value + 1), []);

  return { session, messages, pending, drafts, liveTools, loading, loadError, sendError, sending, stream, send, cancel, reload };
}
