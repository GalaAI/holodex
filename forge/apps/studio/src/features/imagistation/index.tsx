import { AGENT_ROSTER, type Seed, type SessionEvent } from "@4ge/contracts";
import { Button, Eyebrow, Panel, TextArea } from "@4ge/kit";
import { Plus, ScrollText } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { NavLink, useNavigate, useParams } from "react-router";
import { ForgeApiError, api, useRoute } from "../../lib/api.js";
import { ModelNotice } from "../../shell/models.js";
import { Composer, Thread } from "../../shell/thread/Thread.js";
import { useThread } from "../../shell/thread/useThread.js";
import { ErrorNotice, StatusLed, shortTime } from "../../shell/ui.js";
import { SeedScroll } from "./SeedScroll.js";
import "./imagistation.css";

/**
 * Imagistation (SPEC flow 1 to 4): brainstorm with Prometheus, get a Seed
 * Scroll with a Blueprint picked from the user's Atlas, then decide to build
 * now (the IDE opens) or later (the Seed goes to AInbox with a reminder).
 * Every word on this page comes from the server; nothing is staged.
 */
export default function Imagistation() {
  const { sessionId } = useParams();
  const navigate = useNavigate();
  const sessions = useRoute("imagistation.sessions.list", `sessions:${sessionId ?? ""}`);

  return (
    <section className="imag" data-testid="feature-imagistation">
      <aside className="imag__sessions" aria-label="Imagistation sessions">
        <Eyebrow>4GE // Imagistation</Eyebrow>
        <NavLink to="/imagistation" end className="imag__new">
          <Plus aria-hidden="true" size={16} />
          New idea
        </NavLink>
        {sessions.error ? <ErrorNotice error={sessions.error} compact onRetry={sessions.reload} /> : null}
        <ol className="imag__session-list">
          {sessions.data?.sessions.map((session) => (
            <li key={session.id}>
              <NavLink
                to={`/imagistation/${session.id}`}
                className={({ isActive }) => `imag__session${isActive ? " is-active" : ""}`}
              >
                <span className="imag__session-task" title={session.subject?.label ?? session.task}>
                  {session.subject?.label ?? session.task}
                </span>
                <span className="imag__session-meta">
                  <StatusLed status={session.status} />
                  <span>{shortTime(session.updatedAt)}</span>
                </span>
              </NavLink>
            </li>
          ))}
        </ol>
        {sessions.data && sessions.data.sessions.length === 0 ? (
          <p className="imag__muted">No sessions yet. Your ideas will be listed here.</p>
        ) : null}
      </aside>
      {sessionId ? (
        <Session key={sessionId} sessionId={sessionId} onBuild={(buildId) => navigate(`/ide/${buildId}`)} />
      ) : (
        <IdeaStart
          onStarted={(id) => {
            sessions.reload();
            navigate(`/imagistation/${id}`);
          }}
        />
      )}
    </section>
  );
}

