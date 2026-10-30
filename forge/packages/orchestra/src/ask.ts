import type { ModelTier } from "@4ge/contracts";
import type { z } from "zod";
import { OrchestraError } from "./errors.js";
import type { ChatResult, ChatTurn, ModelClient } from "./model.js";
import { parseJsonAs } from "./parse.js";

export interface AskJsonOptions<T> {
  tier: ModelTier;
  purpose: string;
  system: string;
  messages: ChatTurn[];
  schema: z.ZodType<T>;
  signal?: AbortSignal | undefined;
  maxTokens?: number;
  /** Total tries, counting the first. A failed parse is answered with the problem so the model can fix it. */
  attempts?: number;
  temperature?: number;
}

/** One model call that must come back as JSON matching a schema. */
export async function askJson<T>(models: ModelClient, options: AskJsonOptions<T>): Promise<{ value: T; result: ChatResult }> {
  const attempts = Math.max(1, options.attempts ?? 2);
  const messages: ChatTurn[] = [{ role: "system", content: options.system }, ...options.messages];
  let lastError = "";
  for (let attempt = 1; attempt <= attempts; attempt += 1) {
    const result = await models.chat({
      tier: options.tier,
      purpose: options.purpose,
      messages,
      maxTokens: options.maxTokens ?? 4096,
      temperature: options.temperature ?? 0.3,
      ...(options.signal ? { signal: options.signal } : {}),
    });
    const parsed = parseJsonAs(result.text, options.schema);
    if (parsed.ok) return { value: parsed.value, result };
    lastError = parsed.error;
    messages.push(
      { role: "assistant", content: result.text.slice(0, 12_000) },
      {
        role: "user",
        content: `That answer could not be used: ${parsed.error}. Answer again with only the corrected JSON, no other text.`,
      },
    );
  }
  throw new OrchestraError("model_error", `The model did not return usable data for ${options.purpose}: ${lastError.slice(0, 400)}`);
}
