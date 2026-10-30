import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { SERVER_LAYERS, type BlueprintInput } from "@4ge/contracts";
import { afterAll, describe, expect, it } from "vitest";
import { main } from "./cli.js";
import { commandsFrom, parseBamfText, validateBamfDocument } from "./file.js";
import { BAMF_TARGET_LAYERS } from "./layers.js";
import { PlanError, planBlueprint } from "./plan.js";
import { bamfRiskLevelSchema, validateBamfCommand } from "./schema.js";
import { dryRunSteps } from "./steps.js";

const dir = mkdtempSync(join(tmpdir(), "4ge-bamf-"));
afterAll(() => rmSync(dir, { recursive: true, force: true }));

const command = (overrides: Record<string, unknown> = {}) => ({
  version: "0.1",
  id: "site.deploy",
  intent: "execute",
  targetLayer: "sandbox",
  target: "build:site",
  action: "sandbox.start",
  risk: "medium",
  args: { buildId: "bld_1" },
  ...overrides,
});

const blueprint = (): BlueprintInput => ({
  blueprintId: "blueprint:website-starter",
  name: "Website",
  version: "1.0.0",
  origin: "starter",
  kind: "website",
  platforms: ["web"],
  description: "A website",
  stages: [
    {
      stage: "architecture",
      goal: "Backend",
      layers: [
        { key: "model", kind: "data", title: "Model", agent: "hephaestus", description: "d", checks: ["ok"] },
        { key: "api", kind: "backend", title: "API", agent: "hephaestus", description: "d", dependsOn: ["model"], checks: ["ok"] },
        { key: "research", kind: "research", title: "Research", agent: "metis", description: "d", checks: ["ok"] },
      ],
      checkpoint: { title: "Arch", question: "Good?" },
    },
    {
      stage: "deploy",
      goal: "Ship",
      layers: [{ key: "flight", kind: "test-flight", title: "Flight", agent: "icarus", description: "d", dependsOn: ["api"], checks: ["green"] }],
      checkpoint: { title: "Deploy", question: "Ship it?" },
    },
  ],
});

function capture() {
  const out: string[] = [];
  const err: string[] = [];
  return { io: { out: (line: string) => out.push(line), err: (line: string) => err.push(line) }, out, err };
}

describe("schema copied from @ark/bamf-schema", () => {
  it("accepts a 4GE v0.1 command and refuses an Ark-only layer", () => {
    expect(validateBamfCommand(command()).ok).toBe(true);
    const ark = validateBamfCommand(command({ targetLayer: "web-console" }));
    expect(ark.ok).toBe(false);
    expect(ark.issues.join()).toContain("targetLayer");
  });

  it("keeps the v0.2 rule that provenance covers every argument", () => {
    const hash = "a".repeat(64);
    const v02 = {
      ...command({ version: "0.2" }),
      effects: {
        state: "mutating",
        externality: "internal",
        reversibility: "reversible",
        dataSensitivity: "internal",
        financialImpact: "none",
        environment: "sandbox",
        autonomy: "single_action",
        infrastructure: "none",
        credentialAccess: "none",
        destructive: false,
      },
      execution: {
        commandDefinitionRef: "cmd:sandbox.start",
        commandDefinitionHash: hash,
        argumentProvenance: {},
        controlFlowOwner: "registry-command-definition",
      },
      semantic: {
        candidateRef: "cand:1",
        candidateHash: hash,
        verificationReceiptRef: "rcp:1",
        verificationReceiptHash: hash,
        registrySnapshotRef: "snap:1",
        registrySnapshotHash: hash,
        resolvedRecords: [{ ref: "rec:1", revision: 1, contentHash: hash }],
        compilerVersion: "1.0.0",
        verifierVersion: "1.0.0",
        normalizationVersion: "1.0.0",
        exactInputDigest: hash,
        normalizedInputDigest: hash,
      },
    };
    const result = validateBamfCommand(v02);
    expect(result.ok).toBe(false);
    expect(result.issues.join()).toContain("argumentProvenance");
  });

  it("keeps the layer list in step with the server slots", () => {
    const expected = [...SERVER_LAYERS.filter((layer) => layer !== "core"), "studio", "server", "gate", "build"];
    expect([...BAMF_TARGET_LAYERS]).toEqual(expected);
  });

  it("keeps the risk tiers @4ge/kit ActionPill mirrors", () => {
    expect(bamfRiskLevelSchema.options).toEqual(["low", "medium", "high", "critical"]);
  });
});

