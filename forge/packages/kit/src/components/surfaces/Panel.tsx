// Copied from GalaAI/ark packages/ui/src/surfaces/Panel.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import type { HTMLAttributes, ReactNode } from "react";

export interface PanelProps extends HTMLAttributes<HTMLDivElement> {
  children?: ReactNode;
}

export function Panel({ children, className, ...rest }: PanelProps) {
  return (
    <div className={className ? `rp-panel ${className}` : "rp-panel"} {...rest}>
      {children}
    </div>
  );
}
