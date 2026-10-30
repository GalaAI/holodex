// Copied from GalaAI/ark packages/ui/src/actions/ActionPill.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import type { ButtonHTMLAttributes, MouseEvent, ReactNode } from "react";

/**
 * Net-new justification: the console carried twelve pill and chip skins.
 * ActionPill is the one selectable action chip: it names an action, shows
 * its risk tier and whether a human gate waits behind it, and holds a
 * pressed state. It never runs anything itself; the 4GE consumer routes the
 * click through its own server call.
 */
/**
 * Mirrors bamfRiskLevelSchema in @4ge/bamf (.bamf v0.2). Kept as a local
 * literal so @4ge/kit stays free of zod; kit.test.tsx and bamf.test.ts pin
 * both lists to the same four tiers, so drift fails a test.
 */
export const ACTION_RISKS = ["low", "medium", "high", "critical"] as const;
export type ActionRisk = (typeof ACTION_RISKS)[number];

export interface ActionPillProps extends Omit<
  ButtonHTMLAttributes<HTMLButtonElement>,
  "type"
> {
  risk?: ActionRisk;
  /** True when the action waits for a human at the gate. */
  gated?: boolean;
  /** Selected state, rendered as aria-pressed. */
  pressed?: boolean;
  /**
   * A planned or unwired action: rendered dim and not activatable, but it
   * stays in the tab order (aria-disabled only) so keyboard users can find it.
   */
  planned?: boolean;
  children: ReactNode;
}

export function ActionPill({
  risk = "low",
  gated = false,
  pressed = false,
  planned = false,
  className,
  disabled,
  onClick,
  children,
  ...rest
}: ActionPillProps) {
  const handleClick = (event: MouseEvent<HTMLButtonElement>) => {
    if (planned) {
      event.preventDefault();
      return;
    }
    onClick?.(event);
  };
  const classes = ["rp-pill", className ?? ""].filter(Boolean).join(" ");
  return (
    <button
      type="button"
      className={classes}
      data-risk={risk}
      data-planned={planned ? "true" : undefined}
      aria-pressed={pressed}
      aria-disabled={planned || undefined}
      disabled={disabled}
      {...rest}
      onClick={handleClick}
    >
      {children}
      {gated && (
        <em className="rp-pill__gate" title="operator gate required">
          gate
        </em>
      )}
    </button>
  );
}
