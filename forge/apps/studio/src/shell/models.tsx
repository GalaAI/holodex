import type { ModelRoute } from "@4ge/contracts";
import { Led } from "@4ge/kit";
import { createContext, useContext, useEffect, type ReactNode } from "react";
import { useRoute, type RouteState } from "../lib/api.js";

/**
 * The active model route (GET /models), polled every 30 seconds and shared
 * with every feature, so Imagistation and the IDE can say plainly when no
 * model is connected and what to do about it.
 */
type ModelsState = RouteState<{ route: ModelRoute; models: Array<{ id: string }> }>;

const ModelsContext = createContext<ModelsState | undefined>(undefined);

export function ModelsProvider({ children }: { children: ReactNode }) {
  const state = useRoute("models.list", "models");
  const { reload } = state;
  useEffect(() => {
    const timer = setInterval(reload, 30_000);
    return () => clearInterval(timer);
  }, [reload]);
  return <ModelsContext.Provider value={state}>{children}</ModelsContext.Provider>;
}

export function useModels(): ModelsState {
  const value = useContext(ModelsContext);
  if (!value) throw new Error("useModels needs ModelsProvider");
  return value;
}

/** What the studio knows about the model route, in one word plus a reason. */
export type ModelAvailability =
  | { state: "loading" }
  | { state: "connected"; provider: string; lead: string | undefined; reason: string }
  | { state: "none"; reason: string }
  | { state: "unknown"; reason: string };

export function modelAvailability(models: ModelsState): ModelAvailability {
  if (models.data) {
    const { route } = models.data;
    if (route.provider === "none") return { state: "none", reason: route.reason };
    return { state: "connected", provider: route.provider, lead: route.models.lead, reason: route.reason };
  }
  if (models.error) {
    return {
      state: "unknown",
      reason:
        models.error.code === "not_implemented"
          ? "The model route is not connected in this server yet."
          : models.error.code === "network"
            ? "The 4GE server is not answering, so the model route is unknown."
            : models.error.message,
    };
  }
  return { state: "loading" };
}

export function ModelStatus() {
  const availability = modelAvailability(useModels());
  switch (availability.state) {
    case "connected":
      return (
        <Led state="ok" title={availability.reason} data-testid="model-status">
          Model on {availability.provider}
        </Led>
      );
    case "none":
      return (
        <Led state="warn" title={availability.reason} data-testid="model-status">
          No model connected
        </Led>
      );
    case "unknown":
      return (
        <Led state="warn" title={availability.reason} data-testid="model-status">
          Model unknown
        </Led>
      );
    default:
      return (
        <Led state="warn" data-testid="model-status">
          Checking model
        </Led>
      );
  }
}

/**
 * A plain banner for surfaces that need a model: shown only when no model
 * is connected or the route is unknown. Never a blocker for reading.
 */
export function ModelNotice({ agent }: { agent: string }) {
  const availability = modelAvailability(useModels());
  if (availability.state === "connected" || availability.state === "loading") return null;
  return (
    <div className="studio-notice studio-notice--warn" role="status" data-testid="model-notice">
      <strong>{availability.state === "none" ? `${agent} has no model to think with.` : `${agent} may not be able to answer.`}</strong>
      <span>{availability.reason}</span>
    </div>
  );
}
