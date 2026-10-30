import { z } from "zod";
import type { AgentId } from "./agents.js";

/**
 * The four 4GE stages, in order. Copied from GalaAI/ark
 * packages/shared-types/src/stage-gate.ts (fourGeStageSchema and
 * FOUR_GE_STAGE_ORDER): order is the point, so it is declared once here and
 * @4ge/gate, the orchestra and the studio derive from it.
 */
export const STAGES = [
  "architecture",
  "orchestration",
  "interface",
  "deploy",
] as const;
export const stageSchema = z.enum(STAGES);
export type Stage = z.infer<typeof stageSchema>;

export const STAGE_ORDER: readonly Stage[] = STAGES;

export function stageOrderOf(stage: Stage): number {
  return STAGE_ORDER.indexOf(stage) + 1;
}

/** Owner of each stage; supporting agents work inside the owner's sprint. */
export const STAGE_OWNERS: Record<Stage, { owner: AgentId; supports: AgentId[] }> = {
  architecture: { owner: "hephaestus", supports: ["metis"] },
  orchestration: { owner: "athena", supports: ["metis"] },
  interface: { owner: "smith", supports: ["metis"] },
  deploy: { owner: "icarus", supports: [] },
};

export const STAGE_LABELS: Record<Stage, string> = {
  architecture: "Architecture",
  orchestration: "Orchestration",
  interface: "Interface",
  deploy: "Deploy",
};

/**
 * Kinds of layer a stage plan can hold. A layer is one unit of parallel work
 * owned by one agent, previewed on its own and test-flown on its own.
 */
export const LAYER_KINDS = [
  "research",
  "data",
  "backend",
  "orchestration",
  "integrations",
  "design-system",
  "interface",
  "content",
  "deploy",
  "test-flight",
] as const;
export const layerKindSchema = z.enum(LAYER_KINDS);
export type LayerKind = z.infer<typeof layerKindSchema>;

/** What a new build can be (SPEC: any kind of build works). */
export const BUILD_KINDS = [
  "website",
  "web-app",
  "mobile-app",
  "game",
  "erp",
  "crm",
  "desktop-app",
  "other",
] as const;
export const buildKindSchema = z.enum(BUILD_KINDS);
export type BuildKind = z.infer<typeof buildKindSchema>;

export const PLATFORMS = [
  "web",
  "ios",
  "android",
  "windows",
  "linux",
  "macos",
] as const;
export const platformSchema = z.enum(PLATFORMS);
export type Platform = z.infer<typeof platformSchema>;
