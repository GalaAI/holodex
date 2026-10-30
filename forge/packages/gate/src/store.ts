// Receipt storage and verification for 4GE builds. The verification idea is
// copied from GalaAI/ark scripts/4ge-stage-gate.mjs at 0f4b55c6: a receipt is
// evidence, not an announcement, so every artifact it names must exist, and
// when it names a commit that commit must exist and contain the artifacts.
// Adapted: receipts live under dataRoot()/builds/<id>/receipts/*.json and
// artifacts are relative to the build's app folder instead of a repo root.

import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, readdirSync, renameSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { STAGE_ORDER, receiptSchema, type Receipt, type StageGateDecision } from "@4ge/contracts";
import { dataPaths, resolveInBuild, type BuildPaths, type DataPaths } from "@4ge/contracts/node";
import { evaluateAllStages, stageSatisfied, type GateOptions } from "./gate.js";

/** verified: commit checked. no-commit: none named. unresolvable: commit gone (rebased or squashed). */
export type Provenance = "verified" | "no-commit" | "unresolvable";

export interface VerifiedReceipt extends Receipt {
  file: string;
  provenance: Provenance;
}

export interface LoadedReceipts {
  loaded: VerifiedReceipt[];
  /** One line per receipt that is malformed or makes a false claim. */
  broken: string[];
}

function git(cwd: string, args: string[]): string | null {
  try {
    return execFileSync("git", ["-C", cwd, ...args], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "ignore"],
      windowsHide: true,
    }).trim();
  } catch {
    return null;
  }
}

function receiptFileName(receipt: Receipt): string {
  return `${receipt.stage}${receipt.layerId ? `.${receipt.layerId}` : ""}.${receipt.id}.json`;
}

/**
 * Write a receipt. It is validated first and written atomically, so a
 * reader never sees half a file.
 */
export function recordReceipt(receipt: Receipt, paths: DataPaths = dataPaths()): string {
  const checked = receiptSchema.parse(receipt);
  const build = paths.build(checked.buildId);
  mkdirSync(build.receipts, { recursive: true });
  const target = join(build.receipts, receiptFileName(checked));
  const temp = `${target}.tmp`;
  writeFileSync(temp, `${JSON.stringify(checked, null, 2)}\n`, "utf8");
  renameSync(temp, target);
  return target;
}

/** Check one parsed receipt against the build's app folder. */
export function verifyReceipt(receipt: Receipt, build: BuildPaths): { provenance: Provenance } | { problem: string } {
  const missingNow = receipt.artifacts.filter((artifact) => !existsSync(resolveInBuild(build, artifact)));
  if (missingNow.length > 0) {
    return {
      problem: `${missingNow.length} artifact(s) absent: ${missingNow.slice(0, 3).join(", ")}`,
    };
  }
  if (!receipt.commit) return { provenance: "no-commit" };
  if (git(build.app, ["cat-file", "-e", `${receipt.commit}^{commit}`]) === null) {
    // A commit that no longer resolves is the normal end of a squashed or
    // rebased branch. Saying the gate cannot confirm provenance is true;
    // calling the stage undone is not.
    return { provenance: "unresolvable" };
  }
  const missingThen = receipt.artifacts.filter(
    (artifact) => git(build.app, ["cat-file", "-e", `${receipt.commit}:${artifact.replace(/\\/g, "/")}`]) === null,
  );
  if (missingThen.length > 0) {
    return {
      problem: `commit ${receipt.commit.slice(0, 8)} exists but lacks ${missingThen.length} claimed artifact(s): ${missingThen.slice(0, 3).join(", ")}`,
    };
  }
  return { provenance: "verified" };
}

/** Load and verify every receipt for a build. */
export function loadReceipts(buildId: string, paths: DataPaths = dataPaths()): LoadedReceipts {
  const build = paths.build(buildId);
  const loaded: VerifiedReceipt[] = [];
  const broken: string[] = [];
  if (!existsSync(build.receipts)) return { loaded, broken };
  for (const name of readdirSync(build.receipts).sort()) {
    if (!name.endsWith(".json")) continue;
    let raw: unknown;
    try {
      raw = JSON.parse(readFileSync(join(build.receipts, name), "utf8"));
    } catch (error) {
      broken.push(`${name}: not valid JSON (${String(error).slice(0, 80)})`);
      continue;
    }
    const parsed = receiptSchema.safeParse(raw);
    if (!parsed.success) {
      broken.push(
        `${name}: ${parsed.error.issues
          .slice(0, 3)
          .map((issue) => `${issue.path.join(".") || "root"} ${issue.message}`)
          .join("; ")}`,
      );
      continue;
    }
    if (parsed.data.buildId !== buildId) {
      broken.push(`${name}: belongs to ${parsed.data.buildId}, not ${buildId}`);
      continue;
    }
    const verdict = verifyReceipt(parsed.data, build);
    if ("problem" in verdict) {
      broken.push(`${name}: ${verdict.problem}`);
      continue;
    }
    loaded.push({ ...parsed.data, file: name, provenance: verdict.provenance });
  }
  return { loaded, broken };
}

export interface GateStatus {
  stages: StageGateDecision[];
  receipts: VerifiedReceipt[];
  broken: string[];
  /** Per stage: does a satisfying receipt exist, and was any attempt recorded. */
  summary: Array<{ stage: (typeof STAGE_ORDER)[number]; satisfied: boolean; attempted: boolean }>;
}

/** The whole sequence for one build: what the server returns on /builds/:id/receipts. */
export function gateStatus(buildId: string, options: GateOptions = {}, paths: DataPaths = dataPaths()): GateStatus {
  const { loaded, broken } = loadReceipts(buildId, paths);
  const stages = evaluateAllStages(loaded, options);
  const summary = STAGE_ORDER.map((stage) => ({
    stage,
    satisfied: stageSatisfied(stage, loaded, options),
    attempted: loaded.some((receipt) => receipt.stage === stage),
  }));
  return { stages, receipts: loaded, broken, summary };
}
