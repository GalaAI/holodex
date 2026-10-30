import {
  SESSION_EVENT_TYPES,
  sessionEventSchema,
  type RouteParams,
  type SessionEvent,
  type StreamRouteName,
} from "@4ge/contracts";
import { useEffect, useRef, useState } from "react";
import { api } from "./api.js";

/**
 * Live SessionEvents over SSE (EventSource). Every frame is validated
 * against the contract; a frame that does not parse is dropped and counted,
 * never rendered. EventSource reconnects on its own and sends
 * Last-Event-ID, so the server replays what was missed.
 */
export type StreamStatus = "idle" | "connecting" | "open" | "reconnecting" | "closed";

export interface EventStreamState {
  status: StreamStatus;
  /** Frames that failed validation. */
  dropped: number;
  lastEventAt: string | undefined;
}

export function useEventStream(url: string | null, onEvent: (event: SessionEvent) => void): EventStreamState {
  const handler = useRef(onEvent);
  handler.current = onEvent;
  const [state, setState] = useState<EventStreamState>({ status: url ? "connecting" : "idle", dropped: 0, lastEventAt: undefined });

  useEffect(() => {
    if (!url) {
      setState((previous) => ({ ...previous, status: "idle" }));
      return;
    }
    setState((previous) => ({ ...previous, status: "connecting" }));
    const source = new EventSource(url);
    const receive = (message: MessageEvent<string>) => {
      let raw: unknown;
      try {
        raw = JSON.parse(message.data);
      } catch {
        setState((previous) => ({ ...previous, dropped: previous.dropped + 1 }));
        return;
      }
      const parsed = sessionEventSchema.safeParse(raw);
      if (!parsed.success) {
        setState((previous) => ({ ...previous, dropped: previous.dropped + 1 }));
        return;
      }
      setState((previous) => ({ ...previous, lastEventAt: parsed.data.at }));
      handler.current(parsed.data);
    };
    source.addEventListener("ready", () => setState((previous) => ({ ...previous, status: "open" })));
    source.onopen = () => setState((previous) => ({ ...previous, status: "open" }));
    source.onerror = () =>
      setState((previous) => ({
        ...previous,
        status: source.readyState === EventSource.CLOSED ? "closed" : "reconnecting",
      }));
    for (const type of SESSION_EVENT_TYPES) source.addEventListener(type, receive as EventListener);
    return () => {
      source.close();
    };
  }, [url]);

  return state;
}

/** Stream one of the contract's SSE routes, e.g. useRouteStream("sessions.stream", { id }, onEvent). */
export function useRouteStream<N extends StreamRouteName>(
  name: N,
  params: RouteParams<N> | null,
  onEvent: (event: SessionEvent) => void,
): EventStreamState {
  const url = params ? api.streamUrl(name, params) : null;
  return useEventStream(url, onEvent);
}
