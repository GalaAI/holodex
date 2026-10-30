import type { LibraryOpen } from "@4ge/contracts";
import { Button, Eyebrow, Panel } from "@4ge/kit";
import { useCallback, useState } from "react";
import { ForgeApiError, api } from "../../lib/api.js";
import { Composer, Thread } from "../../shell/thread/Thread.js";
import { useThread } from "../../shell/thread/useThread.js";
import { ErrorNotice } from "../../shell/ui.js";

/**
 * Opening a Smith session about a Library item or a mod: POST /sessions with
 * the item's `open` target, then the same thread component the IDE uses.
 * Shared by the Library and the Mods view.
 */
export function useSmithSession() {
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [subject, setSubject] = useState<string>();
  const [error, setError] = useState<ForgeApiError>();
  const [opening, setOpening] = useState(false);

  const open = useCallback(async (target: LibraryOpen) => {
    setOpening(true);
    setError(undefined);
    setSubject(target.subject.label ?? target.subject.id);
    try {
      const { session } = await api.call("sessions.create", { body: target });
      setSessionId(session.id);
    } catch (failure) {
      setSessionId(null);
      setError(
        failure instanceof ForgeApiError
          ? failure
          : new ForgeApiError({ status: 0, code: "network", message: String(failure), route: "sessions.create" }),
      );
    } finally {
      setOpening(false);
    }
  }, []);

  const close = useCallback(() => {
    setSessionId(null);
    setError(undefined);
    setSubject(undefined);
  }, []);

  return { sessionId, subject, error, opening, open, close };
}

export type SmithSession = ReturnType<typeof useSmithSession>;

export function SmithPanel({ smith }: { smith: SmithSession }) {
  if (!smith.sessionId && !smith.error && !smith.opening) return null;
  return (
    <Panel className="lib-smith" aria-label="Smith session">
      <div className="lib-smith__head">
        <Eyebrow>{`SMITH // ${smith.subject ?? "SESSION"}`}</Eyebrow>
        <Button onClick={smith.close}>Close</Button>
      </div>
      {smith.opening ? <p className="lib-muted">Opening a session with Smith.</p> : null}
      {smith.error ? <ErrorNotice error={smith.error} compact /> : null}
      {smith.sessionId ? <SmithThread key={smith.sessionId} sessionId={smith.sessionId} /> : null}
    </Panel>
  );
}

function SmithThread({ sessionId }: { sessionId: string }) {
  const thread = useThread(sessionId, "session");
  return (
    <div className="lib-smith__thread">
      {thread.loadError ? <ErrorNotice error={thread.loadError} compact onRetry={thread.reload} /> : null}
      <Thread thread={thread} agent="smith" empty={<p className="lib-muted">Smith is reading this. Ask anything about it.</p>} />
      {thread.sendError ? <ErrorNotice error={thread.sendError} compact /> : null}
      <Composer
        onSend={thread.send}
        busy={thread.sending}
        placeholder="Ask Smith about this"
        label="Message Smith"
        compact
      />
    </div>
  );
}
