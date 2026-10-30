// Prints what the seed must do for a package.json: "deps" when it declares
// dependencies, "build" when it has a build script. Used by seed.sh.
import { readFileSync } from "node:fs";

const pkg = JSON.parse(readFileSync(process.argv[2] ?? "/workspace/package.json", "utf8"));
const deps = Object.keys(pkg.dependencies ?? {}).length + Object.keys(pkg.devDependencies ?? {}).length;
const scripts = pkg.scripts ?? {};
console.log([deps > 0 ? "deps" : "", scripts.build ? "build" : ""].filter(Boolean).join(" "));
