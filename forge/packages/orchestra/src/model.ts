import {
  DEFAULT_MODELS,
  MODEL_ENV,
  NEBIUS_BASE_URL,
  type ModelInfo,
  type ModelRoute,
  type ModelTier,
  type ModelUsage,
} from "@4ge/contracts";
import OpenAI, { APIError, APIUserAbortError } from "openai";
import { OrchestraError, cancelled, messageOf } from "./errors.js";
import { createHivemindInvoker, type HivemindInvoker } from "./hivemind.js";
import { stripThinking } from "./parse.js";
import { now } from "./util.js";

/**
 * The model client (SPEC "Architecture", Models). OpenAI-compatible chat
 * completions, routed in this order:
 *
 *   1. the user's Nebius Token Factory key (NEBIUS_API_KEY), through the
 *      official openai SDK pointed at Nebius's base URL;
 *   2. the user's Hivemind model_invoke (HIVEMIND_URL + HIVEMIND_TOKEN),
 *      through the official MCP SDK (see hivemind.ts);
 *   3. none, with plain words saying how to connect a model.
 *
 * Model ids come from the provider's own list at runtime; DEFAULT_MODELS
 * only says which listed id to prefer for each tier. Keys are read from the
 * environment and never logged, returned or written.
 */

export type ChatRole = "system" | "user" | "assistant";

export interface ChatTurn {
  role: ChatRole;
  content: string;
}

export interface ChatRequest {
  tier: ModelTier;
  messages: ChatTurn[];
  /** What the call is for (imagistation.turn, layer, design, ...), for logs and tests. */
  purpose: string;
  maxTokens?: number;
  temperature?: number;
  signal?: AbortSignal;
  /** Called with each piece of visible text as it streams. */
  onDelta?: (text: string) => void;
}

export interface ChatResult {
  text: string;
  model: string;
  usage: ModelUsage;
}

export interface ModelClient {
  /** The active route. Cached; refresh re-reads the provider's model list. */
  route(options?: { refresh?: boolean }): Promise<ModelRoute>;
  /** Models the active provider lists (empty for none). */
  models(): Promise<ModelInfo[]>;
  chat(request: ChatRequest): Promise<ChatResult>;
}

type Env = Readonly<Record<string, string | undefined>>;

const ROUTE_TTL_MS = 10 * 60 * 1000;

/** How long one model call may take before it fails (FORGE_MODEL_TIMEOUT_MS, default four minutes). */
const DEFAULT_MODEL_TIMEOUT_MS = 240_000;
export function modelTimeoutMs(env: Env): number {
  const value = Number(env[MODEL_ENV.timeoutMs]);
  return Number.isInteger(value) && value >= 1000 ? value : DEFAULT_MODEL_TIMEOUT_MS;
}

const NO_MODEL_REASON =
  "No model is connected. Add your Nebius Token Factory key in the Holodex Vault (it reaches 4GE as NEBIUS_API_KEY), or sign in to Hivemind, then restart 4GE.";

const TIER_PATTERNS: Record<ModelTier, RegExp[]> = {
  lead: [/nemotron[-_.]?3[-_.]?super(?!.*vl)/i, /super(?!.*vl)/i, /ultra/i],
  worker: [/nemotron[-_.]?3[-_.]?nano/i, /nano/i, /lightning/i],
  vision: [/vl\b/i, /vision/i],
};

/** Pick the listed id for a tier: the default when listed, else the closest family, else the lead's pick. */
export function pickModels(ids: readonly string[], defaults: Record<ModelTier, string> = DEFAULT_MODELS): Record<ModelTier, string> {
  const lower = new Map(ids.map((id) => [id.toLowerCase(), id]));
  const pick = (tier: ModelTier): string | undefined => {
    const exact = lower.get(defaults[tier].toLowerCase());
    if (exact) return exact;
    for (const pattern of TIER_PATTERNS[tier]) {
      const found = ids.find((id) => pattern.test(id));
      if (found) return found;
    }
    return undefined;
  };
  const lead = pick("lead") ?? ids.find((id) => /nemotron/i.test(id)) ?? ids[0] ?? defaults.lead;
  return {
    lead,
    worker: pick("worker") ?? lead,
    vision: pick("vision") ?? lead,
  };
}

