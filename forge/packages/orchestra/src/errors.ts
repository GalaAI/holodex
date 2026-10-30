import type { ApiErrorCode } from "@4ge/contracts";

/**
 * The one error the orchestra throws. It carries an API error code from
 * @4ge/contracts so the server can answer with the matching status, and a
 * message in plain words a person can act on (never a key, never a stack).
 */
export class OrchestraError extends Error {
  readonly code: ApiErrorCode;
  readonly hint: string | undefined;

  constructor(code: ApiErrorCode, message: string, hint?: string) {
    super(message);
    this.name = "OrchestraError";
    this.code = code;
    this.hint = hint;
  }
}

export const notFound = (what: string) => new OrchestraError("not_found", `${what} was not found`);

export function isOrchestraError(error: unknown, code?: ApiErrorCode): error is OrchestraError {
  return error instanceof OrchestraError && (code === undefined || error.code === code);
}

export function isAbort(error: unknown, signal?: AbortSignal): boolean {
  if (signal?.aborted) return true;
  if (isOrchestraError(error, "cancelled")) return true;
  return error instanceof Error && (error.name === "AbortError" || error.name === "APIUserAbortError");
}

export function cancelled(what = "The work"): OrchestraError {
  return new OrchestraError("cancelled", `${what} was cancelled`);
}

/** Strip anything that looks like a credential from text that may reach a person. */
export function redact(text: string): string {
  return text
    // Bearer first: "Authorization: Bearer x" must lose x, not the word Bearer.
    .replace(/\bBearer\s+[A-Za-z0-9._~+/=-]{8,}/gi, "Bearer [redacted]")
    // "token: x" or "api_key=x" always; "token x" only when x looks like a secret, so "Nebius Token Factory" stays readable.
    .replace(/(authorization|api[_-]?key|token|secret|password)(\s*["']?\s*[:=]\s*["']?)(?!Bearer \[redacted\])([^\s"',}]+)/gi, "$1$2[redacted]")
    .replace(/(authorization|api[_-]?key|token|secret|password)(\s+)((?=[A-Za-z0-9._~+/=-]*\d)[A-Za-z0-9._~+/=-]{16,})/gi, "$1$2[redacted]")
    .replace(/\b(sk|pk|rk|ghp|gho|xox[abp])[-_][A-Za-z0-9_-]{8,}\b/g, "[redacted]")
    .replace(/\b[A-Za-z0-9_-]{120,}\b/g, "[redacted]");
}

export function messageOf(error: unknown): string {
  return redact(error instanceof Error ? error.message : String(error)).slice(0, 600);
}
