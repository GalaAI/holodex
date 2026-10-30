import { STAGE_LABELS, type Annotation, type ChatMessage } from "@4ge/contracts";
import { Button, Eyebrow, TextArea } from "@4ge/kit";
import { ChevronLeft, ChevronRight, ExternalLink, MapPin, SlidersHorizontal, X } from "lucide-react";
import { useImperativeHandle, useState, type Ref } from "react";
import { ForgeApiError, api } from "../../lib/api.js";
import { ErrorNotice, agentName, statusWord } from "../../shell/ui.js";
import { TweakPanel } from "../tweak/index.js";
import { buildFileUrl, FramePreview } from "./FramePreview.js";
import { frameIdFromSelector, frameSelector, type Frame, type Verdict } from "./frames.js";
import type { BuildView } from "./state.js";
import { StageView } from "./StageViews.js";
import { ThreadCard } from "./threads.js";

/**
 * The frame inspector (audit board): a preview, meta chips, the three
 * verdicts, a note that becomes a thread for Smith, Previous and Next.
 * Keys: arrows move, 1 2 3 set the verdict, F fits, Esc closes (wired in
 * the IDE so they work while the canvas has focus).
 */
export interface InspectorProps {
  frame: Frame;
  view: BuildView;
  verdict: Verdict | undefined;
  onVerdict: (verdict: Verdict | undefined) => void;
  onPrevious: () => void;
  onNext: () => void;
  onClose: () => void;
  annotations: Annotation[];
  onAnnotation: (annotation: Annotation) => void;
  smithMessages: ChatMessage[];
  onSendToSmith: (annotation: Annotation) => Promise<boolean>;
  smithReady: boolean;
  pinMode: boolean;
  onPinMode: (on: boolean) => void;
  /** The IDE calls pin(x, y) when the user clicks this frame in pin mode. */
  pinRef: Ref<InspectorControls>;
}

export interface InspectorControls {
  pin: (x: number, y: number) => void;
}

const VERDICTS: Array<{ id: Verdict; label: string; key: string }> = [
  { id: "right", label: "Looks right", key: "1" },
  { id: "work", label: "Needs work", key: "2" },
  { id: "broken", label: "Broken", key: "3" },
];

const PREFIX: Record<Verdict, string> = { right: "", work: "Needs work: ", broken: "Broken: " };

