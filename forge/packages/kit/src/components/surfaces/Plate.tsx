// Copied from GalaAI/ark packages/ui/src/surfaces/Plate.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import type { HTMLAttributes, ReactNode } from "react";

/**
 * The one light object per view. Use at most one plate per region; the whole
 * point is that it is the single cream surface against the matte field.
 */
export interface PlateProps extends HTMLAttributes<HTMLDivElement> {
  children?: ReactNode;
}

export function Plate({ children, className, ...rest }: PlateProps) {
  return (
    <div className={className ? `rp-plate ${className}` : "rp-plate"} {...rest}>
      {children}
    </div>
  );
}
