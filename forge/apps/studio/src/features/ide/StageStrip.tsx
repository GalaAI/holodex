import { AGENT_ROSTER, STAGE_LABELS, STAGE_ORDER, type Build, type Checkpoint } from "@4ge/contracts";
import { Button, Eyebrow, TextArea } from "@4ge/kit";
import { useState } from "react";
import { ForgeApiError, api } from "../../lib/api.js";
import { ErrorNotice, StatusLed } from "../../shell/ui.js";
import { layerTone, stageStatus } from "./frames.js";

/**
 * The stage and layer strip under the canvas: the four stages in order,
 * each stage's sprint and layers with live status, and the sprint
 * checkpoint where the user gives input before the next stage starts.
 */
export interface StageStripProps {
  build: Build;
  checkpoints: Record<string, Checkpoint>;
  selectedLayerId: string | undefined;
  onSelectLayer: (layerId: string) => void;
  onCheckpoint: (checkpoint: Checkpoint, build: Build) => void;
}

export function StageStrip({ build, checkpoints, selectedLayerId, onSelectLayer, onCheckpoint }: StageStripProps) {
  // A finished build takes no more input, so its last checkpoint is not offered.
  const finished = build.status === "cancelled" || build.status === "failed" || build.status === "passed";
  const open = build.openCheckpointId && !finished ? checkpoints[build.openCheckpointId] : undefined;
  return (
    <div className="strip" data-testid="stage-strip">
      {open && open.status === "open" ? <CheckpointCard checkpoint={open} onDecided={onCheckpoint} /> : null}
      {build.openCheckpointId && !finished && !open ? (
        <p className="strip__note" role="status">
          A checkpoint is open for this build, but its details have not loaded yet.
        </p>
      ) : null}
      <ol className="strip__stages">
        {STAGE_ORDER.map((stage, index) => {
          const state = build.stages.find((item) => item.stage === stage);
          const sprint = build.sprints.find((item) => item.stage === stage);
          const layers = build.layers.filter((layer) => layer.stage === stage);
          const checkpoint = sprint?.checkpointId ? checkpoints[sprint.checkpointId] : undefined;
          return (
            <li key={stage} className={`strip__stage${sprint && sprint.id === build.currentSprintId ? " is-current" : ""}`}>
              <div className="strip__head">
                <Eyebrow>{`${String(index + 1).padStart(2, "0")} // ${STAGE_LABELS[stage]}`}</Eyebrow>
                <StatusLed status={stageStatus(build, stage)} />
              </div>
              <div className="strip__meta">
                {state ? `${AGENT_ROSTER[state.owner].name} owns it` : "Not planned"}
                {sprint ? ` // sprint ${sprint.index}` : ""}
                {checkpoint ? ` // checkpoint ${checkpoint.status}` : ""}
              </div>
              <ul className="strip__layers">
                {layers.map((layer) => (
                  <li key={layer.id}>
                    <button
                      type="button"
                      className={`strip__layer is-${layerTone(layer)}${layer.id === selectedLayerId ? " is-selected" : ""}`}
                      onClick={() => onSelectLayer(layer.id)}
                      title={layer.error ?? `${layer.title}: ${layer.status}`}
                    >
                      <i aria-hidden="true" />
                      {layer.title}
                    </button>
                  </li>
                ))}
                {layers.length === 0 ? <li className="strip__empty">No layers yet</li> : null}
              </ul>
            </li>
          );
        })}
      </ol>
    </div>
  );
}

/** One sprint checkpoint: summary first, then approve, revise with a note, or skip. */
export function CheckpointCard({ checkpoint, onDecided }: { checkpoint: Checkpoint; onDecided: (checkpoint: Checkpoint, build: Build) => void }) {
  const [revising, setRevising] = useState(false);
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState<string>();
  const [error, setError] = useState<ForgeApiError>();
  const decide = async (decision: "approve" | "revise" | "skip") => {
    setBusy(decision);
    setError(undefined);
    try {
      const result = await api.call("builds.checkpoints.decide", {
        params: { id: checkpoint.buildId, checkpointId: checkpoint.id },
        body: decision === "revise" ? { decision, note: note.trim() } : { decision, ...(note.trim() ? { note: note.trim() } : {}) },
      });
      onDecided(result.checkpoint, result.build);
    } catch (failure) {
      setError(failure as ForgeApiError);
    } finally {
      setBusy(undefined);
    }
  };
  return (
    <section className="checkpoint" aria-label={`Checkpoint: ${checkpoint.title}`} data-testid="checkpoint">
      <div className="checkpoint__text">
        <Eyebrow>{`Checkpoint // ${STAGE_LABELS[checkpoint.stage]} // needs you`}</Eyebrow>
        <h2 className="checkpoint__title">{checkpoint.title}</h2>
        <p className="checkpoint__summary">{checkpoint.summary}</p>
        <p className="checkpoint__question">{checkpoint.question}</p>
        {checkpoint.previews.length ? (
          <p className="checkpoint__muted">
            {checkpoint.previews.length} preview{checkpoint.previews.length === 1 ? "" : "s"} on the canvas for this stage.
          </p>
        ) : null}
      </div>
      <div className="checkpoint__act">
        {revising ? (
          <>
            <TextArea label="What should change?" rows={3} value={note} maxLength={4000} onValueChange={setNote} hint="Smith revises this stage with your note." />
            <div className="checkpoint__buttons">
              <Button onClick={() => setRevising(false)} disabled={busy !== undefined}>
                Back
              </Button>
              <Button variant="primary" onClick={() => void decide("revise")} disabled={busy !== undefined || note.trim().length === 0}>
                {busy === "revise" ? "Sending" : "Send revision"}
              </Button>
            </div>
          </>
        ) : (
          <div className="checkpoint__buttons">
            <Button onClick={() => void decide("skip")} disabled={busy !== undefined}>
              {busy === "skip" ? "Skipping" : "Skip"}
            </Button>
            <Button onClick={() => setRevising(true)} disabled={busy !== undefined}>
              Revise
            </Button>
            <Button variant="primary" onClick={() => void decide("approve")} disabled={busy !== undefined} data-testid="checkpoint-approve">
              {busy === "approve" ? "Approving" : "Approve"}
            </Button>
          </div>
        )}
        {error ? <ErrorNotice error={error} compact /> : null}
      </div>
    </section>
  );
}