export function Inspector(props: InspectorProps) {
  const { frame, view, verdict } = props;
  const layer = frame.layerId ? view.build.layers.find((item) => item.id === frame.layerId) : undefined;
  const stage = view.build.stages.find((item) => item.stage === frame.stage);
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ForgeApiError>();
  const threads = props.annotations.filter((annotation) => frameIdFromSelector(annotation.target.selector) === frame.id);
  const scale = 320 / frame.width;
  // Artboards that show the built app (its HTML or a live URL) can be tuned
  // in place: the tweak feature edits the build's real source, then the
  // preview reloads to show the change.
  const tunable = frame.kind !== "stage" && (frame.preview?.kind === "html" || frame.preview?.kind === "url");
  const [tuning, setTuning] = useState(false);
  const [previewVersion, setPreviewVersion] = useState(0);

  const pinThread = async (x?: number, y?: number) => {
    const body = `${verdict ? PREFIX[verdict] : ""}${note.trim()}`;
    if (!note.trim()) return;
    setBusy(true);
    setError(undefined);
    try {
      const result = await api.call("design.annotations.create", {
        body: {
          buildId: view.build.id,
          target: {
            selector: frameSelector(frame.id),
            page: frame.stage,
            label: frame.title,
            ...(x !== undefined ? { x } : {}),
            ...(y !== undefined ? { y } : {}),
          },
          body,
        },
      });
      props.onAnnotation(result.annotation);
      setNote("");
    } catch (failure) {
      setError(failure as ForgeApiError);
    } finally {
      setBusy(false);
    }
  };
  useImperativeHandle(props.pinRef, () => ({ pin: (x: number, y: number) => void pinThread(x, y) }));

  return (
    <aside className="inspector" aria-label={`Inspector: ${frame.title}`} data-testid="inspector">
      <header className="inspector__head">
        <div>
          <Eyebrow>{`${STAGE_LABELS[frame.stage]} // ${frame.kind === "stage" ? "stage view" : "artboard"}`}</Eyebrow>
          <h2 className="inspector__title">{frame.title}</h2>
        </div>
        <button type="button" className="board__icon" aria-label="Close the inspector (Esc)" onClick={props.onClose}>
          <X size={16} aria-hidden="true" />
        </button>
      </header>
      <div className="inspector__preview" style={{ height: frame.height * scale }}>
        <div style={{ width: frame.width, height: frame.height, transform: `scale(${scale})`, transformOrigin: "0 0" }}>
          {frame.kind === "stage" ? (
            <StageView stage={frame.stage} view={view} interactive={false} />
          ) : frame.preview ? (
            <FramePreview key={previewVersion} buildId={view.build.id} preview={frame.preview} interactive={false} />
          ) : null}
        </div>
      </div>
      <ul className="inspector__chips">
        <li>{STAGE_LABELS[frame.stage]}</li>
        {frame.agent ? <li>{agentName(frame.agent)}</li> : null}
        {layer ? <li>layer {statusWord(layer.status)}</li> : stage ? <li>stage {statusWord(stage.status)}</li> : null}
        {layer && layer.testFlight !== "not-run" ? <li>test flight {statusWord(layer.testFlight)}</li> : null}
        {layer?.receiptId ? <li>{layer.receiptId}</li> : stage?.receiptId ? <li>{stage.receiptId}</li> : null}
      </ul>
      {layer?.error ? <p className="inspector__error">{layer.error}</p> : null}
      {frame.preview?.kind === "url" ? (
        <a className="inspector__link" href={frame.preview.ref} target="_blank" rel="noreferrer">
          <ExternalLink size={14} aria-hidden="true" /> Open in a new tab
        </a>
      ) : frame.preview && frame.preview.kind !== "text" && frame.preview.kind !== "diff" ? (
        <a className="inspector__link" href={buildFileUrl(view.build.id, frame.preview.ref)} target="_blank" rel="noreferrer">
          <ExternalLink size={14} aria-hidden="true" /> Open the file
        </a>
      ) : null}
      {tunable ? (
        <section className="inspector__tweak" aria-label="Tweak this artboard">
          <Button onClick={() => setTuning(!tuning)} aria-expanded={tuning} data-testid="inspector-tweak">
            <SlidersHorizontal size={14} aria-hidden="true" /> {tuning ? "Close the tweak controls" : "Tweak this artboard"}
          </Button>
          {tuning ? (
            <TweakPanel buildId={view.build.id} hideHistory onChange={() => setPreviewVersion((value) => value + 1)} />
          ) : null}
        </section>
      ) : null}
      <div className="inspector__verdicts" role="group" aria-label="Verdict">
        {VERDICTS.map((item) => (
          <button
            key={item.id}
            type="button"
            className={`verdict is-${item.id}${verdict === item.id ? " is-active" : ""}`}
            aria-pressed={verdict === item.id}
            onClick={() => props.onVerdict(verdict === item.id ? undefined : item.id)}
          >
            <kbd>{item.key}</kbd> {item.label}
          </button>
        ))}
      </div>
      <TextArea
        label="Note for Smith"
        rows={3}
        value={note}
        maxLength={4000}
        onValueChange={setNote}
        hint="Say what you see and what it should do. It becomes a thread on this frame."
      />
      <div className="inspector__row">
        <Button
          onClick={() => props.onPinMode(!props.pinMode)}
          aria-pressed={props.pinMode}
          disabled={!note.trim()}
          title="Click a spot on a frame to pin the note there"
        >
          <MapPin size={14} aria-hidden="true" /> {props.pinMode ? "Click the frame" : "Pin on the frame"}
        </Button>
        <Button variant="primary" onClick={() => void pinThread()} disabled={busy || !note.trim()}>
          {busy ? "Saving" : "Add thread"}
        </Button>
      </div>
      {error ? <ErrorNotice error={error} compact /> : null}
      <div className="inspector__nav">
        <Button onClick={props.onPrevious}>
          <ChevronLeft size={14} aria-hidden="true" /> Previous
        </Button>
        <span className="inspector__keys">arrows move // 1 2 3 verdict // F fit // Esc close</span>
        <Button onClick={props.onNext}>
          Next <ChevronRight size={14} aria-hidden="true" />
        </Button>
      </div>
      {threads.length ? (
        <section className="inspector__threads" aria-label="Threads on this frame">
          <Eyebrow>{`Threads // ${threads.length}`}</Eyebrow>
          {threads.map((annotation) => (
            <ThreadCard
              key={annotation.id}
              annotation={annotation}
              smithMessages={props.smithMessages}
              onSendToSmith={props.onSendToSmith}
              onChanged={props.onAnnotation}
              smithReady={props.smithReady}
            />
          ))}
        </section>
      ) : null}
    </aside>
  );
}
