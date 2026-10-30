import type { BamfCommand } from "./schema.js";
import type { BlueprintPlan } from "./plan.js";

/** One printable line per command: what a dry run would do, in order. */
export function describeCommand(command: BamfCommand): string {
  const gate = command.operatorGate.required
    ? ` [waits for a person${command.operatorGate.reason ? `: ${command.operatorGate.reason}` : ""}]`
    : "";
  const effects =
    command.version === "0.2"
      ? ` (${command.effects.state}, ${command.effects.environment}${command.effects.destructive ? ", destructive" : ""})`
      : "";
  return `${command.intent} ${command.action} on ${command.targetLayer}/${command.target}, risk ${command.risk}${effects}${gate}`;
}

export function dryRunSteps(commands: readonly BamfCommand[]): string[] {
  return commands.map((command, index) => `${String(index + 1).padStart(2)}. ${command.id}: ${describeCommand(command)}`);
}

export function describePlan(plan: BlueprintPlan): string[] {
  const lines = [`Plan for ${plan.blueprintId}: ${plan.steps.length} steps`];
  let stage = "";
  for (const step of plan.steps) {
    if (step.stage !== stage) {
      stage = step.stage;
      lines.push("", `// ${stage.toUpperCase()}`);
    }
    lines.push(
      `${String(step.index).padStart(2)}. wave ${step.wave}  ${step.command.id}: ${describeCommand(step.command)}`,
    );
  }
  return lines;
}
