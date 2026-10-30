/**
 * 4GE ports (SPEC "Architecture"). The Ark supervisor owns 5300, 8788 and
 * 4200 and Ark development owns 15300, 18788 and 14200; 4GE never binds any
 * of them, and assertForgePort refuses them so a typo cannot take one.
 */
export const PORTS = {
  server: 17400,
  studioDev: 17401,
  sandboxApp: 17410,
  sandboxCdp: 17411,
  sandboxViewer: 17412,
} as const;
export type PortName = keyof typeof PORTS;

export const RESERVED_PORTS = [5300, 8788, 4200, 15300, 18788, 14200] as const;

export const FORGE_HOST = "127.0.0.1";

export function isReservedPort(port: number): boolean {
  return (RESERVED_PORTS as readonly number[]).includes(port);
}

/** Throws when a port belongs to Ark. Use before every listen(). */
export function assertForgePort(port: number): number {
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error(`Port ${port} is not a valid TCP port`);
  }
  if (isReservedPort(port)) {
    throw new Error(
      `Port ${port} belongs to Ark and 4GE must not bind it. Use one of the 4GE ports: ${Object.values(PORTS).join(", ")}`,
    );
  }
  return port;
}

export function serverOrigin(port: number = PORTS.server): string {
  return `http://${FORGE_HOST}:${port}`;
}
