import {
  STAGE_ORDER,
  STAGE_OWNERS,
  blueprintSchema,
  type Blueprint,
  type LayerPlan,
  type Stage,
} from "@4ge/contracts";
import { bamfCommandV01Schema, type BamfCommandV01 } from "./schema.js";

/**
 * Turn a Blueprint's stage plan into ordered .bamf v0.1 commands: one
 * command per layer, stages in gate order, layers inside a stage after the
 * layers they depend on, then one gate check per stage. This is the plan the
 * orchestra runs; `bamf plan <blueprint>` prints it.
 */
export interface PlanStep {
  index: number;
  stage: Stage;
  /** Layers in the same wave can run in parallel. */
  wave: number;
  layerKey?: string;
  command: BamfCommandV01;
}

export interface BlueprintPlan {
  blueprintId: string;
  steps: PlanStep[];
}

export class PlanError extends Error {}

function layerCommand(blueprint: Blueprint, stage: Stage, layer: LayerPlan): BamfCommandV01 {
  const deploys = layer.kind === "deploy" || layer.kind === "test-flight";
  return bamfCommandV01Schema.parse({
    version: "0.1",
    id: `${blueprint.blueprintId.replace(/^blueprint:/, "")}.${layer.key}`,
    intent: "execute",
    targetLayer: deploys ? "sandbox" : "orchestra",
    target: `build:${layer.key}`,
    action: layer.kind === "test-flight" ? "testflight.run" : "layer.build",
    args: {
      stage,
      kind: layer.kind,
      agent: layer.agent,
      title: layer.title,
      dependsOn: layer.dependsOn,
      deliverables: layer.deliverables,
      checks: layer.checks,
    },
    risk: deploys ? "medium" : "low",
    operatorGate: { required: false },
    metadata: { source: blueprint.blueprintId },
  });
}

function gateCommand(blueprint: Blueprint, stage: Stage, question: string): BamfCommandV01 {
  return bamfCommandV01Schema.parse({
    version: "0.1",
    id: `${blueprint.blueprintId.replace(/^blueprint:/, "")}.${stage}.checkpoint`,
    intent: "validate",
    targetLayer: "gate",
    target: `stage:${stage}`,
    action: "checkpoint.open",
    args: { stage, owner: STAGE_OWNERS[stage].owner, question },
    risk: "low",
    operatorGate: { required: true, reason: "The user answers the sprint checkpoint" },
    metadata: { source: blueprint.blueprintId },
  });
}

/**
 * Waves inside one stage: a layer joins the first wave after every
 * in-stage dependency. Dependencies on earlier stages are already met by
 * stage order; dependencies on later stages are a plan error.
 */
function waves(stage: Stage, layers: LayerPlan[], stageOfKey: Map<string, Stage>): LayerPlan[][] {
  const stageIndex = STAGE_ORDER.indexOf(stage);
  const pending = new Map(layers.map((layer) => [layer.key, layer]));
  const placed = new Set<string>();
  const result: LayerPlan[][] = [];
  for (const layer of layers) {
    for (const dependency of layer.dependsOn) {
      const dependencyStage = stageOfKey.get(dependency);
      if (dependencyStage && STAGE_ORDER.indexOf(dependencyStage) > stageIndex) {
        throw new PlanError(
          `Layer ${layer.key} in ${stage} depends on ${dependency}, which is in the later stage ${dependencyStage}`,
        );
      }
    }
  }
  while (pending.size > 0) {
    const wave = [...pending.values()].filter((layer) =>
      layer.dependsOn.every(
        (dependency) => stageOfKey.get(dependency) !== stage || placed.has(dependency),
      ),
    );
    if (wave.length === 0) {
      throw new PlanError(
        `Layers in ${stage} depend on each other in a cycle: ${[...pending.keys()].join(", ")}`,
      );
    }
    for (const layer of wave) {
      pending.delete(layer.key);
      placed.add(layer.key);
    }
    result.push(wave);
  }
  return result;
}

export function planBlueprint(input: unknown): BlueprintPlan {
  const blueprint = blueprintSchema.parse(input);
  const stageOfKey = new Map<string, Stage>();
  for (const plan of blueprint.stages) {
    for (const layer of plan.layers) stageOfKey.set(layer.key, plan.stage);
  }
  const steps: PlanStep[] = [];
  let waveNumber = 0;
  for (const plan of blueprint.stages) {
    for (const wave of waves(plan.stage, plan.layers, stageOfKey)) {
      waveNumber += 1;
      for (const layer of wave) {
        steps.push({
          index: steps.length + 1,
          stage: plan.stage,
          wave: waveNumber,
          layerKey: layer.key,
          command: layerCommand(blueprint, plan.stage, layer),
        });
      }
    }
    waveNumber += 1;
    steps.push({
      index: steps.length + 1,
      stage: plan.stage,
      wave: waveNumber,
      command: gateCommand(blueprint, plan.stage, plan.checkpoint.question),
    });
  }
  return { blueprintId: blueprint.blueprintId, steps };
}
