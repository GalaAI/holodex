/**
 * @4ge/kit/design: orchestra-free design helpers for a build's design layer.
 *
 * Pure TypeScript, no DOM, no file system and no React, so the server (which
 * writes the build's files) and the studio (live preview, pickers) share one
 * implementation:
 *
 * - tokens:  design system presets (Phosphor read from the kit's tokens) and
 *            the token-to-CSS custom property convention.
 * - css:     tweak ops compiled to CSS, and the managed tweak sheet whose
 *            marked blocks make every tweak individually undoable.
 * - source:  text, asset and component edits on real source files, found by
 *            data-4ge-id, each returning an undo patch.
 * - catalog: inventory candidates from @4ge/kit and the 21st registry, each
 *            recording its source.
 */
export * from "./tokens.js";
export * from "./css.js";
export * from "./source.js";
export * from "./catalog.js";
