import {
  BUILD_KINDS,
  seedReadiness,
  type AtlasEntry,
  type BlueprintMatch,
  type Build,
  type Seed,
} from "@4ge/contracts";
import { Badge, Button, Eyebrow, Led, Panel, TextArea } from "@4ge/kit";
import { Inbox, Rocket } from "lucide-react";
import { useState } from "react";
import { Link } from "react-router";
import { ForgeApiError, api } from "../../lib/api.js";
import { ErrorNotice, statusWord } from "../../shell/ui.js";

/**
 * The Seed Scroll card (SEED_SCROLL.md, adapted): the user's idea kept
 * verbatim, what Prometheus organized around it, the Blueprint Prometheus
 * picked and why, and the one decision left to the user: build now, or
 * later with a reminder in AInbox.
 */
export interface SeedScrollProps {
  seed: Seed;
  atlas: AtlasEntry[] | undefined;
  /** Put a message in the Prometheus composer for the user to confirm. */
  onAskPrometheus: (text: string) => void;
  onSeedChange: (seed: Seed) => void;
  onBuildStarted: (build: Build) => void;
}

const KIND_WORDS: Record<(typeof BUILD_KINDS)[number], string> = {
  website: "Website",
  "web-app": "Web app",
  "mobile-app": "Mobile app",
  game: "Video game",
  erp: "ERP",
  crm: "CRM",
  "desktop-app": "Desktop app",
  other: "Other",
};

const FIELD_WORDS = { idea: "the idea", problem: "the problem", audience: "who it serves", successSignal: "how success shows" } as const;

export function SeedScroll({ seed, atlas, onAskPrometheus, onSeedChange, onBuildStarted }: SeedScrollProps) {
  const readiness = seedReadiness(seed);
  const blueprintName = (id: string | undefined) =>
    id ? (atlas?.find((entry) => entry.blueprint.blueprintId === id)?.blueprint.name ?? id) : undefined;

  return (
    <Panel className="seed" data-testid="seed-scroll">
      <header className="seed__head">
        <Eyebrow>Seed Scroll // {statusWord(seed.status)}</Eyebrow>
        <h2 className="seed__title">{seed.title}</h2>
        <div className="seed__chips">
          <span className="seed__chip">{KIND_WORDS[seed.kind]}</span>
          {seed.platforms.map((platform) => (
            <span key={platform} className="seed__chip">
              {platform}
            </span>
          ))}
        </div>
      </header>

      <blockquote className="seed__idea" aria-label="Your idea, as you said it">
        {seed.idea}
      </blockquote>

      <dl className="seed__facts">
        <SeedFact label="Problem" value={seed.problem} />
        <SeedFact label="Who it serves" value={seed.audience} />
        <SeedFact label="Success looks like" value={seed.successSignal} />
      </dl>

      {seed.features.length ? <SeedList label="Features" items={seed.features} /> : null}
      {seed.constraints.length ? <SeedList label="Constraints" items={seed.constraints} /> : null}

      {!readiness.ready ? (
        <div className="studio-notice studio-notice--warn" role="status">
          <strong>Prometheus still needs {readiness.missing.map((field) => FIELD_WORDS[field]).join(", ")}.</strong>
          <span>You can still decide now; the build works from what the Seed says.</span>
          <span>
            <Button
              onClick={() =>
                onAskPrometheus(
                  `Help me fill in ${readiness.missing.map((field) => FIELD_WORDS[field]).join(" and ")} for the Seed.`,
                )
              }
            >
              Ask Prometheus
            </Button>
          </span>
        </div>
      ) : null}

      <BlueprintPick
        seed={seed}
        blueprintName={blueprintName}
        atlas={atlas}
        onAskPrometheus={onAskPrometheus}
      />

      <SeedDecision seed={seed} onSeedChange={onSeedChange} onBuildStarted={onBuildStarted} />
    </Panel>
  );
}

function SeedFact({ label, value }: { label: string; value: string }) {
  return (
    <div className="seed__fact">
      <dt>{label}</dt>
      <dd className={value.trim() ? undefined : "is-missing"}>{value.trim() || "Not said yet"}</dd>
    </div>
  );
}

function SeedList({ label, items }: { label: string; items: string[] }) {
  return (
    <div className="seed__list">
      <Eyebrow>{label}</Eyebrow>
      <ul>
        {items.map((item) => (
          <li key={item}>{item}</li>
        ))}
      </ul>
    </div>
  );
}

