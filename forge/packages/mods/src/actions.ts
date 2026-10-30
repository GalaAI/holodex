import type { Adapter, AdapterAction, AdapterActionRun, ConnectAction } from "@4ge/contracts";
import { findAdapter, type AdapterDefinition } from "./catalog/adapters.js";
import { clip, redactOutput } from "./cli.js";
import { adapterCredentialTarget, CredentialError } from "./credentials.js";
import { AdapterDetector, HOLODEX_NEBIUS_TARGET, adapterEnvironment } from "./detect.js";

/**
 * Connecting adapters and running their actions with the user's own CLI.
 *
 * - cli-login adapters: 4GE opens the provider's login command in its own
 *   window; the provider stores its own session. 4GE stores nothing.
 * - token adapters: the token goes straight to Windows Credential Manager
 *   (target 4GE:adapter:<slug>) and is checked live; it is never echoed.
 * - actions: parameters are checked against strict patterns before they
 *   reach a CLI; actions that spend money need confirm: true, and the CLI
 *   asks again in its own window.
 */

export class ActionError extends Error {
  override readonly name = "ActionError";
  constructor(
    readonly code: "not_found" | "bad_request" | "credential_missing",
    message: string,
  ) {
    super(message);
  }
}

function definition(id: string): AdapterDefinition {
  const def = findAdapter(id);
  if (!def) throw new ActionError("not_found", `There is no adapter called ${id}`);
  return def;
}

export interface ConnectResult {
  adapter: Adapter;
  action: ConnectAction;
}

export async function connectAdapter(detector: AdapterDetector, id: string, token?: string): Promise<ConnectResult> {
  const def = definition(id);
  const deps = detector.dependencies;
  if (def.auth.method === "token") {
    const target = adapterCredentialTarget(def.slug);
    if (token && token.trim().length > 0) {
      try {
        await deps.credentials.write(target, token.trim());
      } catch (error) {
        throw new ActionError("credential_missing", error instanceof CredentialError ? error.message : "The key could not be stored");
      }
    } else if (def.slug === "nebius") {
      // Reuse the key Holodex already keeps, so the user does not paste it twice.
      const found = await deps.credentials.has([target, HOLODEX_NEBIUS_TARGET]).catch(() => ({}) as Record<string, boolean>);
      if (!found[target] && found[HOLODEX_NEBIUS_TARGET]) {
        const value = await deps.credentials.read(HOLODEX_NEBIUS_TARGET).catch(() => undefined);
        if (value) await deps.credentials.write(target, value);
      }
    }
    const adapter = (await detector.get(def.id, true)) as Adapter;
    if (adapter.status === "connected") {
      return { adapter, action: { kind: "done", message: `${def.name} is connected. ${adapter.statusMessage ?? ""}`.trim() } };
    }
    return {
      adapter,
      action: {
        kind: "enter-token",
        tokenUrl: def.auth.tokenUrl,
        message:
          token && adapter.status === "expired"
            ? `${def.name} rejected that key. Make a new one and paste it here.`
            : `Make a key in your ${def.provider} account, then paste it here. It goes to Windows Credential Manager only.`,
      },
    };
  }

  const adapter = (await detector.get(def.id, true)) as Adapter;
  if (adapter.status === "connected") {
    return { adapter, action: { kind: "done", message: `${def.name} is already signed in${adapter.account ? ` as ${adapter.account}` : ""}.` } };
  }
  if (!def.cli) {
    return { adapter, action: { kind: "open-url", url: def.signUpUrl, message: `Open ${def.provider} to connect.` } };
  }
  if (adapter.status === "needs-cli") {
    return {
      adapter,
      action: { kind: "install-cli", url: def.cli.installUrl, message: `Install the ${def.cli.bin} CLI first: ${def.cli.install}. Then connect again.` },
    };
  }
  if (def.slug === "docker" && adapter.status === "error") {
    return { adapter, action: { kind: "done", message: "Docker Desktop is installed but not running. Start it, then check again." } };
  }
  const [bin, ...args] = def.cli.login.split(" ") as [string, ...string[]];
  try {
    await deps.runner.open(bin, args);
    detector.invalidate();
    return {
      adapter,
      action: {
        kind: "run-cli",
        command: def.cli.login,
        message: `${def.cli.login} is open in its own window. Sign in with your own ${def.provider} account there, then check again here.`,
      },
    };
  } catch {
    return {
      adapter,
      action: { kind: "run-cli", command: def.cli.login, message: `Run ${def.cli.login} in a terminal and sign in, then check again here.` },
    };
  }
}

