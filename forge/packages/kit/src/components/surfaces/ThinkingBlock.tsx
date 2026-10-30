// Copied from GalaAI/ark packages/ui/src/surfaces/ThinkingBlock.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import { useEffect, useId, useState } from "react";
import { CadenceMark, StreamCaret } from "../status/Cadence";

/**
 * Agent thinking disclosure, Phosphor chassis layer.
 * thinking: no reasoning text yet. reasoning: summary lines are streaming (opens on its own).
 * done: answer has started, block folds to "Thought for 12s". stopped: interrupted or cancelled.
 */
export type ThinkingState = "thinking" | "reasoning" | "done" | "stopped";

export interface ThinkingBlockProps {
  state: ThinkingState;
  /** Seconds since thinking started. The caller ticks it; the block only formats it. */
  seconds: number;
  /** Reasoning summary, one paragraph per entry. The last one streams with the caret. */
  summary?: string[];
  /** What the agent is doing right now ("reading 14 files"). Shown on the header while live. */
  hint?: string;
  /** Controlled open state. Leave unset for the default: open while reasoning, folded when done. */
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
  className?: string;
}

const clock = (s: number) =>
  `${String(Math.floor(s / 60)).padStart(2, "0")}:${String(Math.floor(s % 60)).padStart(2, "0")}`;
const span = (s: number) =>
  s < 60
    ? `${Math.max(1, Math.round(s))}s`
    : `${Math.floor(s / 60)}m ${String(Math.round(s % 60)).padStart(2, "0")}s`;

export function ThinkingBlock({
  state,
  seconds,
  summary = [],
  hint,
  open,
  onOpenChange,
  className,
}: ThinkingBlockProps) {
  const uid = useId();
  const live = state === "thinking" || state === "reasoning";
  // Uncontrolled default follows the state; a manual toggle wins until the state changes again.
  const [auto, setAuto] = useState(state === "reasoning");
  useEffect(() => {
    setAuto(state === "reasoning");
  }, [state]);
  const isOpen = (open ?? auto) && summary.length > 0;
  const toggle = () => {
    const next = !isOpen;
    if (open === undefined) setAuto(next);
    onOpenChange?.(next);
  };

  const label = live
    ? "Thinking"
    : state === "done"
      ? `Thought for ${span(seconds)}`
      : `Stopped after ${span(seconds)}`;

  return (
    <div
      className={["rp-thinking", className].filter(Boolean).join(" ")}
      data-state={state}
    >
      <span className="rp-thinking__halo" aria-hidden="true" />
      <div className="rp-thinking__panel" data-open={isOpen ? "true" : "false"}>
        <button
          type="button"
          className="rp-thinking__head"
          aria-expanded={summary.length ? isOpen : undefined}
          aria-controls={summary.length ? uid : undefined}
          onClick={summary.length ? toggle : undefined}
          disabled={!summary.length && !live ? true : undefined}
        >
          <CadenceMark
            state={live ? "live" : state === "done" ? "done" : "stopped"}
          />
          <span className="rp-thinking__label" aria-live="polite">
            {label}
          </span>
          <span className="rp-thinking__hint">{live && hint ? hint : ""}</span>
          {live ? (
            <span className="rp-thinking__time" role="timer">
              {clock(seconds)}
            </span>
          ) : null}
          {summary.length ? (
            <svg
              className="rp-thinking__chev"
              viewBox="0 0 16 16"
              aria-hidden="true"
            >
              <path d="M6 3.5 10.5 8 6 12.5" />
            </svg>
          ) : null}
        </button>
        {summary.length ? (
          <div className="rp-thinking__more" id={uid}>
            <div className="rp-thinking__more-in">
              <div className="rp-thinking__body" aria-hidden={!isOpen}>
                {summary.map((p, i) => (
                  <p key={i}>
                    {p}
                    {state === "reasoning" && i === summary.length - 1 ? (
                      <StreamCaret />
                    ) : null}
                  </p>
                ))}
              </div>
            </div>
          </div>
        ) : null}
      </div>
    </div>
  );
}