function BlueprintPick({
  seed,
  blueprintName,
  atlas,
  onAskPrometheus,
}: {
  seed: Seed;
  blueprintName: (id: string | undefined) => string | undefined;
  atlas: AtlasEntry[] | undefined;
  onAskPrometheus: (text: string) => void;
}) {
  const [matches, setMatches] = useState<BlueprintMatch[]>();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<ForgeApiError>();
  const chosen = seed.blueprintId;
  const loadMatches = async () => {
    setLoading(true);
    setError(undefined);
    try {
      const result = await api.call("atlas.match", { body: { seedId: seed.id } });
      setMatches(result.matches);
    } catch (failure) {
      setError(failure as ForgeApiError);
    } finally {
      setLoading(false);
    }
  };
  const description = chosen ? atlas?.find((entry) => entry.blueprint.blueprintId === chosen)?.blueprint.description : undefined;

  return (
    <section className="seed__blueprint" aria-label="Blueprint">
      <Eyebrow>Blueprint // picked by Prometheus</Eyebrow>
      {chosen ? (
        <div className="seed__pick" data-testid="blueprint-pick">
          <div className="seed__pick-head">
            <strong>{blueprintName(chosen)}</strong>
            {seed.blueprintMatch ? (
              <Badge state="validated">{Math.round(seed.blueprintMatch.score * 100)}% fit</Badge>
            ) : null}
          </div>
          {seed.blueprintMatch ? <p className="seed__reason">{seed.blueprintMatch.reason}</p> : null}
          {description ? <p className="seed__muted">{description}</p> : null}
        </div>
      ) : (
        <div className="seed__pick is-none">
          <p>No Blueprint in your Atlas fits yet. Prometheus can craft one with you and register it in your Atlas.</p>
          <Button onClick={() => onAskPrometheus("None of my Blueprints fit. Let's craft a new Blueprint for this Seed.")}>
            Craft a Blueprint with Prometheus
          </Button>
        </div>
      )}
      {matches === undefined ? (
        <Button onClick={() => void loadMatches()} disabled={loading}>
          {loading ? "Ranking your Atlas" : "Compare other Blueprints"}
        </Button>
      ) : matches.length === 0 ? (
        <p className="seed__muted">Your Atlas has no other Blueprints to compare.</p>
      ) : (
        <ol className="seed__matches" aria-label="Other Blueprints">
          {matches.map((match) => (
            <li key={match.blueprintId} className={match.blueprintId === chosen ? "is-chosen" : undefined}>
              <div className="seed__match-head">
                <strong>{blueprintName(match.blueprintId)}</strong>
                <span className="seed__score">{Math.round(match.score * 100)}%</span>
              </div>
              <p className="seed__reason">{match.reason}</p>
              {match.blueprintId === chosen ? (
                <Led state="ok">Picked</Led>
              ) : (
                <Button
                  onClick={() =>
                    onAskPrometheus(
                      `Use the ${blueprintName(match.blueprintId)} Blueprint (${match.blueprintId}) for this Seed instead, and rewrite the Seed Scroll.`,
                    )
                  }
                >
                  Ask to switch
                </Button>
              )}
            </li>
          ))}
        </ol>
      )}
      {error ? <ErrorNotice error={error} compact onRetry={() => void loadMatches()} /> : null}
    </section>
  );
}

function SeedDecision({
  seed,
  onSeedChange,
  onBuildStarted,
}: {
  seed: Seed;
  onSeedChange: (seed: Seed) => void;
  onBuildStarted: (build: Build) => void;
}) {
  const [later, setLater] = useState(false);
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState<"now" | "later">();
  const [error, setError] = useState<ForgeApiError>();

  const decide = async (when: "now" | "later") => {
    setBusy(when);
    setError(undefined);
    try {
      const result = await api.call("seeds.decide", {
        params: { id: seed.id },
        body: when === "later" ? { when, ...(note.trim() ? { reminderNote: note.trim() } : {}) } : { when },
      });
      onSeedChange(result.seed);
      if (when === "now") {
        if (result.build) onBuildStarted(result.build);
        else
          setError(
            new ForgeApiError({
              status: 200,
              code: "invalid_response",
              message: "The server accepted the decision but did not start a build.",
              route: "seeds.decide",
            }),
          );
      }
    } catch (failure) {
      setError(failure as ForgeApiError);
    } finally {
      setBusy(undefined);
    }
  };

  if (seed.decision === "later") {
    return (
      <section className="seed__decision is-later" data-testid="seed-later" aria-label="Decision">
        <div className="seed__decided">
          <Inbox aria-hidden="true" size={18} />
          <div>
            <strong>Sent to AInbox</strong>
            <p className="seed__muted">
              This Seed is waiting in your AInbox{seed.ainboxItem ? ` as ${seed.ainboxItem}` : ""} so you can time-block it.
            </p>
            {seed.reminderNote ? <p className="seed__note">Reminder: {seed.reminderNote}</p> : null}
          </div>
        </div>
        <Button variant="primary" onClick={() => void decide("now")} disabled={busy !== undefined}>
          {busy === "now" ? "Starting the build" : "Build it now instead"}
        </Button>
        {error ? <ErrorNotice error={error} compact /> : null}
      </section>
    );
  }

  if (seed.decision === "now" || seed.buildId) {
    return (
      <section className="seed__decision" aria-label="Decision">
        <div className="seed__decided">
          <Rocket aria-hidden="true" size={18} />
          <div>
            <strong>Building now</strong>
            <p className="seed__muted">Smith has this Seed in the IDE.</p>
          </div>
        </div>
        {seed.buildId ? (
          <Link className="studio-link-button" to={`/ide/${seed.buildId}`}>
            Open the build
          </Link>
        ) : null}
      </section>
    );
  }

  return (
    <section className="seed__decision" aria-label="Decision">
      <Eyebrow>Build it now or later?</Eyebrow>
      {later ? (
        <>
          <TextArea
            label="Reminder note for AInbox"
            hint="Optional. Say when or why you want to come back to it."
            rows={2}
            value={note}
            maxLength={600}
            onValueChange={setNote}
          />
          <div className="seed__actions">
            <Button onClick={() => setLater(false)} disabled={busy !== undefined}>
              Back
            </Button>
            <Button variant="primary" onClick={() => void decide("later")} disabled={busy !== undefined}>
              {busy === "later" ? "Sending to AInbox" : "Send to AInbox"}
            </Button>
          </div>
        </>
      ) : (
        <div className="seed__actions">
          <Button onClick={() => setLater(true)} disabled={busy !== undefined}>
            Later
          </Button>
          <Button variant="primary" onClick={() => void decide("now")} disabled={busy !== undefined} data-testid="build-now">
            {busy === "now" ? "Starting the build" : "Build now"}
          </Button>
        </div>
      )}
      {error ? <ErrorNotice error={error} compact /> : null}
    </section>
  );
}
