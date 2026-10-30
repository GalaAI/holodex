import { makeId, type ComponentCandidate, type ComponentInventoryItem } from "@4ge/contracts";
import { Badge, Button, Eyebrow, Led, TextField } from "@4ge/kit";
import { useEffect, useState } from "react";
import { api, useRoute } from "../../lib/api.js";
import { describeError, token, useAction } from "./helpers.js";

/**
 * The build's component inventory and the picker that fills it. Candidates
 * come from @4ge/kit and the 21st registry; each shows where it came from
 * (kit, 21st catalog snapshot with its date, or a live 21st search) and adding
 * one records that source on the inventory item.
 */
export interface InventoryPickerProps {
  buildId: string;
  /** Called when the inventory changes. */
  onChange?: (item: ComponentInventoryItem) => void;
}

const VIA_LABEL: Record<ComponentCandidate["via"], string> = {
  kit: "4GE kit",
  "21st-snapshot": "21st catalog",
  "21st-live": "21st live",
};

function useDebounced(value: string, ms: number): string {
  const [settled, setSettled] = useState(value);
  useEffect(() => {
    const timer = setTimeout(() => setSettled(value), ms);
    return () => clearTimeout(timer);
  }, [value, ms]);
  return settled;
}

export function InventoryPicker({ buildId, onChange }: InventoryPickerProps) {
  const inventory = useRoute("design.inventory.list", `inventory:${buildId}`, { query: { buildId } });
  const [query, setQuery] = useState("");
  const q = useDebounced(query.trim(), 350);
  const candidates = useRoute("design.inventory.candidates", `candidates:${buildId}:${q}`, { query: q ? { buildId, q } : { buildId } });
  const action = useAction();

  const items = inventory.data?.items ?? [];
  const added = new Set(items.map((item) => `${item.source}:${item.sourceRef ?? ""}`));

  async function save(item: ComponentInventoryItem) {
    const result = await action.run(() => api.call("design.inventory.save", { params: { id: item.id }, body: item }));
    if (result) {
      inventory.reload();
      onChange?.(result.item);
    }
  }

  function add(candidate: ComponentCandidate) {
    const note = candidate.via === "21st-snapshot" && candidate.fetchedAt ? ` (21st catalog, ${candidate.fetchedAt.slice(0, 10)})` : "";
    return save({
      id: makeId("component", token()),
      buildId,
      name: candidate.name,
      category: candidate.category,
      status: "proposed",
      description: `${candidate.description || candidate.name}${candidate.sourceUrl ? ` Source: ${candidate.sourceUrl}` : ""}${note}`.slice(0, 1200),
      source: candidate.source,
      sourceRef: candidate.sourceRef,
      usedIn: [],
      updatedAt: new Date().toISOString(),
    });
  }

  return (
    <section className="inventory" aria-label="Component inventory" data-testid="inventory-picker">
      <Eyebrow>INVENTORY // {items.length} COMPONENTS</Eyebrow>
      {inventory.error ? <p className="tweak-error">{describeError(inventory.error)}</p> : null}
      {inventory.data && items.length === 0 ? <p className="tweak-note">No components chosen yet. Pick from the candidates below.</p> : null}
      <ul className="inventory__list">
        {items.map((item) => (
          <li key={item.id} className="inventory__item" data-status={item.status}>
            <div>
              <strong>{item.name}</strong>
              <span className="inventory__meta">
                {item.category} // {item.source}
                {item.sourceRef ? ` // ${item.sourceRef}` : ""}
              </span>
            </div>
            <Badge state={item.status === "built" ? "proven" : item.status === "accepted" ? "validated" : item.status === "rejected" ? "blocked" : undefined}>
              {item.status}
            </Badge>
            {item.status === "proposed" || item.status === "rejected" ? (
              <Button variant="default" disabled={action.busy} onClick={() => save({ ...item, status: "accepted" })}>
                Accept
              </Button>
            ) : null}
            {item.status !== "rejected" && item.status !== "built" ? (
              <Button variant="default" disabled={action.busy} onClick={() => save({ ...item, status: "rejected" })}>
                Reject
              </Button>
            ) : null}
          </li>
        ))}
      </ul>

      <Eyebrow>CANDIDATES // KIT AND 21ST</Eyebrow>
      <TextField label="What do you need?" value={query} onValueChange={setQuery} placeholder="booking calendar, gallery, pricing" />
      {candidates.data ? (
        <Led state={candidates.data.registry.live ? "live" : "warn"} title={candidates.data.registry.message}>
          {candidates.data.registry.message}
        </Led>
      ) : null}
      {candidates.error ? <p className="tweak-error">{describeError(candidates.error)}</p> : null}
      {action.error ? (
        <p className="tweak-error" role="alert">
          {action.error}
        </p>
      ) : null}
      {candidates.data && candidates.data.candidates.length === 0 ? <p className="tweak-note">Nothing matches. Try other words.</p> : null}
      <ul className="inventory__candidates">
        {(candidates.data?.candidates ?? []).slice(0, 40).map((candidate) => {
          const isAdded = added.has(`${candidate.source}:${candidate.sourceRef}`);
          return (
            <li key={candidate.key} className="inventory__candidate">
              {candidate.previewUrl ? <img src={candidate.previewUrl} alt="" loading="lazy" className="inventory__preview" /> : null}
              <div className="inventory__body">
                <strong>{candidate.name}</strong>
                <span className="inventory__meta">
                  {VIA_LABEL[candidate.via]} // {candidate.category}
                  {candidate.author ? ` // ${candidate.author}` : ""}
                </span>
                {candidate.description ? <p>{candidate.description}</p> : null}
                {candidate.sourceUrl ? (
                  <a href={candidate.sourceUrl} target="_blank" rel="noreferrer" className="inventory__source">
                    {candidate.sourceUrl.replace(/^https:\/\//, "")}
                  </a>
                ) : (
                  <span className="inventory__source">@4ge/kit {candidate.sourceRef}</span>
                )}
              </div>
              <Button variant="default" disabled={isAdded || action.busy} onClick={() => add(candidate)}>
                {isAdded ? "Added" : "Add"}
              </Button>
            </li>
          );
        })}
      </ul>
    </section>
  );
}
