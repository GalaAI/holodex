import type { TweakOp } from "@4ge/contracts";
import { DesignInputError, assertSafeCssValue, tokenSlug } from "./tokens.js";

/**
 * Tweak ops that are CSS, compiled to rules for the build's managed tweak
 * sheet. Each applied tweak owns one marked block, so undo removes exactly
 * that tweak's rules and leaves every other tweak in place:
 *
 *   / * 4ge:begin twk_abc * /
 *   [data-4ge-id="hero"] { color: #fff; }
 *   / * 4ge:end twk_abc * /
 *
 * Pure string functions: no DOM, no file system. The server writes the
 * result; the studio uses the same compiler for its live preview.
 */

export const TWEAK_SHEET_NAME = "4ge-tweaks.css";
export const TOKENS_BLOCK_ID = "tokens";

/** Selectors go into a stylesheet verbatim, so they must not be able to close the rule. */
export function assertSafeSelector(selector: string): string {
  const trimmed = selector.trim();
  if (trimmed.length === 0 || trimmed.length > 600 || /[{};<]|\/\*|\*\//.test(trimmed)) {
    throw new DesignInputError(`The target "${selector.slice(0, 80)}" is not a selector 4GE can style`);
  }
  return trimmed;
}

/** Keyframes 4GE ships for animation tweaks. Other names must exist in the build already. */
export const KEYFRAMES: Record<string, string> = {
  "fade-in": "from { opacity: 0; } to { opacity: 1; }",
  "fade-up": "from { opacity: 0; transform: translateY(16px); } to { opacity: 1; transform: none; }",
  "fade-down": "from { opacity: 0; transform: translateY(-16px); } to { opacity: 1; transform: none; }",
  "slide-in-left": "from { opacity: 0; transform: translateX(-32px); } to { opacity: 1; transform: none; }",
  "slide-in-right": "from { opacity: 0; transform: translateX(32px); } to { opacity: 1; transform: none; }",
  "scale-in": "from { opacity: 0; transform: scale(.94); } to { opacity: 1; transform: none; }",
  pulse: "0%, 100% { opacity: 1; } 50% { opacity: .55; }",
  spin: "to { transform: rotate(360deg); }",
  bounce: "0%, 100% { transform: translateY(0); } 50% { transform: translateY(-8px); }",
  float: "0%, 100% { transform: translateY(0); } 50% { transform: translateY(-6px); }",
};
export const ANIMATION_NAMES = Object.keys(KEYFRAMES);

/** Motion presets: behavior, scaled by intensity 0..1. */
export const MOTION_PRESETS = [
  { key: "fade-up", label: "Fade up on load" },
  { key: "hover-lift", label: "Lift on hover" },
  { key: "hover-glow", label: "Glow on hover" },
  { key: "hover-scale", label: "Grow on hover" },
  { key: "float", label: "Float gently" },
  { key: "parallax", label: "Parallax on scroll" },
] as const;
export type MotionPresetKey = (typeof MOTION_PRESETS)[number]["key"];

function keyframesName(name: string): string {
  return `fge-${tokenSlug(name)}`;
}

function rule(selector: string, declarations: string[]): string {
  return `${selector} {\n${declarations.map((line) => `  ${line}`).join("\n")}\n}`;
}

function cssValue(value: string, what: string): string {
  const safe = assertSafeCssValue(value, what);
  // A bare custom property name means "use that token".
  return /^--[A-Za-z0-9_-]+$/.test(safe) ? `var(${safe})` : safe;
}

function round(value: number, places = 2): string {
  const factor = 10 ** places;
  return String(Math.round(value * factor) / factor);
}

