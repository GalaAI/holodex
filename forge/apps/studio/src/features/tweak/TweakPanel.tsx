import { TWEAK_KINDS, tweakOpSchema, type AppliedTweak, type DesignTarget, type TweakKind } from "@4ge/contracts";
import { ANIMATION_NAMES, MOTION_PRESETS } from "@4ge/kit/design";
import { Badge, Button, CheckboxField, Eyebrow, SelectField, TextArea, TextField } from "@4ge/kit";
import { useEffect, useMemo, useState } from "react";
import { api, useRoute } from "../../lib/api.js";
import { describeError, namesOneElement, useAction } from "./helpers.js";

/**
 * TweakPanel: tune one element of the built app. Each change is a TweakOp
 * the server applies to the build's real source (a managed CSS block, or a
 * text, image or component edit), listed below with Undo.
 */
export interface TweakPanelProps {
  buildId: string;
  /**
   * The element being tuned. The IDE sets it when the person picks an element
   * on an artboard. Leave it undefined to let the panel own its target field.
   */
  target?: DesignTarget | null;
  /** Called when the person edits the target field. */
  onTargetChange?: (target: DesignTarget | null) => void;
  /** Called after a tweak applies or is undone, so the host reloads its preview. */
  onChange?: (tweak: AppliedTweak) => void;
  /** Hide the history list (the IDE may show its own). */
  hideHistory?: boolean;
}

const KIND_LABELS: Record<TweakKind, string> = {
  color: "Color",
  placement: "Placement",
  layout: "Layout",
  text: "Text",
  animation: "Animation",
  motion: "Motion",
  transition: "Transition",
  asset: "Image or asset",
  component: "Component",
};

const SOURCE_KINDS: TweakKind[] = ["text", "component"];

type Fields = Record<string, string>;

const DEFAULTS: Fields = {
  colorProperty: "color",
  colorValue: "",
  tokenName: "",
  x: "",
  y: "",
  align: "",
  order: "",
  display: "",
  direction: "",
  columns: "",
  gap: "",
  padding: "",
  width: "",
  text: "",
  animationName: "fade-up",
  durationMs: "400",
  easing: "ease-out",
  delayMs: "0",
  iterations: "1",
  infinite: "",
  preset: "fade-up",
  intensity: "0.5",
  reducedMotion: "yes",
  transitionProperty: "all",
  transitionMs: "200",
  transitionEasing: "ease",
  assetRef: "",
  alt: "",
  componentAction: "insert",
  componentId: "",
  html: "",
};

const num = (value: string | undefined) => (value === undefined || value.trim() === "" ? undefined : Number(value));
const str = (value: string | undefined) => (value === undefined || value.trim() === "" ? undefined : value.trim());

/** Build the TweakOp for the current form. Exported for tests. */
export function buildTweakOp(kind: TweakKind, buildId: string, target: DesignTarget, f: Fields): unknown {
  const base = { buildId, target, kind };
  switch (kind) {
    case "color":
      return { ...base, property: f.colorProperty, value: f.colorValue, tokenName: str(f.tokenName) };
    case "placement":
      return { ...base, x: num(f.x), y: num(f.y), align: str(f.align), order: num(f.order) };
    case "layout":
      return {
        ...base,
        display: str(f.display),
        direction: str(f.direction),
        columns: num(f.columns),
        gap: str(f.gap),
        padding: str(f.padding),
        width: str(f.width),
      };
    case "text":
      return { ...base, text: f.text ?? "" };
    case "animation":
      return {
        ...base,
        name: f.animationName,
        durationMs: num(f.durationMs),
        easing: str(f.easing),
        delayMs: num(f.delayMs) ?? 0,
        iterations: f.infinite ? "infinite" : (num(f.iterations) ?? 1),
      };
    case "motion":
      return { ...base, preset: f.preset, intensity: num(f.intensity) ?? 0.5, respectReducedMotion: f.reducedMotion === "yes" };
    case "transition":
      return { ...base, property: f.transitionProperty, durationMs: num(f.transitionMs), easing: str(f.transitionEasing) };
    case "asset":
      return { ...base, assetRef: f.assetRef, alt: str(f.alt) };
    case "component":
      return {
        ...base,
        action: f.componentAction,
        componentId: str(f.componentId),
        props: str(f.html) ? { html: f.html } : undefined,
      };
  }
}

