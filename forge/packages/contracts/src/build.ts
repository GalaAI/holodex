import { z } from "zod";
import { agentIdSchema } from "./agents.js";
import { blueprintIdSchema } from "./atlas.js";
import {
  buildIdSchema,
  checkpointIdSchema,
  isoDateTimeSchema,
  layerIdSchema,
  receiptIdSchema,
  relativePathSchema,
  seedIdSchema,
  sessionIdSchema,
  sprintIdSchema,
} from "./common.js";
import { layerKindSchema, stageSchema } from "./stages.js";

/**
 * Builds, layers, sprints, checkpoints, previews and receipts.
 *
 * A build runs its Blueprint's stage plan: one sprint per stage, the layers
 * of a sprint in parallel nested sessions under Smith, a checkpoint at the
 * end of each sprint where the user gives input, and a receipt per layer and
 * per stage that @4ge/gate checks before the next stage may start.
 */

export const workStatusSchema = z.enum([
  "pending",
  "running",
  "blocked",
  "passed",
  "failed",
  "cancelled",
]);
export type WorkStatus = z.infer<typeof workStatusSchema>;

/** Something the user can look at for a stage or a layer. */
export const previewSchema = z.object({
  kind: z.enum(["url", "html", "image", "text", "diff"]),
  title: z.string().min(1).max(160),
  /** A URL for kind url, otherwise a build-relative path. */
  ref: z.string().min(1).max(1000),
  stage: stageSchema,
  layerId: layerIdSchema.optional(),
  updatedAt: isoDateTimeSchema,
});
export type Preview = z.infer<typeof previewSchema>;

export const layerSchema = z.object({
  id: layerIdSchema,
  buildId: buildIdSchema,
  /** The Blueprint layer plan key this layer came from. */
  key: z.string().min(2).max(80),
  kind: layerKindSchema,
  stage: stageSchema,
  agent: agentIdSchema,
  title: z.string().min(1).max(160),
  status: workStatusSchema,
  sessionId: sessionIdSchema.optional(),
  dependsOn: z.array(z.string().min(2).max(80)).default([]),
  previews: z.array(previewSchema).default([]),
  /** Test flight result for this layer, set by Icarus. */
  testFlight: z.enum(["not-run", "running", "passed", "failed"]).default("not-run"),
  receiptId: receiptIdSchema.optional(),
  startedAt: isoDateTimeSchema.optional(),
  finishedAt: isoDateTimeSchema.optional(),
  /** Plain words when status is failed or blocked. */
  error: z.string().max(2000).optional(),
});
export type Layer = z.infer<typeof layerSchema>;

export const sprintSchema = z.object({
  id: sprintIdSchema,
  buildId: buildIdSchema,
  /** 1-based, in stage order. */
  index: z.number().int().positive(),
  stage: stageSchema,
  layerIds: z.array(layerIdSchema),
  status: workStatusSchema,
  checkpointId: checkpointIdSchema.optional(),
  startedAt: isoDateTimeSchema.optional(),
  finishedAt: isoDateTimeSchema.optional(),
});
export type Sprint = z.infer<typeof sprintSchema>;

export const checkpointStatusSchema = z.enum(["open", "approved", "revise", "skipped"]);
export type CheckpointStatus = z.infer<typeof checkpointStatusSchema>;

export const checkpointDecisionSchema = z.object({
  decision: z.enum(["approve", "revise", "skip"]),
  /** What the user wants changed, in their words. Required for revise. */
  note: z.string().max(4000).optional(),
});
export type CheckpointDecision = z.infer<typeof checkpointDecisionSchema>;

export const checkpointSchema = z.object({
  id: checkpointIdSchema,
  buildId: buildIdSchema,
  sprintId: sprintIdSchema,
  stage: stageSchema,
  title: z.string().min(1).max(160),
  /** What the sprint produced, summary first. */
  summary: z.string().min(1).max(4000),
  question: z.string().min(1).max(600),
  previews: z.array(previewSchema).default([]),
  status: checkpointStatusSchema,
  decision: checkpointDecisionSchema
    .extend({ decidedAt: isoDateTimeSchema })
    .optional(),
  createdAt: isoDateTimeSchema,
});
export type Checkpoint = z.infer<typeof checkpointSchema>;

/**
 * A stage receipt. Adapted from GalaAI/ark stageReceiptSchema
 * (packages/shared-types/src/stage-gate.ts): a receipt names real artifacts
 * and real checks so it can be verified rather than believed. In 4GE the
 * artifacts are paths relative to the build's app folder, the commit is
 * optional (a build's app folder may not be a git repo yet), and a receipt
 * may cover one layer or the whole stage.
 */
export const receiptCheckSchema = z.object({
  name: z.string().min(1).max(160),
  passed: z.boolean(),
  detail: z.string().max(1000).optional(),
});
export type ReceiptCheck = z.infer<typeof receiptCheckSchema>;

export const receiptSchema = z.object({
  id: receiptIdSchema,
  buildId: buildIdSchema,
  stage: stageSchema,
  /** Absent for a whole-stage receipt. */
  layerId: layerIdSchema.optional(),
  agentId: agentIdSchema,
  commit: z
    .string()
    .regex(/^[0-9a-f]{40}$/, "commit must be a full 40 character sha")
    .optional(),
  artifacts: z.array(relativePathSchema).min(1).max(200),
  checks: z.array(receiptCheckSchema).min(1).max(80),
  recordedAt: isoDateTimeSchema,
});
export type Receipt = z.infer<typeof receiptSchema>;

export const stageStateSchema = z.object({
  stage: stageSchema,
  owner: agentIdSchema,
  status: workStatusSchema,
  sprintId: sprintIdSchema.optional(),
  receiptId: receiptIdSchema.optional(),
});
export type StageState = z.infer<typeof stageStateSchema>;

export const buildStatusSchema = z.enum([
  "queued",
  "running",
  "awaiting-checkpoint",
  "test-flight",
  "passed",
  "failed",
  "cancelled",
]);
export type BuildStatus = z.infer<typeof buildStatusSchema>;

export const buildSchema = z.object({
  id: buildIdSchema,
  seedId: seedIdSchema,
  blueprintId: blueprintIdSchema,
  title: z.string().min(1).max(160),
  status: buildStatusSchema,
  stages: z.array(stageStateSchema),
  layers: z.array(layerSchema),
  sprints: z.array(sprintSchema),
  currentSprintId: sprintIdSchema.optional(),
  openCheckpointId: checkpointIdSchema.optional(),
  /** Smith's session for this build; layer sessions nest under it. */
  rootSessionId: sessionIdSchema.optional(),
  /** Live preview of the running app, when one is up. */
  previewUrl: z.string().url().optional(),
  createdAt: isoDateTimeSchema,
  updatedAt: isoDateTimeSchema,
  finishedAt: isoDateTimeSchema.optional(),
  error: z.string().max(2000).optional(),
});
export type Build = z.infer<typeof buildSchema>;

export const createBuildRequestSchema = z.object({
  seedId: seedIdSchema,
  /** Defaults to the Seed's chosen Blueprint. */
  blueprintId: blueprintIdSchema.optional(),
});
export type CreateBuildRequest = z.infer<typeof createBuildRequestSchema>;

/** One stage's gate verdict, as @4ge/gate evaluateStageGate returns it. */
export const stageGateDecisionSchema = z.object({
  admitted: z.boolean(),
  stage: stageSchema,
  unsatisfied: z.array(stageSchema),
  reason: z.string(),
});
export type StageGateDecision = z.infer<typeof stageGateDecisionSchema>;
