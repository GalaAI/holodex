import { buildIdSchema } from "@4ge/contracts";
import { Button, Eyebrow, SelectField, TextField } from "@4ge/kit";
import { useRef, useState } from "react";
import { useNavigate, useParams } from "react-router";
import { useRoute } from "../../lib/api.js";
import { AnnotationLayer } from "./AnnotationLayer.js";
import { DesignSystemEditor } from "./DesignSystemEditor.js";
import { describeError, targetFromIframe } from "./helpers.js";
import { InventoryPicker } from "./InventoryPicker.js";
import { TweakPanel } from "./TweakPanel.js";
import "./tweak.css";

/**
 * features/tweak: the design layer of a build.
 *
 * What the IDE mounts (import from "../tweak/index.js"):
 *
 *   <TweakPanel buildId target onTargetChange onChange hideHistory />
 *     Tweak controls for one element: color, placement, layout, text,
 *     animation, motion, transition, asset, component. Each applies a
 *     TweakOp to the build's real source through POST /design/tweaks and
 *     lists applied tweaks with Undo. onChange fires after apply or undo with
 *     the AppliedTweak (changedFiles says what moved) so the IDE reloads its
 *     artboard.
 *
 *   <AnnotationLayer buildId page pinning onPinningChange resolveTarget
 *                    smithSessionId showResolved onSelect />
 *     Overlay for one artboard (fills its positioned parent). Pins are
 *     fractions of the artboard. smithSessionId is Build.rootSessionId:
 *     Send to Smith posts the thread to Smith and records Smith's reply.
 *     resolveTarget maps a click to the element under it; for a same-origin
 *     iframe pass (x, y) => targetFromIframe(frame, x, y, page).
 *
 *   <DesignSystemEditor buildId onSaved />   choose or make the design system
 *   <InventoryPicker buildId onChange />      kit and 21st component candidates
 *
 *   targetFromIframe, targetForElement, smithBrief, buildTweakOp: helpers.
 *
 * The default export is the standalone Tweak page at /tweak/:buildId.
 */
export { TweakPanel, buildTweakOp, type TweakPanelProps } from "./TweakPanel.js";
export { AnnotationLayer, smithBrief, type AnnotationLayerProps } from "./AnnotationLayer.js";
export { DesignSystemEditor, type DesignSystemEditorProps } from "./DesignSystemEditor.js";
export { InventoryPicker, type InventoryPickerProps } from "./InventoryPicker.js";
export { targetForElement, targetFromIframe, namesOneElement } from "./helpers.js";

type Side = "system" | "inventory";

export default function TweakFeature() {
  const { buildId } = useParams();
  const navigate = useNavigate();
  const valid = buildId ? buildIdSchema.safeParse(buildId).success : false;
  const builds = useRoute("builds.list", "builds");
  const [typed, setTyped] = useState("");

  return (
    <section className="tweak-page" data-testid="feature-tweak">
      <header className="tweak-page__head">
        <div>
          <Eyebrow>4GE // TWEAK</Eyebrow>
          <h1 className="tweak-page__title">Tweak</h1>
        </div>
        {builds.data && builds.data.builds.length > 0 ? (
          <SelectField label="Build" value={valid ? (buildId as string) : ""} onValueChange={(id) => id && navigate(`/tweak/${id}`)}>
            <option value="">Choose a build</option>
            {builds.data.builds.map((build) => (
              <option key={build.id} value={build.id}>
                {build.title}
              </option>
            ))}
          </SelectField>
        ) : (
          <form
            className="tweak-page__open"
            onSubmit={(event) => {
              event.preventDefault();
              if (buildIdSchema.safeParse(typed.trim()).success) navigate(`/tweak/${typed.trim()}`);
            }}
          >
            <TextField label="Build id" mono value={typed} onValueChange={setTyped} placeholder="bld_..." />
            <Button type="submit" variant="default" disabled={!buildIdSchema.safeParse(typed.trim()).success}>
              Open
            </Button>
          </form>
        )}
      </header>
      {builds.error ? <p className="tweak-note">{describeError(builds.error)} Open a build by its id instead.</p> : null}
      {builds.data && builds.data.builds.length === 0 ? <p className="tweak-note">There are no builds yet. Start one from Imagistation.</p> : null}
      {buildId && !valid ? <p className="tweak-error">"{buildId}" is not a build id.</p> : null}
      {valid ? <TweakWorkspace buildId={buildId as string} /> : null}
    </section>
  );
}

