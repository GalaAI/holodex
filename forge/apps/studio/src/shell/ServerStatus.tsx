import type { Health } from "@4ge/contracts";
import { Led } from "@4ge/kit";
import { createContext, useContext, useEffect, type ReactNode } from "react";
import { useRoute, type RouteState } from "../lib/api.js";

/**
 * Server health, polled every 10 seconds and shared with every feature, so
 * a feature can say plainly when its layer is not connected.
 */
const HealthContext = createContext<RouteState<Health> | undefined>(undefined);

export function HealthProvider({ children }: { children: ReactNode }) {
  const state = useRoute("health", "health");
  const { reload } = state;
  useEffect(() => {
    const timer = setInterval(reload, 10_000);
    return () => clearInterval(timer);
  }, [reload]);
  return <HealthContext.Provider value={state}>{children}</HealthContext.Provider>;
}

export function useHealth(): RouteState<Health> {
  const value = useContext(HealthContext);
  if (!value) throw new Error("useHealth needs HealthProvider");
  return value;
}

export function ServerStatus() {
  const { data, error, loading } = useHealth();
  if (error) {
    return (
      <Led state="err" title={error.hint ?? error.message} data-testid="server-status">
        Server offline
      </Led>
    );
  }
  if (!data) {
    return (
      <Led state="warn" data-testid="server-status">
        {loading ? "Connecting" : "No answer"}
      </Led>
    );
  }
  return (
    <Led state="live" title={`4GE ${data.version} on port ${data.ports.server}`} data-testid="server-status">
      Server live
    </Led>
  );
}
