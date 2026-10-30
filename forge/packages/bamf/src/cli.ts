#!/usr/bin/env node
import { pathToFileURL } from "node:url";
import { readBamfFile, validateBamfDocument } from "./file.js";
import { PlanError, planBlueprint } from "./plan.js";
import { describePlan, dryRunSteps } from "./steps.js";

/**
 * bamf: the 4GE .bamf command line.
 *
 *   bamf validate <file>             check every command in a .bamf file
 *   bamf plan <blueprint.json> [--json]  print the ordered plan for a Blueprint
 *   bamf run <file> --dry-run        print the steps a run would take
 *
 * Exit codes: 0 ok, 1 invalid input, 2 usage.
 */
export interface CliIo {
  out: (line: string) => void;
  err: (line: string) => void;
}

const USAGE = [
  "Usage:",
  "  bamf validate <file>",
  "  bamf plan <blueprint.json> [--json]",
  "  bamf run <file> --dry-run",
];

export async function main(argv: readonly string[], io: CliIo = { out: console.log, err: console.error }): Promise<number> {
  const [command, file, ...flags] = argv;
  if (!command || command === "help" || command === "--help" || command === "-h") {
    for (const line of USAGE) io.out(line);
    return command ? 0 : 2;
  }
  if (!file) {
    io.err(`bamf ${command} needs a file.`);
    for (const line of USAGE) io.err(line);
    return 2;
  }

  let document: unknown;
  try {
    document = await readBamfFile(file);
  } catch (error) {
    io.err(error instanceof Error ? error.message : String(error));
    return 1;
  }

  switch (command) {
    case "validate": {
      const result = validateBamfDocument(document);
      if (result.ok) {
        io.out(`${file}: ${result.commands.length} valid command${result.commands.length === 1 ? "" : "s"}`);
        return 0;
      }
      io.err(`${file}: ${result.problems.length} invalid command${result.problems.length === 1 ? "" : "s"}`);
      for (const problem of result.problems) {
        io.err(`  command ${problem.index + 1}${problem.id ? ` (${problem.id})` : ""}:`);
        for (const issue of problem.issues) io.err(`    ${issue}`);
      }
      return 1;
    }
    case "plan": {
      try {
        const plan = planBlueprint(document);
        if (flags.includes("--json")) io.out(JSON.stringify(plan, null, 2));
        else for (const line of describePlan(plan)) io.out(line);
        return 0;
      } catch (error) {
        if (error instanceof PlanError) {
          io.err(error.message);
          return 1;
        }
        if (error && typeof error === "object" && "issues" in error && Array.isArray((error as { issues: unknown[] }).issues)) {
          io.err(`${file} is not a valid Blueprint:`);
          for (const issue of (error as { issues: Array<{ path: PropertyKey[]; message: string }> }).issues) {
            io.err(`  ${issue.path.map(String).join(".") || "blueprint"}: ${issue.message}`);
          }
          return 1;
        }
        throw error;
      }
    }
    case "run": {
      const result = validateBamfDocument(document);
      if (!result.ok) {
        io.err(`${file} has invalid commands; run bamf validate ${file} for details.`);
        return 1;
      }
      if (!flags.includes("--dry-run")) {
        io.err(
          "bamf run only does --dry-run. Live runs happen inside the 4GE server, where the orchestra and the stage gate execute each step.",
        );
        return 2;
      }
      io.out(`Dry run of ${file}: ${result.commands.length} step${result.commands.length === 1 ? "" : "s"}, nothing executed`);
      for (const line of dryRunSteps(result.commands)) io.out(line);
      return 0;
    }
    default:
      io.err(`Unknown command "${command}".`);
      for (const line of USAGE) io.err(line);
      return 2;
  }
}

const invokedDirectly = process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href;
if (invokedDirectly) {
  main(process.argv.slice(2)).then(
    (code) => {
      process.exitCode = code;
    },
    (error: unknown) => {
      console.error(error instanceof Error ? error.message : String(error));
      process.exitCode = 1;
    },
  );
}
