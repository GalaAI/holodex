import { Eyebrow, Led, Panel } from "@4ge/kit";
import { FEATURES, type FeatureDef } from "../features.js";
import { useHealth } from "./ServerStatus.js";

/**
 * What a feature folder renders until its builder lands: the feature's name,
 * the plain fact that it is being built, and whether the server layers it
 * needs answer yet. No mock data.
 */
export function FeaturePending({ id }: { id: FeatureDef["id"] }) {
  const feature = FEATURES.find((candidate) => candidate.id === id);
  const { data } = useHealth();
  if (!feature) return null;
  return (
    <section className="studio-pending" aria-labelledby={`pending-${id}`} data-testid={`feature-${id}`}>
      <Panel className="studio-pending__panel">
        <Eyebrow>{feature.eyebrow}</Eyebrow>
        <h1 id={`pending-${id}`} className="studio-pending__title">
          {feature.label}
        </h1>
        <p className="studio-pending__body">{feature.label} is being built. This part of 4GE is not connected yet.</p>
        <ul className="studio-pending__layers" aria-label="Server layers">
          {feature.layers.map((layer) => {
            const connected = data?.layers[layer] === true;
            return (
              <li key={layer}>
                <Led state={connected ? "ok" : "warn"}>
                  {layer} {connected ? "connected" : "not connected"}
                </Led>
              </li>
            );
          })}
        </ul>
      </Panel>
    </section>
  );
}