function motionCss(selector: string, preset: string, intensity: number): string {
  const k = Math.max(0, Math.min(1, intensity));
  switch (preset) {
    case "fade-up":
      return [
        `@keyframes fge-motion-fade-up { from { opacity: 0; transform: translateY(${round(8 + 32 * k, 0)}px); } to { opacity: 1; transform: none; } }`,
        rule(selector, [`animation: fge-motion-fade-up ${round(300 + 600 * k, 0)}ms cubic-bezier(.2,.7,.2,1) both;`]),
      ].join("\n");
    case "hover-lift":
      return [
        rule(selector, ["transition: transform 200ms ease, box-shadow 200ms ease;"]),
        rule(`${selector}:hover`, [
          `transform: translateY(-${round(2 + 8 * k, 0)}px);`,
          `box-shadow: 0 ${round(6 + 18 * k, 0)}px ${round(16 + 32 * k, 0)}px rgba(0,0,0,${round(0.15 + 0.25 * k)});`,
        ]),
      ].join("\n");
    case "hover-glow":
      return [
        rule(selector, ["transition: box-shadow 240ms ease;"]),
        rule(`${selector}:hover`, [
          `box-shadow: 0 0 ${round(6 + 24 * k, 0)}px color-mix(in srgb, var(--color-primary, currentColor) ${round(30 + 50 * k, 0)}%, transparent);`,
        ]),
      ].join("\n");
    case "hover-scale":
      return [
        rule(selector, ["transition: transform 180ms ease;"]),
        rule(`${selector}:hover`, [`transform: scale(${round(1 + 0.08 * k, 3)});`]),
      ].join("\n");
    case "float":
      return [
        `@keyframes fge-motion-float { 0%, 100% { transform: translateY(0); } 50% { transform: translateY(-${round(2 + 10 * k, 0)}px); } }`,
        rule(selector, [`animation: fge-motion-float ${round(5000 - 2500 * k, 0)}ms ease-in-out infinite;`]),
      ].join("\n");
    case "parallax":
      // Scroll-driven animation: browsers without it simply show the element still.
      return [
        `@keyframes fge-motion-parallax { from { transform: translateY(${round(20 + 80 * k, 0)}px); } to { transform: translateY(-${round(20 + 80 * k, 0)}px); } }`,
        rule(selector, ["animation: fge-motion-parallax linear both;", "animation-timeline: view();"]),
      ].join("\n");
    default:
      throw new DesignInputError(
        `There is no motion preset "${preset}". Pick one of: ${MOTION_PRESETS.map((item) => item.key).join(", ")}`,
      );
  }
}

/**
 * CSS for one tweak, or null when the tweak is a source edit (text, asset,
 * component). Throws DesignInputError with plain words for bad input.
 */
export function tweakToCss(op: TweakOp): string | null {
  const selector = assertSafeSelector(op.target.selector);
  switch (op.kind) {
    case "color": {
      if (op.property === "token") {
        const name = op.tokenName ?? op.target.label;
        if (!name) throw new DesignInputError("A token color tweak needs the token name to change");
        return rule(":root", [`--color-${tokenSlug(name)}: ${cssValue(op.value, "Color")};`]);
      }
      return rule(selector, [`${op.property}: ${cssValue(op.value, "Color")};`]);
    }
    case "placement": {
      const lines: string[] = [];
      if (op.x !== undefined || op.y !== undefined) {
        lines.push(`translate: ${round(op.x ?? 0)}px ${round(op.y ?? 0)}px;`);
      }
      if (op.align) {
        const align = op.align === "start" ? "start" : op.align === "end" ? "end" : op.align;
        lines.push(`align-self: ${align};`, `justify-self: ${align};`);
        if (op.align === "center") lines.push("margin-inline: auto;");
      }
      if (op.order !== undefined) lines.push(`order: ${op.order};`);
      if (lines.length === 0) throw new DesignInputError("A placement tweak needs a move, an alignment or an order");
      return rule(selector, lines);
    }
    case "layout": {
      const lines: string[] = [];
      if (op.display) lines.push(`display: ${op.display};`);
      if (op.direction) {
        if (!op.display) lines.push("display: flex;");
        lines.push(`flex-direction: ${op.direction};`);
      }
      if (op.columns !== undefined) {
        if (op.display !== "grid") lines.push("display: grid;");
        lines.push(`grid-template-columns: repeat(${op.columns}, minmax(0, 1fr));`);
      }
      if (op.gap) lines.push(`gap: ${cssValue(op.gap, "Gap")};`);
      if (op.padding) lines.push(`padding: ${cssValue(op.padding, "Padding")};`);
      if (op.width) lines.push(`width: ${cssValue(op.width, "Width")};`);
      if (lines.length === 0) throw new DesignInputError("A layout tweak needs at least one change");
      return rule(selector, lines);
    }
    case "animation": {
      const known = KEYFRAMES[op.name];
      const name = known ? keyframesName(op.name) : assertSafeCssValue(op.name, "Animation name");
      if (!/^[A-Za-z_][\w-]*$/.test(name)) throw new DesignInputError(`"${op.name}" is not an animation name`);
      const parts = [
        `animation: ${name} ${op.durationMs}ms ${assertSafeCssValue(op.easing, "Easing")} ${op.delayMs}ms ${op.iterations} both;`,
      ];
      const css = rule(selector, parts);
      return known ? `@keyframes ${name} { ${known} }\n${css}` : css;
    }
    case "motion": {
      const css = motionCss(selector, op.preset, op.intensity);
      return op.respectReducedMotion ? `@media (prefers-reduced-motion: no-preference) {\n${css}\n}` : css;
    }
    case "transition": {
      const property = assertSafeCssValue(op.property, "Transition property");
      return rule(selector, [`transition: ${property} ${op.durationMs}ms ${assertSafeCssValue(op.easing, "Easing")};`]);
    }
    case "asset":
    case "text":
    case "component":
      return null;
  }
}

