/**
 * 4GE system layers a .bamf command can target. Stands in for Ark's
 * ARK_LAYERS (packages/shared-types): the server layer slots from
 * @4ge/contracts SERVER_LAYERS (without "core"), plus the studio, the server
 * itself, the stage gate and a build's own app. bamf.test.ts fails if this
 * list and SERVER_LAYERS drift.
 */
export const BAMF_TARGET_LAYERS = [
  "atlas",
  "orchestra",
  "design",
  "mods",
  "sandbox",
  "library",
  "studio",
  "server",
  "gate",
  "build",
] as const;
export type BamfLayer = (typeof BAMF_TARGET_LAYERS)[number];
