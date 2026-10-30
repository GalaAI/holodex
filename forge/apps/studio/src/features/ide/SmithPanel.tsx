import type { ChatMessage, Session } from "@4ge/contracts";
import { Eyebrow, StreamCaret } from "@4ge/kit";
import { Maximize2, Minimize2 } from "lucide-react";
import { ModelNotice } from "../../shell/models.js";
import { Composer, Thread } from "../../shell/thread/Thread.js";
import type { ThreadState } from "../../shell/thread/useThread.js";
import { ErrorNotice, StatusLed, agentName } from "../../shell/ui.js";

/**
 * The left Smith panel: the build-wide conversation, the one place the user
 * talks to the orchestra. It folds into a compact HUD over the canvas so
 * the board can take the whole screen.
 */
export interface SmithPanelProps {
  thread: ThreadState;
  hasSession: boolean;
  mode: "panel" | "hud";
  onMode: (mode: "panel" | "hud") => void;
  finished: boolean;
  /** Open the frame thread a message belongs to. */
  onOpenThreadMarker: (annotationId: string) => void;
}

const MARKER = /^\[thread (ann_[A-Za-z0-9_-]+)\]/;

export function SmithPanel({ thread, hasSession, mode, onMode, finished, onOpenThreadMarker }: SmithPanelProps) {
  const disabledReason = !hasSession
    ? "Smith has no session for this build yet."
    : thread.session?.status === "cancelled"
      ? "Smith's session was cancelled."
      : undefined;
  const decorate = (message: ChatMessage) => {
    const match = MARKER.exec(message.content);
    if (!match?.[1]) return null;
    const id = match[1];
    return (
      <button type="button" className="thread__chip" onClick={() => onOpenThreadMarker(id)}>
        Frame thread {id}
      </button>
    );
  };

  if (mode === "hud") {
    const last = [...thread.messages].reverse().find((message) => message.role === "assistant");
    const streaming = Object.values(thread.drafts).at(-1);
    return (
      <section className="hud" aria-label="Smith" data-testid="smith-hud">
        <header className="hud__head">
          <Eyebrow>Smith // HUD</Eyebrow>
          {thread.session ? <StatusLed status={thread.session.status} /> : null}
          <button type="button" className="board__icon" aria-label="Open the Smith panel" onClick={() => onMode("panel")}>
            <Maximize2 size={14} aria-hidden="true" />
          </button>
        </header>
        <p className="hud__last">
          {streaming ? (
            <>
              {streaming.slice(-280)}
              <StreamCaret />
            </>
          ) : last ? (
            last.content.slice(0, 280)
          ) : (
            "Smith has not said anything yet."
          )}
        </p>
        {thread.sendError ? <ErrorNotice error={thread.sendError} compact /> : null}
        <Composer
          compact
          label="Message Smith"
          placeholder="Tell Smith what to change"
          busy={thread.sending}
          onSend={thread.send}
          disabled={disabledReason !== undefined}
          {...(disabledReason ? { disabledReason } : {})}
        />
      </section>
    );
  }

  return (
    <section className="smith" aria-label="Smith" data-testid="smith-panel">
      <header className="smith__head">
        <div>
          <Eyebrow>Smith // build lead</Eyebrow>
          <div className="smith__status">
            {thread.session ? <StatusLed status={thread.session.status} /> : null}
            {thread.session?.summary ? <span className="smith__summary">{thread.session.summary}</span> : null}
          </div>
        </div>
        <button type="button" className="board__icon" aria-label="Fold Smith into a HUD" onClick={() => onMode("hud")}>
          <Minimize2 size={14} aria-hidden="true" />
        </button>
      </header>
      <ModelNotice agent="Smith" />
      <Thread
        thread={thread}
        agent="smith"
        decorate={decorate}
        empty={
          <p className="smith__muted">
            {hasSession
              ? "Smith reads the Seed and starts. Ask about the design system, the landing page or anything the orchestra is doing."
              : "Smith opens its session when the build starts."}
          </p>
        }
      />
      {thread.sendError ? <ErrorNotice error={thread.sendError} compact /> : null}
      <Composer
        label="Message Smith"
        placeholder={finished ? "Ask Smith to tune or explain the build" : "Talk to Smith about the build"}
        busy={thread.sending}
        onSend={thread.send}
        disabled={disabledReason !== undefined}
        {...(disabledReason ? { disabledReason } : {})}
      />
    </section>
  );
}

/** A delegated agent's conversation, read only: the user talks to Smith, not to workers. */
export function SessionThread({ thread, session, onClose }: { thread: ThreadState; session: Session | undefined; onClose: () => void }) {
  const agent = thread.session?.agent ?? session?.agent ?? "smith";
  return (
    <section className="subthread" aria-label={`${agentName(agent)} thread`} data-testid="session-thread">
      <header className="subthread__head">
        <div>
          <Eyebrow>{`${agentName(agent)} // read only`}</Eyebrow>
          <p className="subthread__task">{thread.session?.task ?? session?.task}</p>
        </div>
        <button type="button" className="tree__link" onClick={onClose}>
          Back to the tree
        </button>
      </header>
      <Thread thread={thread} agent={agent} empty={<p className="smith__muted">No messages in this session yet.</p>} />
      <p className="smith__muted">To change what this agent does, tell Smith.</p>
    </section>
  );
}