function describeTweak(tweak: AppliedTweak): string {
  const op = tweak.op;
  const where = op.target.label ?? op.target.selector;
  switch (op.kind) {
    case "color":
      return `${where}: ${op.property} ${op.value}`;
    case "text":
      return `${where}: text "${op.text.slice(0, 40)}${op.text.length > 40 ? "..." : ""}"`;
    case "asset":
      return `${where}: image ${op.assetRef}`;
    case "component":
      return `${where}: ${op.action} component`;
    case "animation":
      return `${where}: ${op.name} ${op.durationMs}ms`;
    case "motion":
      return `${where}: ${op.preset}`;
    case "transition":
      return `${where}: transition ${op.property}`;
    default:
      return `${where}: ${KIND_LABELS[op.kind].toLowerCase()}`;
  }
}

export function TweakPanel({ buildId, target: controlled, onTargetChange, onChange, hideHistory }: TweakPanelProps) {
  const [ownTarget, setOwnTarget] = useState<DesignTarget | null>(null);
  const target = controlled === undefined ? ownTarget : controlled;
  const [selector, setSelector] = useState(target?.selector ?? "");
  useEffect(() => setSelector(target?.selector ?? ""), [target?.selector]);

  const [kind, setKind] = useState<TweakKind>("color");
  const [fields, setFields] = useState<Fields>(DEFAULTS);
  const set = (name: string) => (value: string) => setFields((previous) => ({ ...previous, [name]: value }));
  const apply = useAction();
  const undo = useAction();
  const history = useRoute("design.tweaks.list", hideHistory ? null : `tweaks:${buildId}`, { query: { buildId } });
  const inventory = useRoute("design.inventory.list", kind === "component" ? `inventory:${buildId}` : null, { query: { buildId } });
  const [notice, setNotice] = useState<string | null>(null);

  const commitSelector = (value: string) => {
    setSelector(value);
    const next = value.trim() ? { selector: value.trim(), page: target?.page ?? "/", label: value.trim() } : null;
    if (controlled === undefined) setOwnTarget(next);
    onTargetChange?.(next);
  };

  const sourceOnly = SOURCE_KINDS.includes(kind);
  const targetProblem = !target
    ? "Pick an element on the artboard or type its selector."
    : sourceOnly && !namesOneElement(target.selector)
      ? `${KIND_LABELS[kind]} tweaks edit the source, so the target must be [data-4ge-id="..."] or #id.`
      : null;

  const draft = useMemo(() => (target ? tweakOpSchema.safeParse(buildTweakOp(kind, buildId, target, fields)) : null), [kind, buildId, target, fields]);
  const formProblem = draft && !draft.success ? (draft.error.issues[0]?.message ?? "Fill in the fields") : null;

  async function submit() {
    if (!draft?.success) return;
    setNotice(null);
    const result = await apply.run(() => api.call("design.tweaks.apply", { body: draft.data }));
    if (result) {
      setNotice(`Applied to ${result.tweak.changedFiles.join(", ")}`);
      history.reload();
      onChange?.(result.tweak);
    }
  }

  async function revert(tweak: AppliedTweak) {
    const result = await undo.run(() => api.call("design.tweaks.revert", { params: { id: tweak.id } }));
    if (result) {
      history.reload();
      onChange?.(result.tweak);
    }
  }

  return (
    <section className="tweak-panel" aria-label="Tweak controls" data-testid="tweak-panel">
      <Eyebrow>TWEAK // {target?.label ?? "NO ELEMENT"}</Eyebrow>
      <TextField
        label="Element"
        mono
        value={selector}
        onValueChange={commitSelector}
        placeholder='[data-4ge-id="hero"]'
        hint="A data-4ge-id or #id names one element. Any CSS selector works for style tweaks."
      />
      <div className="tweak-kinds" role="tablist" aria-label="What to change">
        {TWEAK_KINDS.map((option) => (
          <button
            key={option}
            type="button"
            role="tab"
            aria-selected={kind === option}
            className="tweak-kinds__tab"
            onClick={() => setKind(option)}
          >
            {KIND_LABELS[option]}
          </button>
        ))}
      </div>

      <div className="tweak-form">
        {kind === "color" ? (
          <>
            <SelectField label="Property" value={fields.colorProperty ?? "color"} onValueChange={set("colorProperty")}>
              <option value="color">Text color</option>
              <option value="background">Background</option>
              <option value="border-color">Border</option>
              <option value="fill">Fill</option>
              <option value="stroke">Stroke</option>
              <option value="token">A design token, everywhere</option>
            </SelectField>
            {fields.colorProperty === "token" ? (
              <TextField label="Token name" mono value={fields.tokenName ?? ""} onValueChange={set("tokenName")} placeholder="primary" />
            ) : null}
            <div className="tweak-color">
              <TextField
                label="Color"
                mono
                value={fields.colorValue ?? ""}
                onValueChange={set("colorValue")}
                placeholder="#d4402b or --color-primary"
              />
              <input
                aria-label="Pick a color"
                className="tweak-color__swatch"
                type="color"
                value={/^#[0-9a-f]{6}$/i.test(fields.colorValue ?? "") ? fields.colorValue : "#000000"}
                onChange={(event) => set("colorValue")(event.currentTarget.value)}
              />
            </div>
          </>
        ) : null}

        {kind === "placement" ? (
          <>
            <div className="tweak-row">
              <TextField label="Move right (px)" type="number" value={fields.x ?? ""} onValueChange={set("x")} />
              <TextField label="Move down (px)" type="number" value={fields.y ?? ""} onValueChange={set("y")} />
            </div>
            <div className="tweak-row">
              <SelectField label="Align" value={fields.align ?? ""} onValueChange={set("align")}>
                <option value="">Leave as is</option>
                <option value="start">Start</option>
                <option value="center">Center</option>
                <option value="end">End</option>
                <option value="stretch">Stretch</option>
              </SelectField>
              <TextField label="Order" type="number" value={fields.order ?? ""} onValueChange={set("order")} />
            </div>
          </>
        ) : null}

        {kind === "layout" ? (
          <>
            <div className="tweak-row">
              <SelectField label="Display" value={fields.display ?? ""} onValueChange={set("display")}>
                <option value="">Leave as is</option>
                <option value="block">Block</option>
                <option value="flex">Flex</option>
                <option value="grid">Grid</option>
                <option value="none">Hidden</option>
              </SelectField>
              <SelectField label="Direction" value={fields.direction ?? ""} onValueChange={set("direction")}>
                <option value="">Leave as is</option>
                <option value="row">Row</option>
                <option value="column">Column</option>
              </SelectField>
            </div>
            <div className="tweak-row">
              <TextField label="Columns" type="number" min={1} max={24} value={fields.columns ?? ""} onValueChange={set("columns")} />
              <TextField label="Gap" mono value={fields.gap ?? ""} onValueChange={set("gap")} placeholder="24px" />
            </div>
            <div className="tweak-row">
              <TextField label="Padding" mono value={fields.padding ?? ""} onValueChange={set("padding")} placeholder="32px 16px" />
              <TextField label="Width" mono value={fields.width ?? ""} onValueChange={set("width")} placeholder="100%" />
            </div>
          </>
        ) : null}

        {kind === "text" ? <TextArea label="New text" value={fields.text ?? ""} onValueChange={set("text")} /> : null}

        {kind === "animation" ? (
          <>
            <SelectField label="Animation" value={fields.animationName ?? "fade-up"} onValueChange={set("animationName")}>
              {ANIMATION_NAMES.map((name) => (
                <option key={name} value={name}>
                  {name}
                </option>
              ))}
            </SelectField>
            <div className="tweak-row">
              <TextField label="Duration (ms)" type="number" value={fields.durationMs ?? ""} onValueChange={set("durationMs")} />
              <TextField label="Delay (ms)" type="number" value={fields.delayMs ?? ""} onValueChange={set("delayMs")} />
            </div>
            <div className="tweak-row">
              <TextField label="Easing" mono value={fields.easing ?? ""} onValueChange={set("easing")} />
              <TextField
                label="Repeat"
                type="number"
                min={1}
                value={fields.iterations ?? ""}
                onValueChange={set("iterations")}
                disabled={Boolean(fields.infinite)}
              />
            </div>
            <CheckboxField label="Repeat forever" checked={Boolean(fields.infinite)} onCheckedChange={(on) => set("infinite")(on ? "yes" : "")} />
          </>
        ) : null}

        {kind === "motion" ? (
          <>
            <SelectField label="Motion" value={fields.preset ?? "fade-up"} onValueChange={set("preset")}>
              {MOTION_PRESETS.map((preset) => (
                <option key={preset.key} value={preset.key}>
                  {preset.label}
                </option>
              ))}
            </SelectField>
            <label className="tweak-range">
              <span>Intensity {Math.round(Number(fields.intensity ?? 0.5) * 100)}%</span>
              <input
                type="range"
                min={0}
                max={1}
                step={0.05}
                value={fields.intensity ?? "0.5"}
                onChange={(event) => set("intensity")(event.currentTarget.value)}
              />
            </label>
            <CheckboxField
              label="Stay still for people who ask for reduced motion"
              checked={fields.reducedMotion === "yes"}
              onCheckedChange={(on) => set("reducedMotion")(on ? "yes" : "")}
            />
          </>
        ) : null}

        {kind === "transition" ? (
          <div className="tweak-row">
            <TextField label="Property" mono value={fields.transitionProperty ?? ""} onValueChange={set("transitionProperty")} />
            <TextField label="Duration (ms)" type="number" value={fields.transitionMs ?? ""} onValueChange={set("transitionMs")} />
            <TextField label="Easing" mono value={fields.transitionEasing ?? ""} onValueChange={set("transitionEasing")} />
          </div>
        ) : null}

        {kind === "asset" ? (
          <>
            <TextField
              label="Image or asset"
              mono
              value={fields.assetRef ?? ""}
              onValueChange={set("assetRef")}
              placeholder="public/hero.jpg or https://..."
              hint="A file in the build or a URL. On an img it swaps the source; elsewhere it becomes the background."
            />
            <TextField label="Alt text" value={fields.alt ?? ""} onValueChange={set("alt")} />
          </>
        ) : null}

        {kind === "component" ? (
          <>
            <SelectField label="Action" value={fields.componentAction ?? "insert"} onValueChange={set("componentAction")}>
              <option value="insert">Insert after the element</option>
              <option value="swap">Swap the element</option>
              <option value="remove">Remove the element</option>
            </SelectField>
            {fields.componentAction !== "remove" ? (
              <>
                <SelectField label="From the inventory" value={fields.componentId ?? ""} onValueChange={set("componentId")}>
                  <option value="">None, use markup below</option>
                  {(inventory.data?.items ?? [])
                    .filter((item) => item.status !== "rejected")
                    .map((item) => (
                      <option key={item.id} value={item.id}>
                        {item.name} ({item.source})
                      </option>
                    ))}
                </SelectField>
                <TextArea label="Or markup" value={fields.html ?? ""} onValueChange={set("html")} />
              </>
            ) : null}
          </>
        ) : null}
      </div>

      {targetProblem ? <p className="tweak-note">{targetProblem}</p> : formProblem ? <p className="tweak-note">{formProblem}</p> : null}
      {apply.error ? (
        <p className="tweak-error" role="alert">
          {apply.error}
        </p>
      ) : null}
      {notice ? (
        <p className="tweak-ok" role="status">
          {notice}
        </p>
      ) : null}
      <Button variant="primary" disabled={Boolean(targetProblem) || !draft?.success || apply.busy} onClick={submit}>
        {apply.busy ? "Applying" : "Apply tweak"}
      </Button>

      {hideHistory ? null : (
        <div className="tweak-history">
          <Eyebrow>TWEAKS // {history.data?.tweaks.length ?? 0}</Eyebrow>
          {history.error ? <p className="tweak-error">{describeError(history.error)}</p> : null}
          {undo.error ? (
            <p className="tweak-error" role="alert">
              {undo.error}
            </p>
          ) : null}
          {history.data && history.data.tweaks.length === 0 ? <p className="tweak-note">No tweaks yet.</p> : null}
          <ul className="tweak-history__list">
            {(history.data?.tweaks ?? []).map((tweak) => (
              <li key={tweak.id} className="tweak-history__item" data-status={tweak.status}>
                <span className="tweak-history__what">{describeTweak(tweak)}</span>
                <Badge state={tweak.status === "applied" ? "validated" : tweak.status === "failed" ? "blocked" : undefined}>
                  {tweak.status === "applied" ? "Applied" : tweak.status === "reverted" ? "Undone" : tweak.status}
                </Badge>
                {tweak.status === "applied" ? (
                  <Button variant="default" disabled={undo.busy} onClick={() => revert(tweak)}>
                    Undo
                  </Button>
                ) : null}
              </li>
            ))}
          </ul>
        </div>
      )}
    </section>
  );
}