export async function disconnectAdapter(detector: AdapterDetector, id: string): Promise<Adapter> {
  const def = definition(id);
  if (def.auth.method === "token") {
    await detector.dependencies.credentials.delete(adapterCredentialTarget(def.slug)).catch(() => false);
    const adapter = (await detector.get(def.id, true)) as Adapter;
    const still = adapter.status !== "disconnected";
    return {
      ...adapter,
      statusMessage: still
        ? `4GE forgot its stored key. ${adapter.statusMessage ?? ""} (it is set outside 4GE)`.slice(0, 600)
        : "4GE forgot the stored key.",
    };
  }
  const adapter = (await detector.get(def.id, true)) as Adapter;
  return {
    ...adapter,
    statusMessage: `4GE stores nothing for ${def.name}; your sign-in lives in the ${def.cli?.bin ?? def.provider} CLI. To sign out, run its logout command in a terminal.`,
  };
}

/** Turn an action's command template into argv, checking every parameter. Exported for tests. */
export function buildCommand(
  action: AdapterAction,
  params: Record<string, string>,
): { ok: true; argv: [string, ...string[]] } | { ok: false; message: string } {
  const values: Record<string, string | undefined> = {};
  for (const spec of action.params) {
    const raw = params[spec.name]?.trim();
    if (!raw) {
      if (spec.required) return { ok: false, message: `${spec.label} is needed.` };
      values[spec.name] = undefined;
      continue;
    }
    if (spec.pattern && !new RegExp(spec.pattern).test(raw)) {
      return { ok: false, message: `${spec.label} has characters 4GE will not pass to a CLI.` };
    }
    values[spec.name] = raw;
  }
  const argv: string[] = [];
  for (const token of action.command.split(" ").filter(Boolean)) {
    const whole = /^\{([a-zA-Z0-9]+)\}$/.exec(token);
    if (whole) {
      const value = values[whole[1] as string];
      if (value !== undefined) argv.push(value);
      continue;
    }
    const replaced = token.replace(/\{([a-zA-Z0-9]+)\}/g, (_m, name: string) => values[name] ?? "");
    if (replaced.endsWith("=")) continue;
    argv.push(replaced);
  }
  if (argv.length === 0) return { ok: false, message: "The action has no command." };
  return { ok: true, argv: argv as [string, ...string[]] };
}

export interface RunActionOptions {
  params: Record<string, string>;
  confirm?: boolean;
  /** Folder the command runs in, e.g. a build's app folder. */
  cwd?: string;
}

export async function runAdapterAction(
  detector: AdapterDetector,
  adapterId: string,
  actionId: string,
  options: RunActionOptions,
): Promise<AdapterActionRun> {
  const def = definition(adapterId);
  const action = def.actions.find((candidate) => candidate.id === actionId);
  if (!action) throw new ActionError("not_found", `${def.name} has no action called ${actionId}`);
  const ranAt = new Date().toISOString();
  const base = { adapterId: def.id, actionId: action.id, ranAt };
  const built = buildCommand(action, options.params);
  if (!built.ok) return { ...base, status: "refused", command: action.command, message: built.message };
  const command = built.argv.join(" ");
  if (action.spends && options.confirm !== true) {
    return {
      ...base,
      status: "refused",
      command,
      message: `${action.title} can spend money on your ${def.provider} account. Confirm to go on; ${def.provider} shows the price and asks again.`,
    };
  }
  const deps = detector.dependencies;
  const [bin, ...args] = built.argv;
  if (!deps.runner.which(bin)) {
    return {
      ...base,
      status: "failed",
      command,
      message: `The ${bin} CLI is not installed.${def.cli ? ` Install it with: ${def.cli.install}` : ""}`,
    };
  }
  const env = await adapterEnvironment(deps).catch(() => ({}));
  if (action.interactive) {
    try {
      await deps.runner.open(bin, args, { ...(options.cwd ? { cwd: options.cwd } : {}), env });
      detector.invalidate();
      return { ...base, status: "opened", command, message: `${command} is open in its own window. Answer its questions there.` };
    } catch {
      return { ...base, status: "refused", command, message: `Run ${command} in a terminal; this PC cannot open one for you.` };
    }
  }
  const result = await deps.runner.run(bin, args, { ...(options.cwd ? { cwd: options.cwd } : {}), env, timeoutMs: 120_000 });
  detector.invalidate();
  const output = clip(redactOutput(`${result.stdout}\n${result.stderr}`.trim()));
  const ok = result.exitCode === 0 && !result.timedOut;
  return {
    ...base,
    status: ok ? "done" : "failed",
    command,
    exitCode: result.exitCode,
    ...(output ? { output } : {}),
    message: ok
      ? `${action.title} finished.`
      : result.timedOut
        ? `${action.title} took too long and was stopped.`
        : `${action.title} failed with exit code ${result.exitCode}. The output says why.`,
  };
}