interface Provider {
  route: ModelRoute;
  models: ModelInfo[];
  chat(request: ChatRequest, model: string): Promise<ChatResult>;
}

function noneProvider(reason: string): Provider {
  return {
    route: { provider: "none", models: { ...DEFAULT_MODELS }, reason, checkedAt: now() },
    models: [],
    chat: async () => {
      throw new OrchestraError("model_unavailable", reason, "Connect a model in Holodex, then try again.");
    },
  };
}

function nebiusError(error: unknown, signal?: AbortSignal): OrchestraError {
  if (signal?.aborted || error instanceof APIUserAbortError) return cancelled("The model call");
  if (error instanceof APIError) {
    if (error.status === 401 || error.status === 403) {
      return new OrchestraError(
        "model_unavailable",
        "Nebius Token Factory refused the key. Check the key in the Holodex Vault.",
        "Replace the Nebius key, then restart 4GE.",
      );
    }
    if (error.status === 429) {
      return new OrchestraError("model_error", "Nebius Token Factory is rate limiting this key. Wait a minute and try again.");
    }
    return new OrchestraError("model_error", `Nebius Token Factory answered ${error.status ?? "with an error"}: ${messageOf(error)}`);
  }
  return new OrchestraError("model_error", `The model call failed: ${messageOf(error)}`);
}

async function nebiusProvider(env: Env): Promise<Provider> {
  const apiKey = env[MODEL_ENV.nebiusKey]?.trim() ?? "";
  const baseURL = env[MODEL_ENV.nebiusBaseUrl]?.trim() || NEBIUS_BASE_URL;
  const client = new OpenAI({ apiKey, baseURL, maxRetries: 2, timeout: modelTimeoutMs(env) });
  const models: ModelInfo[] = [];
  for await (const model of client.models.list()) {
    const raw = model as unknown as Record<string, unknown>;
    const contextLength = [raw.context_length, raw.context_window, raw.max_context_length].find(
      (value): value is number => typeof value === "number" && Number.isInteger(value) && value > 0,
    );
    models.push({
      id: model.id,
      ...(typeof model.owned_by === "string" && model.owned_by ? { ownedBy: model.owned_by } : {}),
      ...(contextLength ? { contextLength } : {}),
    });
  }
  if (models.length === 0) throw new Error("Nebius Token Factory listed no models for this key");
  const picked = pickModels(models.map((model) => model.id));
  return {
    route: {
      provider: "nebius",
      baseUrl: baseURL,
      models: picked,
      reason: `Using your Nebius Token Factory key: ${models.length} models listed.`,
      checkedAt: now(),
    },
    models,
    async chat(request, model) {
      const started = Date.now();
      let text = "";
      let usage: OpenAI.CompletionUsage | undefined;
      try {
        const stream = await client.chat.completions.create(
          {
            model,
            messages: request.messages,
            stream: true,
            stream_options: { include_usage: true },
            max_tokens: request.maxTokens ?? 2048,
            temperature: request.temperature ?? 0.4,
          },
          { signal: request.signal ?? null },
        );
        let thinking = false;
        for await (const chunk of stream) {
          if (chunk.usage) usage = chunk.usage;
          let piece = chunk.choices[0]?.delta?.content ?? "";
          if (!piece) continue;
          // Hide inline reasoning from the stream; stripThinking cleans the final text too.
          if (piece.includes("<think>")) thinking = true;
          if (thinking) {
            text += piece;
            if (piece.includes("</think>")) {
              thinking = false;
              piece = piece.slice(piece.indexOf("</think>") + "</think>".length);
              if (piece) request.onDelta?.(piece);
            }
            continue;
          }
          text += piece;
          request.onDelta?.(piece);
        }
      } catch (error) {
        throw nebiusError(error, request.signal);
      }
      return {
        text: stripThinking(text),
        model,
        usage: {
          model,
          ...(usage?.prompt_tokens !== undefined ? { promptTokens: usage.prompt_tokens } : {}),
          ...(usage?.completion_tokens !== undefined ? { completionTokens: usage.completion_tokens } : {}),
          latencyMs: Date.now() - started,
        },
      };
    },
  };
}

