// Copied from GalaAI/ark packages/ui/src/actions/TextLink.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import type { AnchorHTMLAttributes, ReactNode } from "react";

export interface TextLinkProps extends Omit<
  AnchorHTMLAttributes<HTMLAnchorElement>,
  "href"
> {
  /**
   * Required: an anchor without href has no link role and is not keyboard
   * focusable. Untyped callers that omit it still render a plain anchor.
   */
  href: string;
  children?: ReactNode;
}

/**
 * The inline navigation link: mono, uppercase, patina, no underline until
 * hover. It is the only way a surface points at another route. It is not a
 * Button: a Button performs an action, a TextLink moves the operator.
 */
export function TextLink({ children, className, ...rest }: TextLinkProps) {
  return (
    <a className={className ? `rp-link ${className}` : "rp-link"} {...rest}>
      {children}
    </a>
  );
}
