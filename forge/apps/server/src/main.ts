import { PORTS } from "@4ge/contracts";
import { PortTakenError, startServer } from "./serve.js";

/**
 * Entry point. Holodex starts this with the user's keys in the environment
 * (NEBIUS_API_KEY, HIVEMIND_URL, HIVEMIND_TOKEN); nothing here prints them.
 * FORGE_PORT overrides the port for a second instance (never an Ark port).
 */
const port = process.env.FORGE_PORT ? Number(process.env.FORGE_PORT) : PORTS.server;

try {
  const running = await startServer({ port });
  console.log(`4GE server listening on ${running.url} (data: ${running.ctx.paths.root})`);
  let stopping = false;
  const shutdown = async (signal: string) => {
    if (stopping) return;
    stopping = true;
    console.log(`4GE server stopping (${signal})`);
    await running.stop();
    process.exit(0);
  };
  process.on("SIGINT", () => void shutdown("SIGINT"));
  process.on("SIGTERM", () => void shutdown("SIGTERM"));
} catch (error) {
  if (error instanceof PortTakenError) console.error(error.message);
  else console.error(`4GE server failed to start: ${error instanceof Error ? error.message : String(error)}`);
  process.exitCode = 1;
}
