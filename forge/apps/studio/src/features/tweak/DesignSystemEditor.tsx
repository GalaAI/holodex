import { designSystemSchema, makeId, type DesignSystem, type DesignTokens } from "@4ge/contracts";
import { DESIGN_SYSTEM_PRESETS, isSafeCssValue } from "@4ge/kit/design";
import { Button, Eyebrow, IconButton, SelectField, TextField } from "@4ge/kit";
import { useEffect, useState } from "react";
import { api, useRoute } from "../../lib/api.js";
import { describeError, token, useAction } from "./helpers.js";

/**
 * Choose or make the build's design system. Start from a preset (Phosphor is
 * read from @4ge/kit's own tokens) or a saved system, edit colors, type,
 * spacing, radius and motion, then Save and apply: the server stores it and
 * writes the tokens into the build's managed sheet as CSS custom properties
 * (--color-*, --font-*, --space-*, --radius-*, --motion-*).
 */
export interface DesignSystemEditorProps {
  buildId: string;
  /** Called after a save, so the host reloads its preview. */
  onSaved?: (system: DesignSystem) => void;
}

type Draft = Pick<DesignSystem, "name" | "source" | "tokens" | "principles"> & { id?: string };

const clone = (tokens: DesignTokens): DesignTokens => JSON.parse(JSON.stringify(tokens)) as DesignTokens;

function fromPreset(key: string): Draft {
  const preset = DESIGN_SYSTEM_PRESETS.find((candidate) => candidate.key === key) ?? DESIGN_SYSTEM_PRESETS[0]!;
  return { name: preset.name, source: "kit", tokens: clone(preset.tokens), principles: [...preset.principles] };
}

type ListKey = "spacing" | "radii";