function TweakWorkspace({ buildId }: { buildId: string }) {
  const build = useRoute("builds.get", `build:${buildId}`, { params: { id: buildId } });
  const [side, setSide] = useState<Side>("system");
  const [target, setTarget] = useState<ReturnType<typeof targetFromIframe>>(null);
  const [mode, setMode] = useState<"view" | "pick" | "pin">("view");
  const [version, setVersion] = useState(0);
  const frame = useRef<HTMLIFrameElement>(null);
  const previewUrl = build.data?.build.previewUrl;
  const reload = () => setVersion((value) => value + 1);

  return (
    <div className="tweak-workspace">
      <aside className="tweak-workspace__side">
        <div className="tweak-kinds" role="tablist" aria-label="Design">
          <button type="button" role="tab" className="tweak-kinds__tab" aria-selected={side === "system"} onClick={() => setSide("system")}>
            Design system
          </button>
          <button type="button" role="tab" className="tweak-kinds__tab" aria-selected={side === "inventory"} onClick={() => setSide("inventory")}>
            Components
          </button>
        </div>
        {side === "system" ? <DesignSystemEditor buildId={buildId} onSaved={reload} /> : <InventoryPicker buildId={buildId} />}
      </aside>

      <div className="tweak-workspace__board">
        <div className="tweak-workspace__tools">
          <Eyebrow>ARTBOARD // {build.data?.build.title ?? buildId}</Eyebrow>
          <Button variant={mode === "pick" ? "accent" : "default"} disabled={!previewUrl} onClick={() => setMode(mode === "pick" ? "view" : "pick")}>
            Pick element
          </Button>
          <Button variant={mode === "pin" ? "accent" : "default"} onClick={() => setMode(mode === "pin" ? "view" : "pin")}>
            Pin comment
          </Button>
        </div>
        {build.error ? <p className="tweak-error">{describeError(build.error)}</p> : null}
        <div className="tweak-artboard">
          {previewUrl ? (
            <iframe key={version} ref={frame} className="tweak-artboard__frame" src={previewUrl} title="Build preview" />
          ) : (
            <p className="tweak-artboard__empty">
              No live preview for this build yet. Start it in the Sandbox to watch tweaks land; comments and tweaks still work.
            </p>
          )}
          {mode === "pick" ? (
            <div
              className="tweak-artboard__picker"
              aria-label="Click the element to tweak"
              onClick={(event) => {
                const rect = event.currentTarget.getBoundingClientRect();
                const picked = targetFromIframe(frame.current, (event.clientX - rect.left) / rect.width, (event.clientY - rect.top) / rect.height);
                if (picked) setTarget(picked);
                setMode("view");
              }}
            />
          ) : null}
          <AnnotationLayer
            buildId={buildId}
            pinning={mode === "pin"}
            onPinningChange={(on) => setMode(on ? "pin" : "view")}
            resolveTarget={(x, y) => targetFromIframe(frame.current, x, y)}
            {...(build.data?.build.rootSessionId ? { smithSessionId: build.data.build.rootSessionId } : {})}
          />
        </div>
        {mode === "pick" && previewUrl ? (
          <p className="tweak-note">Click an element. A preview from another address cannot be read; type the element in the panel instead.</p>
        ) : null}
      </div>

      <aside className="tweak-workspace__panel">
        <TweakPanel buildId={buildId} target={target} onTargetChange={setTarget} onChange={reload} />
      </aside>
    </div>
  );
}
