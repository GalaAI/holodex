// Copied from GalaAI/ark packages/shared-types/src/stage-gate.ts at 0f4b55c6
// for @4ge/gate. The stage list, order and receipt schema now live in
// @4ge/contracts (STAGE_ORDER, receiptSchema) so the studio and the server
// share them. Change for 4GE: a stage is also satisfied when every layer the
// build planned for it has a satisfying layer receipt, because the orchestra
// records receipts per layer as parallel sessions finish.
//
// The rule this enforces: "Smith refuses work aimed at a stage whose
// predecessor has not produced a receipt." A stage that ran and a stage that
// succeeded are different answers. A receipt carrying a failed check is
// evidence the stage was attempted, and it does not open the gate.

import {
  STAGE_ORDER,
  receiptSchema,
  type Receipt,
  type Stage,
  type StageGateDecision,
} from "@4ge/contracts";

/**
 * A receipt satisfies the gate only when every check on it passed. A receipt
 * with a failing check records that the stage was attempted, which is worth
 * keeping and is not worth advancing on.
 */
export function receiptSatisfies(receipt: Pick<Receipt, "checks">): boolean {
  return receipt.checks.length > 0 && receipt.checks.every((check) => check.passed);
}

export interface GateOptions {
  /**
   * Layer ids the build planned per stage. When given, a stage with no
   * whole-stage receipt is still satisfied once each of its layers has a
   * satisfying layer receipt.
   */
  layersByStage?: Partial<Record<Stage, readonly string[]>>;
}

/** Whether one stage has a satisfying receipt (whole-stage or every layer). */
export function stageSatisfied(
  stage: Stage,
  receipts: readonly Receipt[],
  options: GateOptions = {},
): boolean {
  const satisfying = receipts.filter((receipt) => receipt.stage === stage && receiptSatisfies(receipt));
  if (satisfying.some((receipt) => receipt.layerId === undefined)) return true;
  const planned = options.layersByStage?.[stage];
  if (!planned || planned.length === 0) return false;
  const covered = new Set(satisfying.map((receipt) => receipt.layerId));
  return planned.every((layerId) => covered.has(layerId));
}

/**
 * Decide whether work aimed at `stage` may proceed.
 *
 * Every earlier stage must have a satisfying receipt. Checking only the
 * immediate predecessor would let a gap two stages back pass unnoticed once one
 * stage was skipped, which is the failure a sequence exists to prevent.
 */
export function evaluateStageGate(
  stage: Stage,
  receipts: readonly Receipt[],
  options: GateOptions = {},
): StageGateDecision {
  const predecessors = STAGE_ORDER.slice(0, STAGE_ORDER.indexOf(stage));
  const unsatisfied = predecessors.filter((earlier) => !stageSatisfied(earlier, receipts, options));

  if (unsatisfied.length === 0) {
    return {
      admitted: true,
      stage,
      unsatisfied: [],
      reason:
        predecessors.length === 0
          ? `${stage} is the first stage and has no predecessor`
          : `every stage before ${stage} produced a satisfying receipt`,
    };
  }

  return {
    admitted: false,
    stage,
    unsatisfied,
    reason:
      `${stage} is gated: ${unsatisfied.join(", ")} ` +
      `${unsatisfied.length === 1 ? "has" : "have"} no satisfying receipt`,
  };
}

/** The gate verdict for every stage, in order. */
export function evaluateAllStages(receipts: readonly Receipt[], options: GateOptions = {}): StageGateDecision[] {
  return STAGE_ORDER.map((stage) => evaluateStageGate(stage, receipts, options));
}

export interface ReceiptParse {
  ok: boolean;
  receipt?: Receipt;
  issues: string[];
}

export function parseReceipt(input: unknown): ReceiptParse {
  const parsed = receiptSchema.safeParse(input);
  if (!parsed.success) {
    return {
      ok: false,
      issues: parsed.error.issues.map((issue) => `${issue.path.join(".") || "root"}: ${issue.message}`),
    };
  }
  return { ok: true, receipt: parsed.data, issues: [] };
}
