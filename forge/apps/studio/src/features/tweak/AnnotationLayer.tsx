import type { AgentId, Annotation, DesignTarget } from "@4ge/contracts";
import { Badge, Button, Eyebrow, IconButton, TextArea } from "@4ge/kit";
import { useState, type MouseEvent } from "react";
import { api, useRoute } from "../../lib/api.js";
import { describeError, useAction } from "./helpers.js";

/**
 * AnnotationLayer: pinned comments on an artboard that Smith can act on.
 *
 * Mount it as an overlay over one artboard (it fills its positioned parent
 * with position absolute, inset 0). Pins sit at x and y as fractions of the
 * artboard, so they stay put at any zoom. Each pin opens a thread: the
 * person's note, replies (Smith's marked as Smith), Resolve, and Send to
 * Smith, which posts the note into Smith's build session and records
 * Smith's reply in the thread.
 */
export interface AnnotationLayerProps {
  buildId: string;
  /** The page or route this artboard shows, e.g. "/" or "/booking". Pins are per page. */
  page?: string;
  /** While true, a click on the artboard drops a new pin. */
  pinning?: boolean;
  /** Called after a pin is dropped (hosts usually turn pinning off). */
  onPinningChange?: (pinning: boolean) => void;
  /**
   * The element under a click, from the artboard's own DOM when the host can
   * read it (see targetFromIframe). Without it a pin names the artboard.
   */
  resolveTarget?: (x: number, y: number) => DesignTarget | null;
  /** Smith's session for the build (Build.rootSessionId). Without it, Send to Smith is off and says why. */
  smithSessionId?: string;
  /** Show resolved pins too (default false). */
  showResolved?: boolean;
  /** Called when a thread opens or closes. */
  onSelect?: (annotation: Annotation | null) => void;
}

const AUTHOR_LABEL: Record<string, string> = { user: "You", smith: "Smith" };
const authorLabel = (author: "user" | AgentId) => AUTHOR_LABEL[author] ?? author.charAt(0).toUpperCase() + author.slice(1);

/** The message Smith receives for a thread. Exported for tests and the IDE. */
export function smithBrief(annotation: Annotation): string {
  const where = annotation.target.label ?? annotation.target.selector;
  const earlier = annotation.replies.map((reply) => `${authorLabel(reply.author)}: ${reply.body}`).join("\n");
  return [
    `Annotation ${annotation.id} on ${where} (selector ${annotation.target.selector}, page ${annotation.target.page}).`,
    `The person wrote: ${annotation.body}`,
    earlier ? `Thread so far:\n${earlier}` : "",
    "Act on it (a tweak, a layer rebuild or an answer) and say plainly what changed.",
  ]
    .filter(Boolean)
    .join("\n");
}

