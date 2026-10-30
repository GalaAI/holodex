import type { DesignTokens } from "@4ge/contracts";
import { token } from "../tokens/index.js";

/**
 * Design system presets and the token-to-CSS convention for a BUILD's
 * design system (not 4GE's own chrome).
 *
 * The "Phosphor" preset is read from the kit's own token mirror
 * (src/tokens/index.ts, copied from GalaAI/ark packages/tokens), so a build
 * that starts from 4GE's system gets the exact values the studio uses. The
 * other presets are plain starting points a user edits; they are choices,
 * not sample data.
 *
 * Convention a built app reads (starters should use these variables):
 *   colors      --color-<name>
 *   typography  --font-<name>-family, --font-<name>-size, --font-<name>-weight,
 *               --font-<name>-line-height, --font-<name>-tracking
 *   spacing     --space-<name>
 *   radii       --radius-<name>
 *   shadows     --shadow-<name>
 *   motion      --motion-<name>-duration, --motion-<name>-easing
 */

export interface DesignSystemPreset {
  key: string;
  name: string;
  description: string;
  principles: string[];
  tokens: DesignTokens;
}

const px = (value: string) => value;

/** The kit's Phosphor tokens as a build design system. */
export function phosphorTokens(): DesignTokens {
  const c = token.color;
  return {
    colors: [
      { name: "background", value: c.ground, role: "background" },
      { name: "surface", value: c.surface, role: "surface" },
      { name: "surface-2", value: c.surface2, role: "surface" },
      { name: "text", value: c.bone, role: "text" },
      { name: "muted", value: c.dim, role: "muted" },
      { name: "primary", value: c.patina, role: "primary" },
      { name: "accent", value: c.coral, role: "accent" },
      { name: "success", value: c.moss, role: "success" },
      { name: "warning", value: c.ember, role: "warning" },
      { name: "danger", value: c.rust, role: "danger" },
      { name: "border", value: c.line, role: "border" },
    ],
    typography: [
      { name: "display", family: token.font.display, size: "48px", weight: 400, lineHeight: "1.05" },
      { name: "body", family: token.font.body, size: "16px", weight: 400, lineHeight: "1.5" },
      { name: "ui", family: token.font.ui, size: token.text.lead, weight: 500, lineHeight: "1.4" },
      { name: "mono", family: token.font.mono, size: token.text.eyebrow, weight: 400, tracking: token.tracking.eyebrow },
    ],
    spacing: [
      { name: "1", value: px(token.space.u) },
      { name: "2", value: px(token.space.s2) },
      { name: "3", value: px(token.space.s3) },
      { name: "4", value: px(token.space.s4) },
      { name: "6", value: px(token.space.s6) },
      { name: "8", value: px(token.space.s8) },
      { name: "12", value: px(token.space.s12) },
    ],
    radii: [
      { name: "card", value: token.radius.widget },
      { name: "button", value: token.radius.btn },
      { name: "badge", value: token.radius.badge },
      { name: "inset", value: token.radius.inset },
    ],
    shadows: [{ name: "cast", value: token.shadow.cast }],
    motion: [
      { name: "step", durationMs: parseInt(token.motion.durStep, 10), easing: token.motion.easeAnalog },
      { name: "bloom", durationMs: parseInt(token.motion.durBloom, 10), easing: token.motion.easeAnalog },
      { name: "spring", durationMs: parseInt(token.motion.durStep, 10), easing: token.motion.easeSpring },
    ],
  };
}

