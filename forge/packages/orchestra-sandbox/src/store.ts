import { existsSync, mkdirSync, readFileSync, readdirSync, renameSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { sandboxRunSchema, testFlightSchema, type SandboxRun, type TestFlight } from "@4ge/contracts";
import type { DataPaths } from "@4ge/contracts/node";

/**
 * Sandbox runs and test flights on disk, under dataRoot()/sandbox:
 * runs/<run_id>.json and flights/<tfl_id>.json. Each write is atomic. A copy
 * of each flight also lands in the build's evidence folder beside its
 * screenshots.
 */
export class SandboxStore {
  readonly runsDir: string;
  readonly flightsDir: string;

  constructor(private readonly paths: DataPaths) {
    this.runsDir = join(paths.sandbox, "runs");
    this.flightsDir = join(paths.sandbox, "flights");
  }

  private write(dir: string, name: string, value: unknown): void {
    mkdirSync(dir, { recursive: true });
    const target = join(dir, name);
    const temp = `${target}.tmp`;
    writeFileSync(temp, `${JSON.stringify(value, null, 2)}\n`, "utf8");
    renameSync(temp, target);
  }

  private readAll<T>(dir: string, parse: (raw: unknown) => T | null): T[] {
    if (!existsSync(dir)) return [];
    const out: T[] = [];
    for (const name of readdirSync(dir).sort()) {
      if (!name.endsWith(".json")) continue;
      try {
        const parsed = parse(JSON.parse(readFileSync(join(dir, name), "utf8")));
        if (parsed) out.push(parsed);
      } catch {
        // A damaged record is skipped; the run or flight it named is gone.
      }
    }
    return out;
  }

  saveRun(run: SandboxRun): void {
    this.write(this.runsDir, `${run.id}.json`, sandboxRunSchema.parse(run));
  }

  loadRuns(): SandboxRun[] {
    return this.readAll(this.runsDir, (raw) => {
      const parsed = sandboxRunSchema.safeParse(raw);
      return parsed.success ? parsed.data : null;
    });
  }

  saveFlight(flight: TestFlight, evidenceDir?: string): void {
    const checked = testFlightSchema.parse(flight);
    this.write(this.flightsDir, `${checked.id}.json`, checked);
    if (evidenceDir) this.write(evidenceDir, "flight.json", checked);
  }

  loadFlights(): TestFlight[] {
    return this.readAll(this.flightsDir, (raw) => {
      const parsed = testFlightSchema.safeParse(raw);
      return parsed.success ? parsed.data : null;
    });
  }

  /** Where a build's sandbox logs and test flight evidence go. */
  evidenceDir(buildId: string, name: string): string {
    return join(this.paths.build(buildId).evidence, name);
  }
}
