import { AGENT_ROSTER, type AgentId } from "@4ge/contracts";
import { Button, Led, type LedState } from "@4ge/kit";
import type { ReactNode } from "react";
import type { ForgeApiError } from "../lib/api.js";

/**
 * Small shared pieces every studio feature uses: status lamps for any work
 * status word, agent names, and plain-word error and empty states.
 */

const LIVE = new Set(["running", "queued", "starting", "test-flight", "stopping"]);
const OK = new Set(["passed", "done", "approved", "built", "ready"]);
const ERR = new Set(["failed", "rejected"]);

/** Map any status word from the contracts to one of the four lamps. */
export function statusLed(status: string): LedState {
  if (LIVE.has(status)) return "live";
  if (OK.has(status)) return "ok";
  if (ERR.has(status)) return "err";
  return "warn";
}

const STATUS_WORDS: Record<string, string> = {
  queued: "Queued",
  running: "Running",
  "awaiting-checkpoint": "Waiting for you",
  "test-flight": "Test flight",
  passed: "Passed",
  failed: "Failed",
  cancelled: "Cancelled",
  pending: "Pending",
  blocked: "Blocked",
  idle: "Idle",
  waiting: "Waiting",
  done: "Done",
  starting: "Starting",
  stopping: "Stopping",
  stopped: "Stopped",
  skipped: "Skipped",
  open: "Open",
  approved: "Approved",
  revise: "Revise",
  "not-run": "Not run",
  "not-covered": "Not covered",
};

export function statusWord(status: string): string {
  return STATUS_WORDS[status] ?? status.charAt(0).toUpperCase() + status.slice(1);
}

export function StatusLed({ status, children }: { status: string; children?: ReactNode }) {
  return (
    <Led state={statusLed(status)} data-status={status}>
      {children ?? statusWord(status)}
    </Led>
  );
}

export function agentName(agent: AgentId | "user" | undefined): string {
  if (!agent) return "Agent";
  if (agent === "user") return "You";
  return AGENT_ROSTER[agent].name;
}

/** Plain words for an API failure: what happened, and what to do next. */
export function describeError(error: ForgeApiError | Error): { title: string; body: string; hint?: string } {
  const code = "code" in error ? (error as ForgeApiError).code : undefined;
  const hint = "hint" in error ? (error as ForgeApiError).hint : undefined;
  switch (code) {
    case "network":
      return { title: "The 4GE server is not answering", body: error.message, hint: hint ?? "Start the 4GE server, or check that Holodex started it." };
    case "not_implemented":
      return { title: "This part of 4GE is not connected yet", body: error.message, ...(hint ? { hint } : {}) };
    case "model_unavailable":
      return { title: "No model is connected", body: error.message, hint: hint ?? "Set NEBIUS_API_KEY (or HIVEMIND_URL and HIVEMIND_TOKEN) for the 4GE server." };
    case "model_error":
      return { title: "The model answered with an error", body: error.message, hint: hint ?? "Try again. If it keeps failing, check your model provider." };
    case "not_found":
      return { title: "Not found", body: error.message, ...(hint ? { hint } : {}) };
    case "cancelled":
      return { title: "Cancelled", body: error.message, ...(hint ? { hint } : {}) };
    case "docker_unavailable":
      return { title: "Docker is not running", body: error.message, hint: hint ?? "Start Docker Desktop, then try again." };
    case "port_taken":
      return { title: "A sandbox port is taken", body: error.message, ...(hint ? { hint } : {}) };
    case "invalid_response":
      return { title: "The server sent something unexpected", body: error.message, ...(hint ? { hint } : {}) };
    default:
      return { title: "Something went wrong", body: error.message, ...(hint ? { hint } : {}) };
  }
}

export function ErrorNotice({
  error,
  onRetry,
  compact = false,
}: {
  error: ForgeApiError | Error;
  onRetry?: () => void;
  compact?: boolean;
}) {
  const text = describeError(error);
  return (
    <div className={`studio-notice studio-notice--err${compact ? " studio-notice--compact" : ""}`} role="alert">
      <strong>{text.title}</strong>
      <span>{text.body}</span>
      {text.hint ? <span className="studio-notice__hint">{text.hint}</span> : null}
      {onRetry ? (
        <span>
          <Button onClick={onRetry}>Try again</Button>
        </span>
      ) : null}
    </div>
  );
}

export function Notice({ tone = "info", children }: { tone?: "info" | "warn" | "err" | "ok"; children: ReactNode }) {
  return (
    <div className={`studio-notice studio-notice--${tone}`} role="status">
      {children}
    </div>
  );
}

/** Elapsed time as 12s, 3m 04s or 1h 02m. */
export function elapsed(fromIso: string, toIso?: string, now = Date.now()): string {
  const start = Date.parse(fromIso);
  const end = toIso ? Date.parse(toIso) : now;
  const seconds = Math.max(0, Math.round((end - start) / 1000));
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ${String(seconds % 60).padStart(2, "0")}s`;
  return `${Math.floor(minutes / 60)}h ${String(minutes % 60).padStart(2, "0")}m`;
}

export function shortTime(iso: string): string {
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}
