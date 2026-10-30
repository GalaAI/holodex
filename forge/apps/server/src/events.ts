import { sessionEventSchema, type SessionEvent } from "@4ge/contracts";

/**
 * In-process event hub. Layers publish SessionEvents here; SSE routes
 * subscribe with a filter. A ring buffer keeps recent events so a client
 * that reconnects with Last-Event-ID gets what it missed.
 *
 * Event payloads are validated on publish, so a malformed event fails in the
 * layer that produced it instead of in every browser.
 */
export type EventDraft = SessionEvent extends infer E
  ? E extends SessionEvent
    ? Omit<E, "id" | "at"> & { at?: string }
    : never
  : never;

export type EventFilter = (event: SessionEvent) => boolean;

export class EventHub {
  private sequence = 0;
  private readonly buffer: SessionEvent[] = [];
  private readonly listeners = new Set<(event: SessionEvent) => void>();

  constructor(private readonly capacity = 2000) {}

  publish(draft: EventDraft): SessionEvent {
    this.sequence += 1;
    const event = sessionEventSchema.parse({
      ...draft,
      id: String(this.sequence),
      at: draft.at ?? new Date().toISOString(),
    });
    this.buffer.push(event);
    if (this.buffer.length > this.capacity) this.buffer.splice(0, this.buffer.length - this.capacity);
    for (const listener of this.listeners) {
      try {
        listener(event);
      } catch {
        // One broken subscriber must not stop the others.
      }
    }
    return event;
  }

  /** Events after `lastId` that match the filter, oldest first. */
  replay(lastId: string | undefined, filter: EventFilter): SessionEvent[] {
    if (!lastId) return [];
    const after = Number(lastId);
    if (!Number.isFinite(after)) return [];
    return this.buffer.filter((event) => Number(event.id) > after && filter(event));
  }

  subscribe(filter: EventFilter, listener: (event: SessionEvent) => void): () => void {
    const wrapped = (event: SessionEvent) => {
      if (filter(event)) listener(event);
    };
    this.listeners.add(wrapped);
    return () => {
      this.listeners.delete(wrapped);
    };
  }

  get size(): number {
    return this.listeners.size;
  }
}

/** Filters for the two stream routes. */
export const forSession =
  (sessionId: string, descendants: () => ReadonlySet<string> = () => new Set()) =>
  (event: SessionEvent): boolean =>
    event.sessionId === sessionId || (event.sessionId !== undefined && descendants().has(event.sessionId));

export const forBuild = (buildId: string) => (event: SessionEvent): boolean => event.buildId === buildId;
