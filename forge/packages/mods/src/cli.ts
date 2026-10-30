import { existsSync } from "node:fs";
import { delimiter, join } from "node:path";
import { execa } from "execa";

/**
 * Running the user's own provider CLIs. execa (cross-spawn underneath)
 * resolves Windows .cmd shims such as vercel.cmd without a shell, so
 * arguments are never re-parsed by cmd.exe. Interactive commands open in
 * their own console window so the provider's prompts (browser sign-in,
 * purchase confirmation, secret entry) stay with the user.
 */

export interface RunResult {
  exitCode: number;
  stdout: string;
  stderr: string;
  timedOut: boolean;
}

export interface RunOptions {
  cwd?: string;
  env?: Record<string, string | undefined>;
  timeoutMs?: number;
}

export interface CommandRunner {
  /** Whether `bin` is on PATH (with PATHEXT on Windows). */
  which(bin: string): boolean;
  run(bin: string, args: string[], options?: RunOptions): Promise<RunResult>;
  /** Open the command in its own console window and return at once. */
  open(bin: string, args: string[], options?: RunOptions): Promise<void>;
}

export function onPath(bin: string, env: Record<string, string | undefined> = process.env, platform = process.platform): boolean {
  const dirs = (env.PATH ?? env.Path ?? "").split(delimiter).filter(Boolean);
  const exts =
    platform === "win32" ? ["", ...(env.PATHEXT ?? ".COM;.EXE;.BAT;.CMD").split(";").map((ext) => ext.toLowerCase())] : [""];
  for (const dir of dirs) {
    for (const ext of exts) {
      if (existsSync(join(dir, bin + ext))) return true;
    }
  }
  return false;
}

/** Strip anything that looks like a credential or an email from CLI output. */
export function redactOutput(text: string): string {
  return text
    .replace(/\x1b\[[0-9;]*[A-Za-z]/g, "")
    .replace(/\bBearer\s+[A-Za-z0-9._~+/=-]{8,}/gi, "[redacted]")
    .replace(/(authorization|api[_-]?key|token|secret|password|passwd)(["'\s:=]+)([^\s"',}]+)/gi, "$1$2[redacted]")
    .replace(/\b(sk|pk|rk|ghp|gho|ghs|xox[abp]|nf|sbp)[-_][A-Za-z0-9_-]{8,}\b/g, "[redacted]")
    .replace(/\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{5,}\b/g, "[redacted]")
    .replace(/[\w.+-]+@[\w-]+(\.[\w-]+)+/g, (email) => maskEmail(email));
}

/** j***@gmail.com: enough for the user to recognise their account, not a full address. */
export function maskEmail(email: string): string {
  const [local, domain] = email.split("@");
  if (!local || !domain) return "[email]";
  return `${local.charAt(0)}***@${domain}`;
}

export function clip(text: string, max = 8000): string {
  return text.length <= max ? text : `${text.slice(0, max - 20)}\n[output trimmed]`;
}

export class ExecaRunner implements CommandRunner {
  /** PATH lookups touch many folders on Windows, so answers are kept for a minute. */
  private readonly found = new Map<string, { at: number; on: boolean }>();

  constructor(private readonly baseEnv: Record<string, string | undefined> = process.env) {}

  which(bin: string): boolean {
    const hit = this.found.get(bin);
    if (hit && Date.now() - hit.at < 60_000) return hit.on;
    const on = onPath(bin, this.baseEnv);
    this.found.set(bin, { at: Date.now(), on });
    return on;
  }

  async run(bin: string, args: string[], options: RunOptions = {}): Promise<RunResult> {
    const result = await execa(bin, args, {
      cwd: options.cwd,
      env: { ...this.baseEnv, ...options.env, NO_COLOR: "1", CI: "1" },
      extendEnv: false,
      reject: false,
      timeout: options.timeoutMs ?? 20_000,
      windowsHide: true,
      stdin: "ignore",
      all: false,
    });
    return {
      exitCode: typeof result.exitCode === "number" ? result.exitCode : 1,
      stdout: String(result.stdout ?? ""),
      stderr: String(result.stderr ?? ""),
      timedOut: result.timedOut === true,
    };
  }

  async open(bin: string, args: string[], options: RunOptions = {}): Promise<void> {
    if (process.platform !== "win32") {
      throw new Error("Opening a CLI in its own window is supported on Windows only");
    }
    // start "" cmd /k <bin> <args>: a new console the user can read and answer.
    // Arguments are checked against strict patterns before they reach here.
    const child = execa("cmd.exe", ["/d", "/c", "start", "", "cmd", "/k", bin, ...args], {
      cwd: options.cwd,
      env: { ...this.baseEnv, ...options.env },
      extendEnv: false,
      detached: true,
      stdio: "ignore",
      reject: false,
      windowsHide: false,
    });
    await child;
  }
}