export function AnnotationLayer({
  buildId,
  page = "/",
  pinning = false,
  onPinningChange,
  resolveTarget,
  smithSessionId,
  showResolved = false,
  onSelect,
}: AnnotationLayerProps) {
  const list = useRoute("design.annotations.list", `annotations:${buildId}`, { query: { buildId } });
  const [openId, setOpenId] = useState<string | null>(null);
  const [draft, setDraft] = useState<{ x: number; y: number; target: DesignTarget } | null>(null);
  const [text, setText] = useState("");
  const [reply, setReply] = useState("");
  const action = useAction();

  const annotations = (list.data?.annotations ?? []).filter(
    (annotation) => annotation.target.page === page && (showResolved || annotation.status === "open" || annotation.id === openId),
  );
  const open = annotations.find((annotation) => annotation.id === openId) ?? null;

  const select = (annotation: Annotation | null) => {
    setOpenId(annotation?.id ?? null);
    setReply("");
    action.setError(null);
    onSelect?.(annotation);
  };

  function drop(event: MouseEvent<HTMLDivElement>) {
    if (!pinning || event.target !== event.currentTarget) return;
    const rect = event.currentTarget.getBoundingClientRect();
    const x = Math.min(1, Math.max(0, (event.clientX - rect.left) / Math.max(1, rect.width)));
    const y = Math.min(1, Math.max(0, (event.clientY - rect.top) / Math.max(1, rect.height)));
    const target = resolveTarget?.(x, y) ?? { selector: "body", page, label: `Artboard ${page}` };
    setDraft({ x, y, target: { ...target, page } });
    setText("");
    select(null);
    onPinningChange?.(false);
  }

  async function create() {
    if (!draft || !text.trim()) return;
    const result = await action.run(() =>
      api.call("design.annotations.create", {
        body: { buildId, target: { ...draft.target, x: draft.x, y: draft.y }, body: text.trim() },
      }),
    );
    if (result) {
      setDraft(null);
      list.reload();
      setOpenId(result.annotation.id);
      onSelect?.(result.annotation);
    }
  }

  async function update(body: { reply?: string; status?: "open" | "resolved"; author?: "user" | AgentId }) {
    if (!open) return;
    const result = await action.run(() => api.call("design.annotations.update", { params: { id: open.id }, body }));
    if (result) {
      setReply("");
      list.reload();
    }
  }

  async function sendToSmith() {
    if (!open || !smithSessionId) return;
    const sent = await action.run(async () => {
      const answer = await api.call("sessions.messages.send", {
        params: { id: smithSessionId },
        body: { content: smithBrief(open) },
      });
      return api.call("design.annotations.update", {
        params: { id: open.id },
        body: { reply: answer.reply.content.trim() || "Smith answered with no text.", author: answer.reply.agent ?? "smith" },
      });
    });
    if (sent) list.reload();
  }

  return (
    <div
      className="annotation-layer"
      data-pinning={pinning ? "true" : undefined}
      data-testid="annotation-layer"
      onClick={drop}
      aria-label={pinning ? "Click the artboard to pin a comment" : undefined}
    >
      {annotations.map((annotation, index) => (
        <button
          key={annotation.id}
          type="button"
          className="annotation-pin"
          data-status={annotation.status}
          data-open={annotation.id === openId ? "true" : undefined}
          style={{ left: `${(annotation.target.x ?? 0.5) * 100}%`, top: `${(annotation.target.y ?? 0.5) * 100}%` }}
          aria-label={`Comment ${index + 1}: ${annotation.body}`}
          onClick={(event) => {
            event.stopPropagation();
            select(annotation.id === openId ? null : annotation);
          }}
        >
          {index + 1}
        </button>
      ))}

      {draft ? (
        <div
          className="annotation-thread"
          style={{ left: `${draft.x * 100}%`, top: `${draft.y * 100}%` }}
          onClick={(event) => event.stopPropagation()}
          role="dialog"
          aria-label="New comment"
        >
          <Eyebrow>PIN // {draft.target.label ?? draft.target.selector}</Eyebrow>
          <TextArea label="What should change?" value={text} onValueChange={setText} />
          {action.error ? <p className="tweak-error">{action.error}</p> : null}
          <div className="annotation-thread__actions">
            <Button variant="default" onClick={() => setDraft(null)}>
              Cancel
            </Button>
            <Button variant="primary" disabled={!text.trim() || action.busy} onClick={create}>
              Pin comment
            </Button>
          </div>
        </div>
      ) : null}

      {open ? (
        <div
          className="annotation-thread"
          style={{ left: `${(open.target.x ?? 0.5) * 100}%`, top: `${(open.target.y ?? 0.5) * 100}%` }}
          onClick={(event) => event.stopPropagation()}
          role="dialog"
          aria-label="Comment thread"
          onKeyDown={(event) => {
            if (event.key === "Escape") select(null);
          }}
        >
          <div className="annotation-thread__head">
            <Eyebrow>THREAD // {open.target.label ?? open.target.selector}</Eyebrow>
            <Badge state={open.status === "resolved" ? "proven" : undefined}>{open.status === "resolved" ? "Done" : "Open"}</Badge>
            <IconButton icon="close" label="Close thread" onClick={() => select(null)} />
          </div>
          <ol className="annotation-thread__messages">
            <li>
              <strong>{authorLabel(open.author)}</strong> {open.body}
            </li>
            {open.replies.map((item, index) => (
              <li key={`${item.at}-${index}`} data-author={item.author}>
                <strong>{authorLabel(item.author)}</strong> {item.body}
              </li>
            ))}
          </ol>
          <TextArea label="Reply" value={reply} onValueChange={setReply} rows={2} />
          {action.error ? (
            <p className="tweak-error" role="alert">
              {action.error}
            </p>
          ) : null}
          {!smithSessionId ? <p className="tweak-note">Smith has no session for this build yet, so this thread cannot be sent to Smith.</p> : null}
          <div className="annotation-thread__actions">
            <Button variant="default" disabled={!reply.trim() || action.busy} onClick={() => update({ reply: reply.trim() })}>
              Reply
            </Button>
            <Button variant="default" disabled={action.busy} onClick={() => update({ status: open.status === "resolved" ? "open" : "resolved" })}>
              {open.status === "resolved" ? "Reopen" : "Resolve"}
            </Button>
            <Button variant="primary" disabled={!smithSessionId || action.busy || open.status === "resolved"} onClick={sendToSmith}>
              {action.busy ? "Sending" : "Send to Smith"}
            </Button>
          </div>
        </div>
      ) : null}

      {list.error ? <p className="annotation-layer__error">{describeError(list.error)}</p> : null}
    </div>
  );
}
