import type { AgentId, ChatMessage } from "@4ge/contracts";
import { Button, CadenceDots, Eyebrow, StreamCaret } from "@4ge/kit";
import { ChevronRight, Wrench } from "lucide-react";
import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { ErrorNotice, agentName } from "../ui.js";
import type { LiveTool, ThreadState } from "./useThread.js";

/**
 * The conversation surface shared by Imagistation (Prometheus) and the IDE
 * (Smith): one column of messages that follows streaming output, tool calls
 * folded into rows you can open, and one composer. Summary first, detail on
 * demand.
 */
export interface ThreadProps {
  thread: ThreadState;
  agent: AgentId;
  /** Shown when the session has no messages yet. */
  empty?: ReactNode;
  /** Extra content rendered under a message, e.g. a thread chip. */
  decorate?: (message: ChatMessage) => ReactNode;
  className?: string;
}

export function Thread({ thread, agent, empty, decorate, className }: ThreadProps) {
  const scroller = useRef<HTMLDivElement>(null);
  const following = useRef(true);
  // System notes are the server's plain-words reasons (no model, a model error, what an action did); they show in place.
  const visible = thread.messages.filter((message) => message.role === "user" || message.role === "assistant" || message.role === "system");
  const toolResults = new Map(
    thread.messages
      .filter((message) => message.role === "tool" && message.toolCallId)
      .map((message) => [message.toolCallId as string, message.content]),
  );
  const draftEntries = Object.entries(thread.drafts);
  const waiting = thread.sending && draftEntries.length === 0;

  useLayoutEffect(() => {
    const element = scroller.current;
    if (element && following.current) element.scrollTop = element.scrollHeight;
  });

  return (
    <div
      ref={scroller}
      className={`thread${className ? ` ${className}` : ""}`}
      aria-live="polite"
      aria-busy={thread.sending || draftEntries.length > 0}
      data-testid="thread"
      onScroll={(event) => {
        const element = event.currentTarget;
        following.current = element.scrollHeight - element.scrollTop - element.clientHeight < 48;
      }}
    >
      {thread.loading ? (
        <div className="thread__status" role="status">
          <CadenceDots tone="cream" label="Loading the conversation" />
        </div>
      ) : null}
      {thread.loadError ? <ErrorNotice error={thread.loadError} onRetry={thread.reload} compact /> : null}
      {!thread.loading && !thread.loadError && visible.length === 0 && !thread.pending ? (
        <div className="thread__empty">{empty}</div>
      ) : null}
      <ol className="thread__list">
        {visible.map((message) => (
          <li key={message.id} className={`thread__msg thread__msg--${message.role}`} data-role={message.role}>
            <MessageHead role={message.role} agent={message.agent ?? agent} />
            {message.content ? <div className="thread__body">{message.content}</div> : null}
            {message.toolCalls?.length ? (
              <ul className="thread__tools">
                {message.toolCalls.map((call) => (
                  <ToolRow
                    key={call.id}
                    name={call.name}
                    args={call.args}
                    result={toolResults.has(call.id) ? { ok: true, summary: toolResults.get(call.id) as string } : undefined}
                  />
                ))}
              </ul>
            ) : null}
            {decorate?.(message)}
          </li>
        ))}
        {thread.pending ? (
          <li className="thread__msg thread__msg--user is-pending" data-role="user">
            <MessageHead role="user" agent={agent} />
            <div className="thread__body">{thread.pending}</div>
          </li>
        ) : null}
        {draftEntries.map(([id, text]) => (
          <li key={id} className="thread__msg thread__msg--assistant is-streaming" data-role="assistant">
            <MessageHead role="assistant" agent={agent} />
            <div className="thread__body">
              {text}
              <StreamCaret />
            </div>
          </li>
        ))}
        {thread.liveTools.length > 0 ? (
          <li className="thread__msg thread__msg--assistant" data-role="tools">
            <ul className="thread__tools">
              {thread.liveTools.map((tool) => (
                <LiveToolRow key={tool.call.id} tool={tool} />
              ))}
            </ul>
          </li>
        ) : null}
        {waiting ? (
          <li className="thread__msg thread__msg--assistant" data-role="waiting">
            <MessageHead role="assistant" agent={agent} />
            <div className="thread__thinking" role="status">
              <CadenceDots tone="patina" size="sm" label={`${agentName(agent)} is thinking`} />
              <span>{agentName(agent)} is thinking</span>
            </div>
          </li>
        ) : null}
      </ol>
    </div>
  );
}

