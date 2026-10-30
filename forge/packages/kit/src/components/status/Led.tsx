// Copied from GalaAI/ark packages/ui/src/status/Led.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import type { HTMLAttributes, ReactNode } from "react";

/**
 * The four lamp states. This union is deliberately closed: passing a state
 * with no styling would render an invisible dot with no error, which is how
 * the S4 prototype currently ships two silent bugs (see CONFLICTS.md #2).
 */
export type LedState = "live" | "warn" | "err" | "ok";

export interface LedProps extends Omit<HTMLAttributes<HTMLSpanElement>, "children"> {
  /** live = pulsing patina · warn = ember · err = rust · ok = moss, unlit. */
  state?: LedState;
  children?: ReactNode;
}

export function Led({ state = "ok", children, ...rest }: LedProps) {
  return (
    <span className={`rp-led rp-led--${state}`} {...rest}>
      <i aria-hidden="true" />
      {children}
    </span>
  );
}
