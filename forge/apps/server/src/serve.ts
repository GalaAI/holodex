import { FORGE_HOST, PORTS, assertForgePort } from "@4ge/contracts";
import { serve, type ServerType } from "@hono/node-server";
import { createApp, type AppOptions, type ForgeApp } from "./app.js";

export interface StartOptions extends AppOptions {
  port?: number;
  host?: string;
}

export interface RunningServer extends ForgeApp {
  port: number;
  url: string;
  server: ServerType;
  stop(): Promise<void>;
}

export class PortTakenError extends Error {
  readonly port: number;
  constructor(port: number) {
    super(
      `Port ${port} is already in use, so the 4GE server cannot start. Another 4GE server may already be running; stop it or set FORGE_PORT to a free port.`,
    );
    this.name = "PortTakenError";
    this.port = port;
  }
}

/** Start the server on 127.0.0.1 (never 0.0.0.0) and resolve once listening. */
export async function startServer(options: StartOptions = {}): Promise<RunningServer> {
  const port = assertForgePort(options.port ?? PORTS.server);
  const host = options.host ?? FORGE_HOST;
  const forge = await createApp({ ...options, port });
  const server = await new Promise<ServerType>((resolvePromise, reject) => {
    const instance = serve({ fetch: forge.app.fetch, port, hostname: host }, () => resolvePromise(instance));
    instance.once("error", (error: NodeJS.ErrnoException) => {
      reject(error.code === "EADDRINUSE" ? new PortTakenError(port) : error);
    });
  });
  const address = server.address();
  const actualPort = typeof address === "object" && address ? address.port : port;
  return {
    ...forge,
    port: actualPort,
    url: `http://${host}:${actualPort}`,
    server,
    async stop() {
      await forge.ctx.close();
      await new Promise<void>((resolveClose) => {
        if ("closeAllConnections" in server && typeof server.closeAllConnections === "function") {
          server.closeAllConnections();
        }
        server.close(() => resolveClose());
      });
    },
  };
}