function MessageHead({ role, agent }: { role: ChatMessage["role"]; agent: AgentId }) {
  return (
    <Eyebrow className="thread__who">{role === "user" ? "You" : role === "system" ? "4GE // note" : `${agentName(agent)}`}</Eyebrow>
  );
}

function ToolRow({ name, args, result }: { name: string; args: unknown; result: { ok: boolean; summary: string } | undefined }) {
  const [open, setOpen] = useState(false);
  return (
    <li className={`thread__tool${result ? (result.ok ? " is-ok" : " is-err") : " is-run"}`}>
      <button type="button" className="thread__tool-head" aria-expanded={open} onClick={() => setOpen((value) => !value)}>
        <ChevronRight aria-hidden="true" size={14} className="thread__chev" />
        <Wrench aria-hidden="true" size={14} />
        <span className="thread__tool-name">{name}</span>
        <span className="thread__tool-state">{result ? (result.ok ? "done" : "failed") : "running"}</span>
      </button>
      {open ? (
        <div className="thread__tool-detail">
          <Eyebrow>Arguments</Eyebrow>
          <pre>{formatArgs(args)}</pre>
          {result ? (
            <>
              <Eyebrow>Result</Eyebrow>
              <pre>{result.summary}</pre>
            </>
          ) : null}
        </div>
      ) : null}
    </li>
  );
}

function LiveToolRow({ tool }: { tool: LiveTool }) {
  return <ToolRow name={tool.call.name} args={tool.call.args} result={tool.result} />;
}

function formatArgs(args: unknown): string {
  if (args === undefined) return "none";
  try {
    return JSON.stringify(args, null, 2);
  } catch {
    return String(args);
  }
}

export interface ComposerProps {
  onSend: (content: string) => Promise<boolean>;
  busy: boolean;
  disabled?: boolean;
  disabledReason?: string;
  placeholder: string;
  label: string;
  /** Text to put in the box for the user to confirm; `n` changes per request. */
  seed?: { text: string; n: number } | undefined;
  compact?: boolean;
  actions?: ReactNode;
}

/** One input, one primary action. Enter sends, Shift+Enter adds a line. */
export function Composer({ onSend, busy, disabled, disabledReason, placeholder, label, seed, compact, actions }: ComposerProps) {
  const [text, setText] = useState("");
  const input = useRef<HTMLTextAreaElement>(null);
  useEffect(() => {
    if (seed) {
      setText(seed.text);
      input.current?.focus();
    }
  }, [seed]);
  const submit = async () => {
    const content = text.trim();
    if (!content || busy || disabled) return;
    setText("");
    const ok = await onSend(content);
    if (!ok) setText(content);
  };
  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault();
      void submit();
    }
  };
  return (
    <form
      className={`composer${compact ? " composer--compact" : ""}`}
      onSubmit={(event) => {
        event.preventDefault();
        void submit();
      }}
    >
      <label className="composer__label">
        <span className="sr-only">{label}</span>
        <textarea
          ref={input}
          className="composer__input"
          rows={compact ? 1 : 3}
          value={text}
          placeholder={disabled && disabledReason ? disabledReason : placeholder}
          disabled={disabled}
          onChange={(event) => setText(event.target.value)}
          onKeyDown={onKeyDown}
        />
      </label>
      <div className="composer__row">
        {actions}
        <Button type="submit" variant="primary" disabled={busy || disabled || text.trim().length === 0}>
          {busy ? "Sending" : "Send"}
        </Button>
      </div>
    </form>
  );
}
