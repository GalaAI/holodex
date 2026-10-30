import { z } from "zod";
import { isoDateTimeSchema } from "./common.js";
import { modelTierSchema } from "./agents.js";

/**
 * Model routing (SPEC "Architecture", Models). Route order: the user's
 * Nebius Token Factory key, then the user's Hivemind model_invoke, then
 * none. Keys are read from the environment by the server; no contract ever
 * carries one.
 */
export const NEBIUS_BASE_URL = "https://api.tokenfactory.nebius.com/v1";

export const MODEL_ENV = {
  nebiusKey: "NEBIUS_API_KEY",
  nebiusBaseUrl: "NEBIUS_BASE_URL",
  hivemindUrl: "HIVEMIND_URL",
  hivemindToken: "HIVEMIND_TOKEN",
  /** Per-call model timeout in milliseconds (default 240000); a hung provider fails after it instead of spinning. */
  timeoutMs: "FORGE_MODEL_TIMEOUT_MS",
} as const;

/** Defaults by tier. Confirm against the provider's live /models list. */
export const DEFAULT_MODELS = {
  lead: "nvidia/nemotron-3-super-120b-a12b",
  worker: "nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B",
  vision: "nvidia/Nemotron-3.5-Super-VL",
} as const;

export const modelProviderSchema = z.enum(["nebius", "hivemind", "none"]);
export type ModelProvider = z.infer<typeof modelProviderSchema>;

export const modelInfoSchema = z.object({
  id: z.string().min(1).max(200),
  ownedBy: z.string().max(200).optional(),
  /** Context window in tokens when the provider reports it. */
  contextLength: z.number().int().positive().optional(),
});
export type ModelInfo = z.infer<typeof modelInfoSchema>;

export const modelRouteSchema = z.object({
  provider: modelProviderSchema,
  /** OpenAI-compatible base URL; absent for none. Never includes a key. */
  baseUrl: z.string().url().optional(),
  /** Model id per tier after checking the provider's list. */
  models: z.record(modelTierSchema, z.string().min(1).max(200)),
  /** Plain words: why this route, or what to do when it is none. */
  reason: z.string().min(1).max(600),
  checkedAt: isoDateTimeSchema,
});
export type ModelRoute = z.infer<typeof modelRouteSchema>;

/** Usage the server records per model call, for receipts and the UI. */
export const modelUsageSchema = z.object({
  model: z.string(),
  promptTokens: z.number().int().nonnegative().optional(),
  completionTokens: z.number().int().nonnegative().optional(),
  latencyMs: z.number().nonnegative().optional(),
});
export type ModelUsage = z.infer<typeof modelUsageSchema>;
