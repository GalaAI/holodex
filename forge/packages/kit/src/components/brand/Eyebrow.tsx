// Copied from GalaAI/ark packages/ui/src/brand/Eyebrow.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import type { HTMLAttributes, ReactNode } from "react";

export interface EyebrowProps extends Omit<HTMLAttributes<HTMLSpanElement>, "children"> {
  children?: ReactNode;
}

/** `<Eyebrow>BUILD LOG // CROWDFUNDING PRE-LAUNCH</Eyebrow>` */
export function Eyebrow({ children, className, ...rest }: EyebrowProps) {
  return (
    <span className={className ? `rp-eyebrow ${className}` : "rp-eyebrow"} {...rest}>
      {children}
    </span>
  );
}
