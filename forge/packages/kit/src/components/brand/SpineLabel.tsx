// Copied from GalaAI/ark packages/ui/src/brand/SpineLabel.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
/**
 * SpineLabel (proposed name, not canon): a vertical mono line that runs down
 * the outside of a card's panel edge, carrying kind // id // state. Ghost at
 * rest; takes the edge's status colour while the card is in a status state.
 * Decoration that repeats visible text, so it is aria-hidden. Hidden on narrow
 * viewports. The host card must be position: relative.
 */
export type SpineTone = "rest" | "live" | "gate" | "error" | "ok" | "pick";

export interface SpineLabelProps {
  text: string;
  tone?: SpineTone;
}

export function SpineLabel({ text, tone = "rest" }: SpineLabelProps) {
  return (
    <span className="rp-spine-label" data-tone={tone} aria-hidden="true">
      {text}
    </span>
  );
}
