// Copied from GalaAI/ark packages/ui/src/surfaces/FocalCard.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import type { HTMLAttributes, ReactNode } from "react";
import { Panel } from "./Panel";

/**
 * Net-new justification: the loop needs exactly one focal object. Telemetry
 * lives in the periphery (Law 06); a FocalCard is the single interruption,
 * shown only when a human must decide, with the plain sentence, the
 * technical facts beside it (never instead of it) and the actions that
 * resolve it. The edge colour says why it interrupted.
 */
export type FocalReason = "gate" | "stop" | "live";

export interface FocalCardProps extends Omit<
  HTMLAttributes<HTMLDivElement>,
  "title"
> {
  /** Uppercase mono eyebrow, e.g. "Gate pending // Watcher". */
  eyebrow: ReactNode;
  title: ReactNode;
  /** The Reverse DLC sentence: what happened, in plain words. */
  plain: ReactNode;
  /** The technical facts the sentence came from. Always shown. */
  technical?: ReactNode;
  /** Outcome after an action: a receipt line or a refusal. */
  outcome?: ReactNode;
  /** The buttons that resolve the card. */
  actions?: ReactNode;
  reason?: FocalReason;
}

export function FocalCard({
  eyebrow,
  title,
  plain,
  technical,
  outcome,
  actions,
  reason = "gate",
  className,
  ...rest
}: FocalCardProps) {
  const classes = ["rp-focal", `rp-focal--${reason}`, className ?? ""]
    .filter(Boolean)
    .join(" ");
  return (
    <Panel className={classes} {...rest}>
      <span className="rp-focal__eyebrow">{eyebrow}</span>
      <div className="rp-focal__title">{title}</div>
      <p className="rp-focal__plain">{plain}</p>
      {technical != null && <div className="rp-focal__tech">{technical}</div>}
      {/* Only the outcome line is live; the card and its actions are not. */}
      <div className="rp-focal__outcome" role="status" aria-live="polite">
        {outcome}
      </div>
      {actions != null && <div className="rp-focal__actions">{actions}</div>}
    </Panel>
  );
}
