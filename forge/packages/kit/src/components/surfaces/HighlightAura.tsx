// Copied from GalaAI/ark packages/ui/src/surfaces/HighlightAura.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import type { HTMLAttributes } from "react";

/**
 * Class for an element that IS the aura layer. Use it when the host owns the
 * layer's position or motion, for example FluidSelect's gliding highlight.
 */
export const auraClassName = "rp-aura";

/**
 * Class for a host that keeps its content and wears the aura behind it on
 * ::before. Light it with `data-aura="lit"` or a component state selector.
 * The host must be `position: relative` and keep its text above the layer.
 */
export const auraBehindClassName = "rp-aura--behind";

/**
 * Class for the row that holds a `<HighlightAura />`: positioned and isolated
 * so the aura sits behind the row's content and never covers a label.
 */
export const auraHostClassName = "rp-aura-host";

export type HighlightAuraProps = Omit<
  HTMLAttributes<HTMLSpanElement>,
  "children"
>;

/**
 * The global Highlight Aura: a leading side light (coral, or `--sidelight`),
 * a bloom into the row and a fade toward the right edge. Decoration only; it
 * never masks labels or takes pointer events. Place it inside a row that
 * carries `auraHostClassName`; it fills the row behind the content.
 *
 * `<div className={auraHostClassName}><HighlightAura /><span>Label</span></div>`
 */
export function HighlightAura({ className, ...rest }: HighlightAuraProps) {
  return (
    <span
      aria-hidden="true"
      className={`${auraClassName} rp-aura--fill${className ? ` ${className}` : ""}`}
      {...rest}
    />
  );
}
