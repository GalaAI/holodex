// Copied from GalaAI/ark packages/ui/src/surfaces/AccentTile.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import type { HTMLAttributes, ReactNode } from "react";

export interface AccentTileProps extends HTMLAttributes<HTMLDivElement> {
  label?: ReactNode;
  value?: ReactNode;
  /** The fractal-noise film that makes the fill read as printed. */
  grain?: boolean;
  children?: ReactNode;
}

/**
 * Colored by the zone accent, not by status. If you are reaching for this to
 * signal state, use Led or Badge instead.
 */
export function AccentTile({ label, value, grain = true, children, className, ...rest }: AccentTileProps) {
  return (
    <div className={className ? `rp-tile ${className}` : "rp-tile"} {...rest}>
      {grain && <div aria-hidden="true" className="rp-tile__grain" />}
      {label != null && <div className="rp-tile__label">{label}</div>}
      {value != null && <div className="rp-tile__value">{value}</div>}
      {children}
    </div>
  );
}
