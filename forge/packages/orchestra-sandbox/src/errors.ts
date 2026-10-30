/**
 * Why a sandbox call could not do what was asked, in words a person can act
 * on. The server maps `code` onto its API error codes (docker_unavailable,
 * port_taken, conflict, not_found, bad_request, internal).
 */
export type SandboxErrorCode =
  | "docker_unavailable"
  | "port_taken"
  | "not_found"
  | "conflict"
  | "bad_request"
  | "internal";

export class SandboxError extends Error {
  readonly code: SandboxErrorCode;
  readonly hint: string | undefined;
  readonly port: number | undefined;
  readonly detail: unknown;

  constructor(code: SandboxErrorCode, message: string, options: { hint?: string; port?: number; detail?: unknown } = {}) {
    super(message);
    this.name = "SandboxError";
    this.code = code;
    this.hint = options.hint;
    this.port = options.port;
    this.detail = options.detail;
  }
}
