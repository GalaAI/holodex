// Copied from GalaAI/ark packages/tokens/src/index.ts at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
/**
 * @ark/tokens: typed mirror of the CSS custom properties.
 *
 * Use this when a value must reach JavaScript (a canvas paint, an inline
 * style, a three.js material). For anything CSS can express, prefer the
 * variable: `var(--color-bone)`. Duplicated values are a drift risk, so this
 * file exists to be read, not to become a second source of truth.
 *
 * Source of record: the Ark Omnitek Design System project in Claude Design.
 * These values mirror src/colors.css, spacing.css and typography.css exactly.
 */

export const token = {
  color: {
    ground: "#050607",
    surface: "#121212",
    surface2: "#161616",
    line: "#262626",
    line2: "#333331",
    bone: "#DAD1B9",
    dim: "#A6A398",
    ghost: "#6B6A62",
    plate: "#CCC4AD",
    plateInk: "#232220",
    plateRust: "#C24B2A",
    ember: "#D99A4E",
    patina: "#79C4B7",
    rust: "#C25B38",
    moss: "#8FAE8C",
    coral: "#D98A63",
    brick: "#B14E33",
    army: "#77785A",
    seal: "#C9A24B",
    cream: "#E4D9C0",
    accentInk: "#201A12",
    patinaBright: "#8FF2D8",
    rustInk: "#E08A6A",
    skeleton: "#1C1C1B",
  },
  bloom: {
    ember:
      "0 0 4px rgba(217,154,78,.5),0 0 14px rgba(217,154,78,.24),0 0 32px rgba(217,154,78,.1)",
    bone: "0 0 4px rgba(218,209,185,.45),0 0 14px rgba(218,209,185,.22),0 0 32px rgba(218,209,185,.09)",
    patina: "0 0 4px rgba(121,196,183,.45),0 0 14px rgba(121,196,183,.2)",
    rust: "0 0 4px rgba(194,91,56,.45),0 0 12px rgba(194,91,56,.2)",
  },
  space: {
    u: "4px",
    s2: "8px",
    s3: "12px",
    s4: "16px",
    s5: "20px",
    s6: "24px",
    s8: "32px",
    s12: "48px",
  },
  radius: {
    widget: "18px",
    btn: "14px",
    tile: "13px",
    badge: "10px",
    inset: "8px",
    menuClosed: "8px",
    menuOpen: "12px",
  },
  stroke: { hair: "1px", focusRing: "2px", focusOffset: "3px" },
  mark: { led: "9px", dot: "6px" },
  shadow: {
    cast: "0 14px 34px rgba(0,0,0,.6),0 3px 10px rgba(0,0,0,.45)",
    edgeVignette:
      "inset 0 1px 0 rgba(255,255,255,.035),inset 0 -18px 30px -22px rgba(0,0,0,.7)",
  },
  motion: {
    easeAnalog: "cubic-bezier(0.45,0.05,0.35,1)",
    durBloom: "480ms",
    durPulse: "3500ms",
    durSweep: "3400ms",
    durSeam: "4800ms",
    easeSpring: "cubic-bezier(0.34,1.36,0.5,1)",
    durCaret: "1100ms",
    durCadenceDots: "1400ms",
    durCadenceArc: "1600ms",
    durLoaderSweep: "2200ms",
    durShuffle: "16800ms",
    durStep: "320ms",
  },
  font: {
    display: '"Archivo Black","Archivo",ui-sans-serif,sans-serif',
    ui: '"Inter",ui-sans-serif,sans-serif',
    mono: '"JetBrains Mono",ui-monospace,monospace',
    body: '"Space Grotesk",ui-sans-serif,sans-serif',
  },
  text: {
    data: "12px",
    badge: "10px",
    btn: "12px",
    eyebrow: "12px",
    micro: "8px",
    caption: "11px",
    lead: "14px",
    value: "22px",
  },
  tracking: { eyebrow: "0.12em" },
  touchMin: "44px",
  shell: { ribbonH: "52px", railW: "236px" },
} as const;

/** The three zone families. A zone sets --rp-accent; primitives read it. */
export const ZONES = ["operate", "create", "distribute"] as const;
export type Zone = (typeof ZONES)[number];

/** Zone accent, matching the [data-zone] rules in colors.css. */
export const zoneAccent: Record<Zone, { accent: string; ink: string }> = {
  operate: { accent: token.color.patina, ink: token.color.ground },
  create: { accent: token.color.coral, ink: token.color.accentInk },
  distribute: { accent: token.color.seal, ink: token.color.accentInk },
};

/** `cssVar("color-bone")` → `"var(--color-bone)"`. Prefer this over literals. */
export function cssVar(name: string, fallback?: string): string {
  return fallback ? `var(--${name}, ${fallback})` : `var(--${name})`;
}

export type Token = typeof token;