/** Background image rule, for an asset tweak on an element that is not an img. */
export function backgroundAssetCss(selector: string, assetUrl: string): string {
  const safeSelector = assertSafeSelector(selector);
  const url = assetUrl.trim();
  if (!url || /["'()\\\s<>]/.test(url)) {
    throw new DesignInputError(`The asset "${assetUrl.slice(0, 80)}" is not a path 4GE can use in CSS`);
  }
  return rule(safeSelector, [`background-image: url("${url}");`, "background-size: cover;", "background-position: center;"]);
}

/* ------------------------------------------------------------------ */
/* Managed blocks                                                     */
/* ------------------------------------------------------------------ */

const blockId = (id: string) => {
  if (!/^[A-Za-z0-9_-]{1,120}$/.test(id)) throw new DesignInputError(`"${id}" is not a block id`);
  return id;
};
const beginMarker = (id: string) => `/* 4ge:begin ${id} */`;
const endMarker = (id: string) => `/* 4ge:end ${id} */`;

export const TWEAK_SHEET_HEADER =
  "/* Managed by 4GE: design tokens and tweaks. Each block belongs to one tweak; undo in 4GE removes it. Edit freely outside the markers. */";

export interface SheetBlock {
  id: string;
  css: string;
}

export function listBlocks(sheet: string): SheetBlock[] {
  const blocks: SheetBlock[] = [];
  const pattern = /\/\* 4ge:begin ([A-Za-z0-9_-]+) \*\/\n?([\s\S]*?)\n?\/\* 4ge:end \1 \*\//g;
  for (const match of sheet.matchAll(pattern)) {
    blocks.push({ id: match[1] as string, css: (match[2] ?? "").trim() });
  }
  return blocks;
}

export function removeBlock(sheet: string, id: string): string {
  const begin = sheet.indexOf(beginMarker(blockId(id)));
  if (begin === -1) return sheet;
  const endTag = endMarker(id);
  const end = sheet.indexOf(endTag, begin);
  if (end === -1) return sheet;
  let stop = end + endTag.length;
  if (sheet[stop] === "\n") stop += 1;
  return sheet.slice(0, begin) + sheet.slice(stop);
}

/**
 * Put a block in the sheet, replacing one with the same id. `first` puts it
 * right after the header (the tokens block goes first so tweaks win).
 */
export function upsertBlock(sheet: string, id: string, css: string, options: { first?: boolean } = {}): string {
  let base = removeBlock(sheet, blockId(id));
  if (!base.startsWith(TWEAK_SHEET_HEADER)) base = `${TWEAK_SHEET_HEADER}\n${base.trimStart()}`;
  const block = `${beginMarker(id)}\n${css.trim()}\n${endMarker(id)}\n`;
  if (options.first) {
    const afterHeader = TWEAK_SHEET_HEADER.length + 1;
    return base.slice(0, afterHeader) + block + base.slice(afterHeader);
  }
  return `${base.replace(/\n*$/, "\n")}${block}`;
}

/** Compile a whole sheet from a token block and ordered tweak blocks (studio live preview). */
export function composeSheet(tokensCss: string, tweaks: SheetBlock[]): string {
  let sheet = TWEAK_SHEET_HEADER + "\n";
  if (tokensCss.trim()) sheet = upsertBlock(sheet, TOKENS_BLOCK_ID, tokensCss, { first: true });
  for (const tweak of tweaks) sheet = upsertBlock(sheet, tweak.id, tweak.css);
  return sheet;
}
