// 4GE sandbox app launcher. Decides how to run the build's app copy in
// /workspace: its own `start` script when it has one, otherwise the static
// server on the first folder that holds an index.html.
import { spawn } from "node:child_process";
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { startStaticServer } from "./serve.mjs";

const workspace = process.env.FORGE_APP_DIR ?? "/workspace";
const port = Number(process.env.PORT ?? 3000);

let pkg = null;
if (existsSync(join(workspace, "package.json"))) {
  try {
    pkg = JSON.parse(readFileSync(join(workspace, "package.json"), "utf8"));
  } catch (error) {
    console.error(`sandbox: package.json is not valid JSON (${error.message}); serving files instead.`);
  }
}

if (pkg?.scripts?.start) {
  console.log("sandbox: running the app's start script");
  const child = spawn("npm", ["run", "start", "--silent"], {
    cwd: workspace,
    stdio: "inherit",
    env: { ...process.env, PORT: String(port), HOST: "0.0.0.0" },
  });
  for (const signal of ["SIGTERM", "SIGINT"]) process.on(signal, () => child.kill(signal));
  child.on("exit", (code, signal) => process.exit(code ?? (signal ? 1 : 0)));
} else {
  const candidates = ["dist", "build", "out", "public", "site", "."];
  const root = candidates.map((name) => join(workspace, name)).find((dir) => existsSync(join(dir, "index.html")));
  const anyHtml = existsSync(workspace) && readdirSync(workspace).some((name) => name.endsWith(".html"));
  if (!root && !anyHtml) {
    console.error("sandbox: the build's app folder has no start script and no index.html, so there is nothing to serve.");
    process.exit(3);
  }
  const server = startStaticServer(root ?? workspace, port);
  for (const signal of ["SIGTERM", "SIGINT"]) process.on(signal, () => server.close(() => process.exit(0)));
}
