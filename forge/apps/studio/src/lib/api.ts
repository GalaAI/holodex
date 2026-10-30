import {
  ForgeApiError,
  createForgeClient,
  type CallRest,
  type JsonRouteName,
  type RouteResponse,
} from "@4ge/contracts";
import { useCallback, useEffect, useRef, useState } from "react";

/**
 * The studio's API client, generated from the @4ge/contracts route table.
 * Same origin in production (the server serves the studio) and through the
 * Vite proxy in development, so the base URL is empty unless
 * VITE_FORGE_SERVER points somewhere else.
 */
export const api = createForgeClient({ baseUrl: import.meta.env.VITE_FORGE_SERVER ?? "" });

export { ForgeApiError };

export interface RouteState<T> {
  data: T | undefined;
  error: ForgeApiError | undefined;
  loading: boolean;
  reload: () => void;
}

/**
 * Load one JSON route and keep it fresh. `key` controls refetching: pass a
 * string built from the arguments (or null to skip the call).
 */
export function useRoute<N extends JsonRouteName>(
  name: N,
  key: string | null,
  ...rest: CallRest<N>
): RouteState<RouteResponse<N>> {
  const [data, setData] = useState<RouteResponse<N>>();
  const [error, setError] = useState<ForgeApiError>();
  const [loading, setLoading] = useState(key !== null);
  const [tick, setTick] = useState(0);
  const args = useRef(rest);
  args.current = rest;

  useEffect(() => {
    if (key === null) {
      setLoading(false);
      return;
    }
    const controller = new AbortController();
    setLoading(true);
    const callArgs = { ...(args.current[0] ?? {}), signal: controller.signal } as unknown as CallRest<N>[0];
    (api.call as (name: N, args: unknown) => Promise<RouteResponse<N>>)(name, callArgs)
      .then((result) => {
        setData(result);
        setError(undefined);
      })
      .catch((failure: unknown) => {
        if (controller.signal.aborted) return;
        setError(
          failure instanceof ForgeApiError
            ? failure
            : new ForgeApiError({
                status: 0,
                code: "network",
                message: failure instanceof Error ? failure.message : String(failure),
                route: name,
              }),
        );
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [name, key, tick]);

  const reload = useCallback(() => setTick((value) => value + 1), []);
  return { data, error, loading, reload };
}
