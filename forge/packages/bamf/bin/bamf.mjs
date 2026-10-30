#!/usr/bin/env node
// Bin shim for the bamf CLI. It exists before the first build so pnpm can
// link it; it runs the compiled CLI from dist (run `pnpm build` first).
import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";

const cli = new URL("../dist/cli.js", import.meta.url);
if (!existsSync(fileURLToPath(cli))) {
  console.error("bamf is not built yet. Run pnpm -C forge build, or use pnpm --filter @4ge/bamf bamf for the source version.");
  process.exit(2);
}
const { main } = await import(cli.href);
process.exitCode = await main(process.argv.slice(2));
