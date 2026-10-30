// Copied from GalaAI/ark packages/ui/src/status/Badge.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import type { HTMLAttributes, ReactNode } from "react";

/**
 * Lifecycle states. Closed union, same reasoning as LedState: the S4 shell
 * currently passes gated/running/failed/settled, none of which exist here.
 * Typing this strictly turns that silent visual no-op into a build error.
 * See CONFLICTS.md #2: do not widen this union to make the shell compile.
 */
export type BadgeState = "validated" | "proven" | "blocked";

export interface BadgeProps extends Omit<HTMLAttributes<HTMLSpanElement>, "children"> {
  /** Omit for the ghost outline. proven is the only filled badge. */
  state?: BadgeState;
  children?: ReactNode;
}

export function Badge({ state, children, ...rest }: BadgeProps) {
  return (
    <span className={`rp-badge${state ? ` rp-badge--${state}` : ""}`} {...rest}>
      {children}
    </span>
  );
}