describe("files", () => {
  it("reads JSON and YAML, single commands, arrays and scripts", () => {
    expect(commandsFrom(parseBamfText(JSON.stringify(command())))).toHaveLength(1);
    expect(commandsFrom(parseBamfText(JSON.stringify([command(), command({ id: "two" })])))).toHaveLength(2);
    const yaml = "commands:\n  - version: '0.1'\n    id: yaml.one\n    intent: plan\n    targetLayer: atlas\n    target: blueprint:x\n    action: blueprint.match\n";
    const result = validateBamfDocument(parseBamfText(yaml));
    expect(result.ok).toBe(true);
    expect(result.commands[0]?.risk).toBe("low");
  });

  it("flags duplicate ids and empty files", () => {
    expect(validateBamfDocument([command(), command()]).problems[0]?.issues[0]).toContain("duplicates");
    expect(() => parseBamfText("   ")).toThrow(/empty/);
  });
});

describe("planBlueprint", () => {
  it("orders layers into waves and closes each stage with a checkpoint", () => {
    const plan = planBlueprint(blueprint());
    const ids = plan.steps.map((step) => step.command.id);
    expect(ids).toEqual([
      "website-starter.model",
      "website-starter.research",
      "website-starter.api",
      "website-starter.architecture.checkpoint",
      "website-starter.flight",
      "website-starter.deploy.checkpoint",
    ]);
    const waveOf = (key: string) => plan.steps.find((step) => step.layerKey === key)?.wave;
    expect(waveOf("model")).toBe(waveOf("research"));
    expect(waveOf("api")).toBeGreaterThan(waveOf("model") ?? 0);
    const flight = plan.steps.find((step) => step.layerKey === "flight");
    expect(flight?.command).toMatchObject({ targetLayer: "sandbox", action: "testflight.run", risk: "medium" });
    expect(plan.steps.at(-1)?.command.operatorGate.required).toBe(true);
  });

  it("refuses cycles and dependencies on a later stage", () => {
    const cyclic = blueprint();
    cyclic.stages[0]!.layers[0]!.dependsOn = ["api"];
    expect(() => planBlueprint(cyclic)).toThrow(PlanError);
    const backwards = blueprint();
    backwards.stages[0]!.layers[0]!.dependsOn = ["flight"];
    expect(() => planBlueprint(backwards)).toThrow(/later stage/);
  });
});

describe("cli", () => {
  const good = join(dir, "good.bamf.json");
  const bad = join(dir, "bad.bamf.json");
  const bp = join(dir, "blueprint.json");
  writeFileSync(good, JSON.stringify([command(), command({ id: "gate.check", intent: "validate", targetLayer: "gate", operatorGate: { required: true, reason: "user approves" } })]));
  writeFileSync(bad, JSON.stringify(command({ intent: "explode" })));
  writeFileSync(bp, JSON.stringify(blueprint()));

  it("validate reports valid and invalid files with exit codes", async () => {
    const ok = capture();
    expect(await main(["validate", good], ok.io)).toBe(0);
    expect(ok.out[0]).toContain("2 valid commands");
    const no = capture();
    expect(await main(["validate", bad], no.io)).toBe(1);
    expect(no.err.join("\n")).toContain("intent");
  });

  it("plan prints the stage plan and run prints a dry run", async () => {
    const planned = capture();
    expect(await main(["plan", bp], planned.io)).toBe(0);
    expect(planned.out.join("\n")).toContain("// ARCHITECTURE");
    const ran = capture();
    expect(await main(["run", good, "--dry-run"], ran.io)).toBe(0);
    expect(ran.out[0]).toContain("nothing executed");
    expect(ran.out[2]).toContain("waits for a person: user approves");
    expect(dryRunSteps(validateBamfDocument(command()).commands)[0]).toContain("execute sandbox.start on sandbox/build:site");
  });

  it("refuses a live run, unknown commands and missing files", async () => {
    const live = capture();
    expect(await main(["run", good], live.io)).toBe(2);
    expect(live.err[0]).toContain("--dry-run");
    expect(await main(["explode", good], capture().io)).toBe(2);
    expect(await main(["validate"], capture().io)).toBe(2);
    expect(await main(["validate", join(dir, "missing.json")], capture().io)).toBe(1);
  });
});