async function hivemindProvider(env: Env, invoker: HivemindInvoker): Promise<Provider> {
  const listed = await invoker.listModels();
  const picked = pickModels(listed.ids, {
    lead: "ark/nemotron-3-super",
    worker: "ark/nemotron-3-nano",
    vision: "ark/nemotron-3.5-super-vl",
  });
  return {
    route: {
      provider: "hivemind",
      baseUrl: env[MODEL_ENV.hivemindUrl]?.trim(),
      models: picked,
      reason: listed.fromProvider
        ? `Using your Hivemind model_invoke: ${listed.ids.length} models listed.`
        : "Using your Hivemind model_invoke. Hivemind did not list its models, so 4GE asks for its Nemotron routes.",
      checkedAt: now(),
    },
    models: listed.ids.map((id) => ({ id })),
    async chat(request, model) {
      const started = Date.now();
      if (request.signal?.aborted) throw cancelled("The model call");
      const answer = await invoker.invoke(
        { messages: request.messages, model, maxTokens: request.maxTokens ?? 2048, temperature: request.temperature ?? 0.4 },
        request.signal,
      );
      const text = stripThinking(answer.text);
      request.onDelta?.(text);
      return {
        text,
        model: answer.model || model,
        usage: {
          model: answer.model || model,
          ...(answer.promptTokens !== undefined ? { promptTokens: answer.promptTokens } : {}),
          ...(answer.completionTokens !== undefined ? { completionTokens: answer.completionTokens } : {}),
          latencyMs: Date.now() - started,
        },
      };
    },
  };
}

export interface ModelClientOptions {
  /** Replace the Hivemind invoker (tests). */
  hivemind?: (env: Env) => HivemindInvoker;
}

export function createModelClient(env: Env, options: ModelClientOptions = {}): ModelClient {
  let cached: { provider: Provider; at: number } | undefined;
  let pending: Promise<Provider> | undefined;

  const resolveProvider = async (): Promise<Provider> => {
    const notes: string[] = [];
    if (env[MODEL_ENV.nebiusKey]?.trim()) {
      try {
        return await nebiusProvider(env);
      } catch (error) {
        const failure = nebiusError(error);
        notes.push(failure.message);
      }
    }
    if (env[MODEL_ENV.hivemindUrl]?.trim() && env[MODEL_ENV.hivemindToken]?.trim()) {
      try {
        const invoker = (options.hivemind ?? createHivemindInvoker)(env);
        return await hivemindProvider(env, invoker);
      } catch (error) {
        notes.push(`Hivemind did not answer: ${messageOf(error)}`);
      }
    }
    return noneProvider(notes.length > 0 ? `${notes.join(" ")} ${NO_MODEL_REASON}`.slice(0, 600) : NO_MODEL_REASON);
  };

  const provider = async (refresh = false): Promise<Provider> => {
    if (!refresh && cached && Date.now() - cached.at < ROUTE_TTL_MS) return cached.provider;
    pending ??= resolveProvider().finally(() => {
      pending = undefined;
    });
    const resolved = await pending;
    cached = { provider: resolved, at: Date.now() };
    return resolved;
  };

  return {
    async route(routeOptions = {}) {
      return (await provider(routeOptions.refresh)).route;
    },
    async models() {
      return (await provider()).models;
    },
    async chat(request) {
      const active = await provider();
      const model = active.route.models[request.tier] ?? active.route.models.lead ?? DEFAULT_MODELS.lead;
      try {
        return await active.chat(request, model);
      } catch (error) {
        // A refused key can be replaced while 4GE runs: re-resolve on the next call.
        if (error instanceof OrchestraError && error.code === "model_unavailable") cached = undefined;
        throw error;
      }
    },
  };
}
