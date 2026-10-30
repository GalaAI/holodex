import { MODEL_ENV } from "@4ge/contracts";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { OrchestraError, cancelled, messageOf } from "./errors.js";

/**
 * The user's Hivemind as a model route: its MCP tool model_invoke (scope
 * model.invoke), reached with the official MCP TypeScript SDK over
 * streamable HTTP and the user's bearer token.
 *
 * The call shape and limits are copied from Holodex's HivemindClient.cs
 * (InvokeModelAsync, ModelMessages, ReadModelInvocation): at most 16
 * messages of at most 8000 characters each, maxTokens 1 to 4096,
 * stream false, and a result envelope { ok, data: <chat completion> } or
 * { ok: false, error, code }. Text only; Hivemind's model_invoke carries no
 * tools.
 */

export interface HivemindInvokeRequest {
  messages: Array<{ role: string; content: string }>;
  model: string;
  maxTokens: number;
  temperature: number;
}

export interface HivemindAnswer {
  text: string;
  model: string;
  promptTokens?: number;
  completionTokens?: number;
}

export interface HivemindInvoker {
  listModels(): Promise<{ ids: string[]; fromProvider: boolean }>;
  invoke(request: HivemindInvokeRequest, signal?: AbortSignal): Promise<HivemindAnswer>;
}

type Env = Readonly<Record<string, string | undefined>>;

const MAX_MESSAGES = 16;
const MAX_CHARS = 8000;

/** HivemindClient.ModelMessages: keep the system message and the latest turns, clip each. */
export function hivemindMessages(messages: ReadonlyArray<{ role: string; content: string }>): Array<{ role: string; content: string }> {
  let kept = messages.filter((message) => message.content.trim().length > 0);
  if (kept.length > MAX_MESSAGES) {
    const system = kept.filter((message) => message.role === "system").slice(0, 1);
    kept = [...system, ...kept.filter((message) => message.role !== "system").slice(-(MAX_MESSAGES - system.length))];
  }
  return kept.map((message) => {
    let content = message.content.trim();
    if (content.length > MAX_CHARS) content = `${content.slice(0, MAX_CHARS - 3)}...`;
    const role = message.role === "system" || message.role === "assistant" ? message.role : "user";
    return { role, content };
  });
}

function endpoint(raw: string): URL {
  const url = new URL(raw);
  if (url.pathname === "/" || url.pathname === "") url.pathname = "/mcp";
  return url;
}

function textOf(result: unknown): { text: string; isError: boolean } {
  const record = (result ?? {}) as { content?: Array<{ type?: string; text?: string }>; isError?: boolean };
  const text = (record.content ?? [])
    .filter((part) => part.type === "text" && typeof part.text === "string")
    .map((part) => part.text as string)
    .join("");
  return { text, isError: record.isError === true };
}

/** HivemindClient.ReadModelInvocation, in TypeScript. */
export function readModelInvocation(text: string, isError: boolean, requestedModel: string): HivemindAnswer {
  let envelope: Record<string, unknown> = {};
  try {
    envelope = JSON.parse(text) as Record<string, unknown>;
  } catch {
    if (isError) throw new OrchestraError("model_error", `Hivemind model_invoke failed: ${messageOf(text)}`);
    return { text, model: requestedModel };
  }
  if (isError || envelope.ok === false) {
    const code = String(envelope.code ?? "");
    const error = String(envelope.error ?? envelope.message ?? "model_invoke failed");
    if (code === "scope-denied" || /model\.invoke/i.test(error)) {
      throw new OrchestraError(
        "model_unavailable",
        "Your Hivemind sign-in is missing the model.invoke scope.",
        "Sign in to Hivemind again and allow model answers.",
      );
    }
    throw new OrchestraError("model_error", `Hivemind model_invoke failed: ${messageOf(error)}`);
  }
  const data = (envelope.data ?? envelope) as {
    model?: string;
    choices?: Array<{ message?: { content?: string } }>;
    usage?: { prompt_tokens?: number; completion_tokens?: number };
  };
  const content = data.choices?.[0]?.message?.content ?? "";
  return {
    text: content,
    model: data.model ?? requestedModel,
    ...(typeof data.usage?.prompt_tokens === "number" ? { promptTokens: data.usage.prompt_tokens } : {}),
    ...(typeof data.usage?.completion_tokens === "number" ? { completionTokens: data.usage.completion_tokens } : {}),
  };
}

/** Pull model ids out of whatever shape ark_models answers with. */
export function modelIdsFrom(value: unknown): string[] {
  const ids = new Set<string>();
  const visit = (node: unknown, depth: number) => {
    if (depth > 5 || node === null || node === undefined) return;
    if (Array.isArray(node)) {
      for (const item of node) {
        if (typeof item === "string" && /[a-z]/i.test(item) && item.length < 200) ids.add(item);
        else visit(item, depth + 1);
      }
      return;
    }
    if (typeof node === "object") {
      const record = node as Record<string, unknown>;
      for (const key of ["id", "model", "alias", "route"]) {
        const candidate = record[key];
        if (typeof candidate === "string" && candidate.length < 200 && /[/:-]/.test(candidate)) ids.add(candidate);
      }
      for (const child of Object.values(record)) if (typeof child === "object") visit(child, depth + 1);
    }
  };
  visit(value, 0);
  return [...ids];
}

export function createHivemindInvoker(env: Env): HivemindInvoker {
  const url = env[MODEL_ENV.hivemindUrl]?.trim() ?? "";
  const token = env[MODEL_ENV.hivemindToken]?.trim() ?? "";
  let connecting: Promise<Client> | undefined;

  const connect = () => {
    connecting ??= (async () => {
      const client = new Client({ name: "4ge-orchestra", version: "0.1.0" });
      const transport = new StreamableHTTPClientTransport(endpoint(url), {
        requestInit: { headers: { Authorization: `Bearer ${token}` } },
      });
      await client.connect(transport);
      return client;
    })().catch((error: unknown) => {
      connecting = undefined;
      throw error;
    });
    return connecting;
  };

  return {
    async listModels() {
      const client = await connect();
      const tools = await client.listTools();
      if (!tools.tools.some((tool) => tool.name === "model_invoke")) {
        throw new Error("this Hivemind offers no model_invoke tool");
      }
      if (tools.tools.some((tool) => tool.name === "ark_models")) {
        try {
          const listed = textOf(await client.callTool({ name: "ark_models", arguments: {} }));
          if (!listed.isError) {
            const ids = modelIdsFrom(JSON.parse(listed.text));
            if (ids.length > 0) return { ids, fromProvider: true };
          }
        } catch {
          // Fall through to the documented routes.
        }
      }
      return { ids: ["ark/nemotron-3-super", "ark/nemotron-3-nano", "ark/nemotron-3.5-super-vl"], fromProvider: false };
    },
    async invoke(request, signal) {
      const client = await connect();
      try {
        const result = await client.callTool(
          {
            name: "model_invoke",
            arguments: {
              messages: hivemindMessages(request.messages),
              model: request.model,
              maxTokens: Math.min(4096, Math.max(1, request.maxTokens)),
              temperature: request.temperature,
              stream: false,
            },
          },
          undefined,
          { ...(signal ? { signal } : {}), timeout: 240_000 },
        );
        const { text, isError } = textOf(result);
        return readModelInvocation(text, isError, request.model);
      } catch (error) {
        if (signal?.aborted) throw cancelled("The model call");
        if (error instanceof OrchestraError) throw error;
        throw new OrchestraError("model_error", `Hivemind did not answer: ${messageOf(error)}`);
      }
    },
  };
}
