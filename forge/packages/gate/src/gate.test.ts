import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { makeId, type Receipt, type Stage } from "@4ge/contracts";
import { dataPaths } from "@4ge/contracts/node";
import { afterAll, describe, expect, it } from "vitest";
import { evaluateAllStages, evaluateStageGate, parseReceipt, receiptSatisfies } from "./gate.js";
import { gateStatus, loadReceipts, recordReceipt } from "./store.js";

const buildId = makeId("build", "gate1");
let counter = 0;
function receipt(stage: Stage, passed = true, extra: Partial<Receipt> = {}): Receipt {
  counter += 1;
  return {
    id: makeId("receipt", `r${counter}`),
    buildId,
    stage,
    agentId: "hephaestus",
    artifacts: ["src/index.ts"],
    checks: [
      { name: "typecheck", passed: true },
      { name: "tests", passed },
    ],
    recordedAt: "2026-10-30T12:00:00.000Z",
    ...extra,
  };
}

describe("evaluateStageGate (copied semantics)", () => {
  it("admits the first stage with no receipts", () => {
    expect(evaluateStageGate("architecture", [])).toMatchObject({ admitted: true, unsatisfied: [] });
  });

  it("gates on every missing predecessor, not just the last", () => {
    const decision = evaluateStageGate("deploy", [receipt("orchestration"), receipt("interface")]);
    expect(decision.admitted).toBe(false);
    expect(decision.unsatisfied).toEqual(["architecture"]);
    expect(decision.reason).toBe("deploy is gated: architecture has no satisfying receipt");
  });

  it("treats a receipt with a failing check as an attempt, not a pass", () => {
    expect(receiptSatisfies(receipt("architecture", false))).toBe(false);
    expect(evaluateStageGate("orchestration", [receipt("architecture", false)]).admitted).toBe(false);
    expect(evaluateStageGate("orchestration", [receipt("architecture")]).admitted).toBe(true);
  });

  it("opens a stage once every planned layer has a satisfying layer receipt", () => {
    const layerA = makeId("layer", "a");
    const layerB = makeId("layer", "b");
    const layersByStage = { architecture: [layerA, layerB] };
    const one = [receipt("architecture", true, { layerId: layerA })];
    expect(evaluateStageGate("orchestration", one, { layersByStage }).admitted).toBe(false);
    const both = [...one, receipt("architecture", true, { layerId: layerB })];
    expect(evaluateStageGate("orchestration", both, { layersByStage }).admitted).toBe(true);
    expect(evaluateAllStages(both, { layersByStage }).map((decision) => decision.admitted)).toEqual([true, true, false, false]);
  });

  it("parses receipts with readable issues", () => {
    expect(parseReceipt(receipt("architecture")).ok).toBe(true);
    const bad = parseReceipt({ ...receipt("architecture"), commit: "abc" });
    expect(bad.ok).toBe(false);
    expect(bad.issues[0]).toContain("commit");
  });
});

describe("receipt store", () => {
  const root = mkdtempSync(join(tmpdir(), "4ge-gate-"));
  const paths = dataPaths(root);
  const build = paths.build(buildId);
  afterAll(() => rmSync(root, { recursive: true, force: true }));
  mkdirSync(join(build.app, "src"), { recursive: true });
  writeFileSync(join(build.app, "src", "index.ts"), "export {};\n");

  it("records, loads and verifies receipts against the build's app folder", () => {
    const written = recordReceipt(receipt("architecture"), paths);
    expect(written).toContain(join("builds", buildId, "receipts"));
    const { loaded, broken } = loadReceipts(buildId, paths);
    expect(broken).toEqual([]);
    expect(loaded).toHaveLength(1);
    expect(loaded[0]?.provenance).toBe("no-commit");
  });

  it("breaks a receipt that names a missing artifact or another build", () => {
    recordReceipt(receipt("orchestration", true, { artifacts: ["src/missing.ts"] }), paths);
    writeFileSync(join(build.receipts, "garbage.json"), "{ not json");
    const { loaded, broken } = loadReceipts(buildId, paths);
    expect(loaded.map((entry) => entry.stage)).toEqual(["architecture"]);
    expect(broken.some((line) => line.includes("absent: src/missing.ts"))).toBe(true);
    expect(broken.some((line) => line.startsWith("garbage.json: not valid JSON"))).toBe(true);
  });

  it("checks a named commit with git", () => {
    const git = (...args: string[]) =>
      execFileSync("git", ["-C", build.app, ...args], { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim();
    git("init", "-q");
    git("-c", "user.email=4ge@example.invalid", "-c", "user.name=4GE", "add", "src/index.ts");
    git("-c", "user.email=4ge@example.invalid", "-c", "user.name=4GE", "commit", "-q", "-m", "init");
    const sha = git("rev-parse", "HEAD");
    recordReceipt(receipt("interface", true, { commit: sha }), paths);
    recordReceipt(receipt("deploy", true, { commit: "f".repeat(40) }), paths);
    const status = gateStatus(buildId, {}, paths);
    const byStage = Object.fromEntries(status.receipts.map((entry) => [entry.stage, entry.provenance]));
    expect(byStage.interface).toBe("verified");
    expect(byStage.deploy).toBe("unresolvable");
    expect(status.summary.map((entry) => entry.satisfied)).toEqual([true, false, true, true]);
    expect(status.stages[2]?.admitted).toBe(false);
    expect(status.stages[2]?.unsatisfied).toEqual(["orchestration"]);
  });
});
