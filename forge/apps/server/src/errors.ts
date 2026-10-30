import { API_ERROR_STATUS, type ApiError, type ApiErrorCode } from "@4ge/contracts";

/**
 * Throw an HttpError from any handler; the app turns it into the one JSON
 * error shape (@4ge/contracts apiErrorSchema). Messages are plain words a
 * person can act on: no stack traces, no secrets, no tokens.
 */
export class HttpError extends Error {
  readonly code: ApiErrorCode;
  readonly status: number;
  readonly hint: string | undefined;
  readonly detail: unknown;

  constructor(code: ApiErrorCode, message: string, options: { hint?: string; detail?: unknown; status?: number } = {}) {
    super(message);
    this.name = "HttpError";
    this.code = code;
    this.status = options.status ?? API_ERROR_STATUS[code];
    this.hint = options.hint;
    this.detail = options.detail;
  }

  toBody(): ApiError {
    return {
      error: {
        code: this.code,
        message: this.message,
        ...(this.hint ? { hint: this.hint } : {}),
        ...(this.detail !== undefined ? { detail: this.detail } : {}),
      },
    };
  }
}

export const notFound = (what: string) => new HttpError("not_found", `${what} was not found`);
export const badRequest = (message: string, detail?: unknown) =>
  new HttpError("bad_request", message, detail === undefined ? {} : { detail });

/** Strip anything that looks like a credential before it reaches a response or a log. */
export function redact(text: string): string {
  return text
    .replace(/(authorization|api[_-]?key|token|secret|password)(["'\s:=]+)([^\s"',}]+)/gi, "$1$2[redacted]")
    .replace(/\b(sk|pk|rk|ghp|gho|xox[abp])[-_][A-Za-z0-9_-]{8,}\b/g, "[redacted]")
    .replace(/\bBearer\s+[A-Za-z0-9._~+/=-]{8,}/gi, "Bearer [redacted]");
}

/** Convert anything thrown into an HttpError, without leaking internals. */
export function toHttpError(error: unknown): HttpError {
  if (error instanceof HttpError) return error;
  if (error && typeof error === "object" && "issues" in error && Array.isArray((error as { issues: unknown }).issues)) {
    const issues = (error as { issues: Array<{ path: PropertyKey[]; message: string }> }).issues;
    return badRequest(
      issues
        .slice(0, 5)
        .map((issue) => `${issue.path.map(String).join(".") || "body"}: ${issue.message}`)
        .join("; "),
      issues.slice(0, 20).map((issue) => ({ path: issue.path.map(String), message: issue.message })),
    );
  }
  const message = error instanceof Error ? error.message : String(error);
  return new HttpError("internal", `Something went wrong inside the 4GE server: ${redact(message).slice(0, 400)}`);
}