export function DesignSystemEditor({ buildId, onSaved }: DesignSystemEditorProps) {
  const saved = useRoute("design.systems.list", `systems:${buildId}`, { query: { buildId } });
  const [startFrom, setStartFrom] = useState("");
  const [draft, setDraft] = useState<Draft | null>(null);
  const action = useAction();
  const [notice, setNotice] = useState<string | null>(null);

  // Open the applied system (the newest saved) once it loads; else wait for a choice.
  useEffect(() => {
    if (draft || !saved.data) return;
    const current = saved.data.systems[0];
    if (current) {
      setDraft({ id: current.id, name: current.name, source: current.source, tokens: clone(current.tokens), principles: current.principles });
      setStartFrom(`saved:${current.id}`);
    }
  }, [saved.data, draft]);

  function choose(value: string) {
    setStartFrom(value);
    setNotice(null);
    if (value.startsWith("preset:")) setDraft(fromPreset(value.slice("preset:".length)));
    else if (value.startsWith("saved:")) {
      const system = saved.data?.systems.find((candidate) => `saved:${candidate.id}` === value);
      if (system) setDraft({ id: system.id, name: system.name, source: system.source, tokens: clone(system.tokens), principles: system.principles });
    }
  }

  const edit = (change: (tokens: DesignTokens) => void) =>
    setDraft((previous) => {
      if (!previous) return previous;
      const tokens = clone(previous.tokens);
      change(tokens);
      return { ...previous, tokens, source: previous.source === "kit" ? "generated" : previous.source };
    });

  const unsafe = draft
    ? [
        ...draft.tokens.colors.map((item) => item.value),
        ...draft.tokens.typography.flatMap((item) => [item.family, item.size]),
        ...draft.tokens.spacing.map((item) => item.value),
        ...draft.tokens.radii.map((item) => item.value),
        ...draft.tokens.motion.map((item) => item.easing),
      ].find((value) => !isSafeCssValue(value.trim()))
    : undefined;

  async function save() {
    if (!draft) return;
    const id = draft.id ?? makeId("designSystem", token());
    const parsed = designSystemSchema.safeParse({ ...draft, id, buildId, updatedAt: new Date().toISOString() });
    if (!parsed.success) {
      action.setError(parsed.error.issues[0]?.message ?? "The design system is not complete");
      return;
    }
    const result = await action.run(() => api.call("design.systems.save", { params: { id }, body: parsed.data }));
    if (result) {
      setDraft({ ...draft, id: result.system.id });
      setStartFrom(`saved:${result.system.id}`);
      setNotice(`Saved and applied "${result.system.name}" to the build.`);
      saved.reload();
      onSaved?.(result.system);
    }
  }

  const listRows = (key: ListKey, label: string) =>
    draft ? (
      <fieldset className="ds-group">
        <legend>{label}</legend>
        {draft.tokens[key].map((item, index) => (
          <div className="ds-row ds-row--pair" key={`${key}-${index}`}>
            <TextField label="Name" mono value={item.name} onValueChange={(value) => edit((t) => void (t[key][index]!.name = value))} />
            <TextField label="Value" mono value={item.value} onValueChange={(value) => edit((t) => void (t[key][index]!.value = value))} />
            <IconButton icon="trash" label={`Remove ${item.name}`} onClick={() => edit((t) => void t[key].splice(index, 1))} />
          </div>
        ))}
        <Button variant="default" onClick={() => edit((t) => void t[key].push({ name: `${t[key].length + 1}`, value: "8px" }))}>
          Add
        </Button>
      </fieldset>
    ) : null;

  return (
    <section className="ds-editor" aria-label="Design system" data-testid="design-system-editor">
      <Eyebrow>DESIGN SYSTEM // {draft?.name ?? "CHOOSE ONE"}</Eyebrow>
      <SelectField label="Start from" value={startFrom} onValueChange={choose}>
        <option value="">Choose a system</option>
        <optgroup label="Saved for this build">
          {(saved.data?.systems ?? []).map((system) => (
            <option key={system.id} value={`saved:${system.id}`}>
              {system.name}
            </option>
          ))}
        </optgroup>
        <optgroup label="Start fresh">
          {DESIGN_SYSTEM_PRESETS.map((preset) => (
            <option key={preset.key} value={`preset:${preset.key}`}>
              {preset.name}
            </option>
          ))}
        </optgroup>
      </SelectField>
      {saved.error ? <p className="tweak-error">{describeError(saved.error)}</p> : null}

      {draft ? (
        <>
          <TextField label="Name" value={draft.name} onValueChange={(name) => setDraft({ ...draft, name })} />
          <div className="ds-swatches" aria-hidden="true">
            {draft.tokens.colors.map((color) => (
              <span key={color.name} className="ds-swatch" style={{ background: color.value }} title={`${color.name} ${color.value}`} />
            ))}
          </div>

          <fieldset className="ds-group">
            <legend>Colors</legend>
            {draft.tokens.colors.map((color, index) => (
              <div className="ds-row ds-row--color" key={`color-${index}`}>
                <TextField label="Name" mono value={color.name} onValueChange={(value) => edit((t) => void (t.colors[index]!.name = value))} />
                <TextField label="Value" mono value={color.value} onValueChange={(value) => edit((t) => void (t.colors[index]!.value = value))} />
                <input
                  aria-label={`Pick ${color.name}`}
                  className="tweak-color__swatch"
                  type="color"
                  value={/^#[0-9a-f]{6}$/i.test(color.value) ? color.value : "#000000"}
                  onChange={(event) => {
                    const value = event.currentTarget.value;
                    edit((t) => void (t.colors[index]!.value = value));
                  }}
                />
                <IconButton icon="trash" label={`Remove ${color.name}`} onClick={() => edit((t) => void t.colors.splice(index, 1))} />
              </div>
            ))}
            <Button variant="default" onClick={() => edit((t) => void t.colors.push({ name: `color-${t.colors.length + 1}`, value: "#888888", role: "other" }))}>
              Add color
            </Button>
          </fieldset>

          <fieldset className="ds-group">
            <legend>Type</legend>
            {draft.tokens.typography.map((type, index) => (
              <div className="ds-row" key={`type-${index}`}>
                <TextField label="Name" mono value={type.name} onValueChange={(value) => edit((t) => void (t.typography[index]!.name = value))} />
                <TextField label="Family" value={type.family} onValueChange={(value) => edit((t) => void (t.typography[index]!.family = value))} />
                <TextField label="Size" mono value={type.size} onValueChange={(value) => edit((t) => void (t.typography[index]!.size = value))} />
                <TextField
                  label="Weight"
                  type="number"
                  value={String(type.weight)}
                  onValueChange={(value) => edit((t) => void (t.typography[index]!.weight = Number(value) || 400))}
                />
              </div>
            ))}
            <Button
              variant="default"
              onClick={() => edit((t) => void t.typography.push({ name: `text-${t.typography.length + 1}`, family: "system-ui, sans-serif", size: "16px", weight: 400 }))}
            >
              Add type style
            </Button>
          </fieldset>

          {listRows("spacing", "Spacing")}
          {listRows("radii", "Radius")}

          <fieldset className="ds-group">
            <legend>Motion</legend>
            {draft.tokens.motion.map((motion, index) => (
              <div className="ds-row" key={`motion-${index}`}>
                <TextField label="Name" mono value={motion.name} onValueChange={(value) => edit((t) => void (t.motion[index]!.name = value))} />
                <TextField
                  label="Duration (ms)"
                  type="number"
                  value={String(motion.durationMs)}
                  onValueChange={(value) => edit((t) => void (t.motion[index]!.durationMs = Math.max(0, Number(value) || 0)))}
                />
                <TextField label="Easing" mono value={motion.easing} onValueChange={(value) => edit((t) => void (t.motion[index]!.easing = value))} />
              </div>
            ))}
            <Button variant="default" onClick={() => edit((t) => void t.motion.push({ name: `motion-${t.motion.length + 1}`, durationMs: 200, easing: "ease-out" }))}>
              Add motion
            </Button>
          </fieldset>

          {unsafe !== undefined ? <p className="tweak-note">"{unsafe}" cannot go into CSS. Remove ; {"{"} {"}"} and comment marks.</p> : null}
          {action.error ? (
            <p className="tweak-error" role="alert">
              {action.error}
            </p>
          ) : null}
          {notice ? (
            <p className="tweak-ok" role="status">
              {notice}
            </p>
          ) : null}
          <Button variant="primary" disabled={action.busy || unsafe !== undefined || !draft.name.trim()} onClick={save}>
            {action.busy ? "Saving" : "Save and apply"}
          </Button>
        </>
      ) : (
        <p className="tweak-note">Pick a saved system or start fresh from one of the presets.</p>
      )}
    </section>
  );
}