function IdeaStart({ onStarted }: { onStarted: (sessionId: string) => void }) {
  const [idea, setIdea] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ForgeApiError>();
  const empty = idea.trim().length === 0;
  const start = async () => {
    if (empty || busy) return;
    setBusy(true);
    setError(undefined);
    try {
      const result = await api.call("imagistation.sessions.create", { body: { idea: idea.trim() } });
      onStarted(result.session.id);
    } catch (failure) {
      setError(failure as ForgeApiError);
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="imag__start">
      <Panel className="imag__start-panel">
        <Eyebrow>Prometheus // Imagistation</Eyebrow>
        <h1 className="imag__title">Imagistation</h1>
        <p className="imag__lede">Say what you want to build. Prometheus asks what it needs, picks a Blueprint from your Atlas and writes the Seed Scroll. You make the calls.</p>
        <ModelNotice agent="Prometheus" />
        <form
          className="imag__start-form"
          onSubmit={(event) => {
            event.preventDefault();
            void start();
          }}
        >
          <TextArea
            label="What do you want to build?"
            rows={4}
            maxLength={4000}
            placeholder="A website for my tattoo shop, with bookings and a flash gallery"
            value={idea}
            onValueChange={setIdea}
            hint={empty ? "Say what you want to build first." : `${idea.trim().length} of 4000 characters`}
          />
          <Button type="submit" variant="primary" disabled={empty || busy} data-testid="start-idea">
            {busy ? "Opening the session" : "Start with Prometheus"}
          </Button>
        </form>
        {error ? <ErrorNotice error={error} /> : null}
      </Panel>
    </div>
  );
}

function Session({ sessionId, onBuild }: { sessionId: string; onBuild: (buildId: string) => void }) {
  const [seed, setSeed] = useState<Seed>();
  const [seedError, setSeedError] = useState<ForgeApiError>();
  const [writing, setWriting] = useState(false);
  const [suggestion, setSuggestion] = useState<{ text: string; n: number }>();
  const atlas = useRoute("atlas.blueprints.list", "atlas");
  // A Blueprint Prometheus just crafted is new to the Atlas list loaded with the page: read the Atlas again so it shows by name.
  const { reload: reloadAtlas } = atlas;
  const crafted = seed?.blueprintId && atlas.data && !atlas.data.entries.some((entry) => entry.blueprint.blueprintId === seed.blueprintId);
  useEffect(() => {
    if (crafted) reloadAtlas();
  }, [crafted, reloadAtlas]);

  const onEvent = useCallback(
    (event: SessionEvent) => {
      if (event.type === "seed.updated" && event.data.seed.imagistationSessionId === sessionId) setSeed(event.data.seed);
    },
    [sessionId],
  );
  const thread = useThread(sessionId, "imagistation", onEvent);

  // A Seed already written for this session survives a reload.
  useEffect(() => {
    const controller = new AbortController();
    api
      .call("seeds.list", { signal: controller.signal })
      .then((result) => {
        const mine = result.seeds.find((candidate) => candidate.imagistationSessionId === sessionId);
        if (mine) setSeed((current) => current ?? mine);
      })
      .catch(() => {
        /* No seed list yet is not an error on this page; writing one reports its own failure. */
      });
    return () => controller.abort();
  }, [sessionId]);

  const writeSeed = async () => {
    setWriting(true);
    setSeedError(undefined);
    try {
      const result = await api.call("imagistation.seed", { params: { id: sessionId } });
      setSeed(result.seed);
    } catch (failure) {
      setSeedError(failure as ForgeApiError);
    } finally {
      setWriting(false);
    }
  };

  const ask = (text: string) => setSuggestion((current) => ({ text, n: (current?.n ?? 0) + 1 }));

  return (
    <>
      <div className="imag__chat">
        <header className="imag__chat-head">
          <div>
            <Eyebrow>Prometheus // {thread.session ? thread.session.status : "session"}</Eyebrow>
            <h1 className="imag__title imag__title--small">{thread.session?.subject?.label ?? thread.session?.task ?? "Imagistation"}</h1>
          </div>
          <p className="imag__muted">{AGENT_ROSTER.prometheus.role}</p>
        </header>
        <ModelNotice agent="Prometheus" />
        <Thread
          thread={thread}
          agent="prometheus"
          empty={<p className="imag__muted">Prometheus is reading your idea. Its first questions appear here.</p>}
        />
        {thread.sendError ? <ErrorNotice error={thread.sendError} compact /> : null}
        <Composer
          label="Message Prometheus"
          placeholder="Answer Prometheus, or add what matters to you"
          busy={thread.sending}
          onSend={thread.send}
          seed={suggestion}
          disabled={thread.session?.status === "cancelled"}
          disabledReason="This session was cancelled."
        />
      </div>
      <aside className="imag__seed" aria-label="Seed Scroll">
        {seed ? (
          <SeedScroll
            seed={seed}
            atlas={atlas.data?.entries}
            onAskPrometheus={ask}
            onSeedChange={setSeed}
            onBuildStarted={(build) => onBuild(build.id)}
          />
        ) : (
          <Panel className="seed seed--empty">
            <ScrollText aria-hidden="true" size={22} />
            <Eyebrow>Seed Scroll // not written</Eyebrow>
            <p className="imag__muted">When the idea is clear enough, Prometheus writes it into a Seed Scroll. Your own words stay as you said them.</p>
          </Panel>
        )}
        <Button
          variant={seed ? "default" : "primary"}
          onClick={() => void writeSeed()}
          disabled={writing || thread.messages.length === 0}
          data-testid="write-seed"
        >
          {writing ? "Prometheus is writing" : seed ? "Rewrite the Seed from the conversation" : "Write the Seed Scroll"}
        </Button>
        {thread.messages.length === 0 && !thread.loading ? (
          <p className="imag__muted">Talk with Prometheus first; the Seed is written from the conversation.</p>
        ) : null}
        {seedError ? <ErrorNotice error={seedError} compact /> : null}
      </aside>
    </>
  );
}
