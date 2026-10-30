// Copied from GalaAI/ark packages/ui/src/data/ActivityTrail.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import type { HTMLAttributes, ReactNode } from "react";

/**
 * Net-new justification: the donor preview makes recent read and selection
 * events visible, but ArkStation had no reusable compact evidence trail.
 * ActivityTrail renders supplied receipts only and performs no logging itself.
 */
export interface ActivityTrailItem {
  id: string;
  label: ReactNode;
}

export interface ActivityTrailProps extends HTMLAttributes<HTMLOListElement> {
  items: readonly ActivityTrailItem[];
  live?: "off" | "polite" | "assertive";
}

export function ActivityTrail({
  items,
  live = "polite",
  className,
  ...rest
}: ActivityTrailProps) {
  const classes = ["rp-activity-trail", className ?? ""]
    .filter(Boolean)
    .join(" ");

  return (
    <ol className={classes} aria-live={live} {...rest}>
      {items.map((item) => (
        <li key={item.id}>{item.label}</li>
      ))}
    </ol>
  );
}