export const DESIGN_SYSTEM_PRESETS: DesignSystemPreset[] = [
  {
    key: "phosphor",
    name: "Phosphor dark",
    description: "4GE's own system: dark matte surfaces, bone text, one cool accent.",
    principles: [
      "Dark surfaces, one primary action per surface.",
      "Decoration only when it carries data.",
    ],
    tokens: phosphorTokens(),
  },
  {
    key: "clean-light",
    name: "Clean light",
    description: "White page, near-black text, one blue accent. Good for shops and services.",
    principles: ["Plenty of white space.", "One accent color for every action."],
    tokens: {
      colors: [
        { name: "background", value: "#ffffff", role: "background" },
        { name: "surface", value: "#f5f5f4", role: "surface" },
        { name: "text", value: "#111827", role: "text" },
        { name: "muted", value: "#6b7280", role: "muted" },
        { name: "primary", value: "#2563eb", role: "primary" },
        { name: "accent", value: "#f59e0b", role: "accent" },
        { name: "success", value: "#16a34a", role: "success" },
        { name: "warning", value: "#d97706", role: "warning" },
        { name: "danger", value: "#dc2626", role: "danger" },
        { name: "border", value: "#e5e7eb", role: "border" },
      ],
      typography: [
        { name: "display", family: "Inter, ui-sans-serif, sans-serif", size: "48px", weight: 700, lineHeight: "1.1" },
        { name: "body", family: "Inter, ui-sans-serif, sans-serif", size: "16px", weight: 400, lineHeight: "1.6" },
      ],
      spacing: [
        { name: "1", value: "4px" },
        { name: "2", value: "8px" },
        { name: "4", value: "16px" },
        { name: "8", value: "32px" },
        { name: "16", value: "64px" },
      ],
      radii: [
        { name: "card", value: "12px" },
        { name: "button", value: "8px" },
      ],
      shadows: [{ name: "card", value: "0 1px 3px rgba(0,0,0,.08), 0 8px 24px rgba(0,0,0,.06)" }],
      motion: [{ name: "step", durationMs: 200, easing: "ease-out" }],
    },
  },
  {
    key: "ink-studio",
    name: "Ink studio",
    description: "Near-black page, warm paper text, a red accent. Built for tattoo and art studios.",
    principles: ["Let the work be the color.", "Bold display type, quiet body."],
    tokens: {
      colors: [
        { name: "background", value: "#0b0b0c", role: "background" },
        { name: "surface", value: "#17161a", role: "surface" },
        { name: "text", value: "#efe7d6", role: "text" },
        { name: "muted", value: "#9a9286", role: "muted" },
        { name: "primary", value: "#d4402b", role: "primary" },
        { name: "accent", value: "#c9a24b", role: "accent" },
        { name: "success", value: "#8fae8c", role: "success" },
        { name: "warning", value: "#d99a4e", role: "warning" },
        { name: "danger", value: "#c25b38", role: "danger" },
        { name: "border", value: "#2a2830", role: "border" },
      ],
      typography: [
        { name: "display", family: "\"Archivo Black\", ui-sans-serif, sans-serif", size: "56px", weight: 400, lineHeight: "1" },
        { name: "body", family: "\"Space Grotesk\", ui-sans-serif, sans-serif", size: "16px", weight: 400, lineHeight: "1.55" },
      ],
      spacing: [
        { name: "2", value: "8px" },
        { name: "4", value: "16px" },
        { name: "8", value: "32px" },
        { name: "16", value: "64px" },
      ],
      radii: [
        { name: "card", value: "4px" },
        { name: "button", value: "2px" },
      ],
      shadows: [],
      motion: [{ name: "step", durationMs: 260, easing: "cubic-bezier(0.45,0.05,0.35,1)" }],
    },
  },
];

export function emptyTokens(): DesignTokens {
  return { colors: [], typography: [], spacing: [], radii: [], shadows: [], motion: [] };
}

/** Lowercase, dash separated, safe as a CSS custom property name part. */
export function tokenSlug(name: string): string {
  const slug = name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9_-]+/g, "-")
    .replace(/^-+|-+$/g, "");
  return slug.length > 0 ? slug : "token";
}

/**
 * A value that can sit inside one CSS declaration. Refuses anything that
 * could end the declaration or the rule (; { } < and comment markers), so a
 * user value can never inject CSS or markup into the build.
 */
export function isSafeCssValue(value: string): boolean {
  return value.length > 0 && value.length <= 600 && !/[;{}<>]|\/\*|\*\/|\\/.test(value);
}

export function assertSafeCssValue(value: string, what: string): string {
  const trimmed = value.trim();
  if (!isSafeCssValue(trimmed)) {
    throw new DesignInputError(`${what} "${value.slice(0, 80)}" is not a value 4GE can write into CSS`);
  }
  return trimmed;
}

/** Thrown for user input that cannot be applied; the message is plain words. */
export class DesignInputError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "DesignInputError";
  }
}

/** Custom property declarations for a token set, one per line. */
export function tokenDeclarations(tokens: DesignTokens): string[] {
  const lines: string[] = [];
  const decl = (name: string, value: string, what: string) =>
    lines.push(`--${name}: ${assertSafeCssValue(value, what)};`);
  for (const color of tokens.colors) decl(`color-${tokenSlug(color.name)}`, color.value, `Color ${color.name}`);
  for (const type of tokens.typography) {
    const base = `font-${tokenSlug(type.name)}`;
    decl(`${base}-family`, type.family, `Font ${type.name}`);
    decl(`${base}-size`, type.size, `Font size ${type.name}`);
    decl(`${base}-weight`, String(type.weight), `Font weight ${type.name}`);
    if (type.lineHeight) decl(`${base}-line-height`, type.lineHeight, `Line height ${type.name}`);
    if (type.tracking) decl(`${base}-tracking`, type.tracking, `Tracking ${type.name}`);
  }
  for (const space of tokens.spacing) decl(`space-${tokenSlug(space.name)}`, space.value, `Spacing ${space.name}`);
  for (const radius of tokens.radii) decl(`radius-${tokenSlug(radius.name)}`, radius.value, `Radius ${radius.name}`);
  for (const shadow of tokens.shadows) decl(`shadow-${tokenSlug(shadow.name)}`, shadow.value, `Shadow ${shadow.name}`);
  for (const motion of tokens.motion) {
    const base = `motion-${tokenSlug(motion.name)}`;
    decl(`${base}-duration`, `${motion.durationMs}ms`, `Motion ${motion.name}`);
    decl(`${base}-easing`, motion.easing, `Easing ${motion.name}`);
  }
  return lines;
}

/** `:root { ... }` for a token set. Empty token sets give an empty string. */
export function tokensToCss(tokens: DesignTokens): string {
  const lines = tokenDeclarations(tokens);
  if (lines.length === 0) return "";
  return `:root {\n${lines.map((line) => `  ${line}`).join("\n")}\n}`;
}
