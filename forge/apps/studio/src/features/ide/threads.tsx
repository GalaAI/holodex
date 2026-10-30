import type { Annotation, ChatMessage } from "@4ge/contracts";
import { Button, Eyebrow, TextArea } from "@4ge/kit";
import { useState } from "react";
import { ForgeApiError, api } from "../../lib/api.js";
import { ErrorNotice, agentName, shortTime } from "../../shell/ui.js";
import { frameIdFromSelector } from "./frames.js";

/**
 * Smith threads, like the Holodex Audit Ledger's comment threads: a thread
 * is an annotation pinned on a frame (design layer), "Send to Smith" posts
 * it into Smith's build conversation tagged with the thread id, and Smith's
 * answer to that message is shown back in the thread. Nothing is authored
 * on Smith's behalf: replies come from Smith's own messages.
 */
export type ThreadStatus = "open" | "in-progress" | "done";

export function threadMarker(annotationId: string): string {
  return `[thread ${annotationId}]`;
}

/** Smith's replies to messages the user sent for this thread. */
export function smithRepliesFor(messages: ChatMessage[], annotationId: string): ChatMessage[] {
  const marker = threadMarker(annotationId);
  const replies: ChatMessage[] = [];
  messages.forEach((message, index) => {
    if (message.role !== "user" || !message.content.startsWith(marker)) return;
    const reply = messages.slice(index + 1).find((candidate) => candidate.role === "assistant" || candidate.role === "user");
    if (reply && reply.role === "assistant") replies.push(reply);
  });
  return replies;
}

export function wasSentToSmith(messages: ChatMessage[], annotationId: string): boolean {
  const marker = threadMarker(annotationId);
  return messages.some((message) => message.role === "user" && message.content.startsWith(marker));
}

export function threadStatus(annotation: Annotation, messages: ChatMessage[]): ThreadStatus {
  if (annotation.status === "resolved") return "done";
  return wasSentToSmith(messages, annotation.id) ? "in-progress" : "open";
}

const STATUS_WORDS: Record<ThreadStatus, string> = { open: "Open", "in-progress": "In progress", done: "Done" };

export function priorityOf(annotation: Annotation): "high" | "medium" | "low" {
  if (annotation.body.startsWith("Broken:")) return "high";
  if (annotation.body.startsWith("Needs work:")) return "medium";
  return "low";
}

export interface ThreadCardProps {
  annotation: Annotation;
  smithMessages: ChatMessage[];
  onSendToSmith: (annotation: Annotation) => Promise<boolean>;
  onChanged: (annotation: Annotation) => void;
  smithReady: boolean;
}

export function ThreadCard({ annotation, smithMessages, onSendToSmith, onChanged, smithReady }: ThreadCardProps) {
  const [reply, setReply] = useState("");
  const [busy, setBusy] = useState<string>();
  const [error, setError] = useState<ForgeApiError>();
  const status = threadStatus(annotation, smithMessages);
  const smith = smithRepliesFor(smithMessages, annotation.id);

  const patch = async (body: { status?: "open" | "resolved"; reply?: string }, label: string) => {
    setBusy(label);
    setError(undefined);
    try {
      const result = await api.call("design.annotations.update", { params: { id: annotation.id }, body });
      onChanged(result.annotation);
      if (body.reply) setReply("");
    } catch (failure) {
      setError(failure as ForgeApiError);
    } finally {
      setBusy(undefined);
    }
  };

  return (
    <article className={`tcard is-${status}`} data-testid={`thread-${annotation.id}`}>
      <header className="tcard__head">
        <Eyebrow>{`${annotation.target.label ?? "Frame"} // ${STATUS_WORDS[status]}`}</Eyebrow>
        <span className="tcard__time">{shortTime(annotation.createdAt)}</span>
      </header>
      <p className="tcard__body">
        <strong>{agentName(annotation.author)}</strong> {annotation.body}
      </p>
      {annotation.replies.map((item) => (
        <p key={`${item.at}-${item.author}`} className={`tcard__reply${item.author === "user" ? "" : " is-agent"}`}>
          <strong>{agentName(item.author)}</strong> {item.body}
        </p>
      ))}
      {smith.map((message) => (
        <p key={message.id} className="tcard__reply is-agent">
          <strong>Smith</strong> {message.content}
        </p>
      ))}
      {status !== "done" ? (
        <>
          <TextArea label="Reply" rows={2} value={reply} maxLength={4000} onValueChange={setReply} />
          <div className="tcard__actions">
            <Button onClick={() => void patch({ status: "resolved" }, "resolve")} disabled={busy !== undefined}>
              {busy === "resolve" ? "Resolving" : "Resolve"}
            </Button>
            {reply.trim() ? (
              <Button onClick={() => void patch({ reply: reply.trim() }, "reply")} disabled={busy !== undefined}>
                {busy === "reply" ? "Saving" : "Reply"}
              </Button>
            ) : null}
            <Button
              variant="primary"
              disabled={busy !== undefined || !smithReady}
              title={smithReady ? undefined : "Smith has no session for this build yet."}
              onClick={async () => {
                setBusy("smith");
                await onSendToSmith(annotation);
                setBusy(undefined);
              }}
            >
              {busy === "smith" ? "Smith is on it" : status === "in-progress" ? "Send to Smith again" : "Send to Smith"}
            </Button>
          </div>
        </>
      ) : (
        <div className="tcard__actions">
          <Button onClick={() => void patch({ status: "open" }, "reopen")} disabled={busy !== undefined}>
            Reopen
          </Button>
        </div>
      )}
      {error ? <ErrorNotice error={error} compact /> : null}
    </article>
  );
}

/** Every thread on the build, ledger style: what you saw, where, priority and status. */
export function Findings({
  annotations,
  smithMessages,
  frameTitle,
  onOpen,
}: {
  annotations: Annotation[];
  smithMessages: ChatMessage[];
  frameTitle: (frameId: string) => string | undefined;
  onOpen: (annotation: Annotation) => void;
}) {
  const rows = annotations.filter((annotation) => frameIdFromSelector(annotation.target.selector));
  if (rows.length === 0) {
    return <p className="tree__empty">No threads yet. Select a frame, write what you see and pin it for Smith.</p>;
  }
  const order = { open: 0, "in-progress": 1, done: 2 } as const;
  const sorted = [...rows].sort(
    (left, right) =>
      order[threadStatus(left, smithMessages)] - order[threadStatus(right, smithMessages)] ||
      right.createdAt.localeCompare(left.createdAt),
  );
  return (
    <table className="findings" data-testid="findings">
      <thead>
        <tr>
          <th scope="col">What you saw</th>
          <th scope="col">Where</th>
          <th scope="col">Priority</th>
          <th scope="col">Status</th>
        </tr>
      </thead>
      <tbody>
        {sorted.map((annotation) => {
          const frameId = frameIdFromSelector(annotation.target.selector) as string;
          const status = threadStatus(annotation, smithMessages);
          return (
            <tr key={annotation.id} className={`is-${status}`}>
              <td>
                <button type="button" className="findings__open" onClick={() => onOpen(annotation)}>
                  {annotation.body}
                </button>
              </td>
              <td>{frameTitle(frameId) ?? annotation.target.label ?? frameId}</td>
              <td>{priorityOf(annotation)}</td>
              <td>{STATUS_WORDS[status]}</td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}
