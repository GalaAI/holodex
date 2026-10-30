// Copied from GalaAI/ark packages/ui/src/surfaces/IntakeComposer.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
// 4GE patch: two undefined guards so it compiles under noUncheckedIndexedAccess; behavior unchanged.
// This composition reuses Ark fields, buttons and panels; the host owns storage.
import { useEffect, useRef, useState } from "react";
import { FileText, Link as LinkIcon, Music } from "lucide-react";
import { Button } from "../actions/Button";
import { Panel } from "./Panel";
import { TextArea } from "../controls/TextArea";

export interface IntakeComposerProps {
  text: string;
  file: File | null;
  surface: string;
  busy: boolean;
  onText: (text: string) => void;
  onFile: (file: File | null) => void;
  onSurface: (surface: string) => void;
  onSubmit: () => void;
}

function detectType(file: File | null, text: string): string {
  if (file) {
    const category = file.type.split("/")[0] ?? "";
    if (["image", "video", "audio", "text"].includes(category)) return category;
    const extension = file.name.split(".").pop()?.toLowerCase() ?? "";
    if (
      ["jpg", "jpeg", "png", "gif", "webp", "heic", "avif"].includes(extension)
    )
      return "image";
    if (["mov", "mp4", "m4v", "webm", "mkv"].includes(extension))
      return "video";
    if (["mp3", "m4a", "wav", "ogg", "flac", "aac"].includes(extension))
      return "audio";
    return "document";
  }
  try {
    const url = new URL(text.trim());
    if (["http:", "https:"].includes(url.protocol)) return "link";
  } catch {
    /* Plain text remains text. */
  }
  return "text";
}

export function IntakeComposer(props: IntakeComposerProps) {
  const input = useRef<HTMLInputElement>(null);
  const [fileError, setFileError] = useState("");
  const [preview, setPreview] = useState<{ file: File; url: string } | null>(
    null,
  );
  const [previewFailed, setPreviewFailed] = useState(false);
  const [dragging, setDragging] = useState(false);
  const type = detectType(props.file, props.text);
  useEffect(() => {
    setPreviewFailed(false);
    if (!props.file || !["image", "video", "audio"].includes(type)) {
      setPreview(null);
      return;
    }
    const url = URL.createObjectURL(props.file);
    setPreview({ file: props.file, url });
    return () => URL.revokeObjectURL(url);
  }, [props.file, type]);
  function selectFiles(files: FileList | null) {
    if (props.busy || !files?.length) return;
    if (files.length > 1) {
      setFileError(
        "Choose one file per intake so each source gets its own receipt.",
      );
      return;
    }
    const file = files[0];
    if (!file) return;
    if (!file.size) {
      setFileError("Choose a file that contains data.");
      if (input.current) input.current.value = "";
      return;
    }
    setFileError("");
    props.onSurface(detectType(file, props.text));
    props.onFile(file);
  }
  const previewUrl = preview?.file === props.file ? preview?.url : undefined;
  return (
    <Panel className="rp-intake-composer">
      <form
        data-dragging={dragging || undefined}
        onDragEnter={(event) => {
          event.preventDefault();
          setDragging(true);
        }}
        onDragOver={(event) => event.preventDefault()}
        onDragLeave={(event) => {
          // Moving between child elements fires dragleave on the form too.
          const next = event.relatedTarget;
          if (next instanceof Node && event.currentTarget.contains(next))
            return;
          setDragging(false);
        }}
        onDrop={(event) => {
          event.preventDefault();
          setDragging(false);
          if (input.current) input.current.value = "";
          selectFiles(event.dataTransfer.files);
        }}
        onSubmit={(event) => {
          event.preventDefault();
          props.onSubmit();
        }}
      >
        <TextArea
          label={props.file ? "Add a note" : "Content"}
          rows={3}
          placeholder="Write, paste a link, or attach a file"
          value={props.text}
          disabled={props.busy}
          onValueChange={(text) => {
            props.onText(text);
            props.onSurface(detectType(props.file, text));
          }}
        />
        {(props.file || props.text.trim()) && (
          <div className="rp-intake-composer__preview">
            {previewUrl && !previewFailed && type === "image" ? (
              <img
                src={previewUrl}
                alt={props.file?.name ?? "Selected image"}
                onError={() => setPreviewFailed(true)}
              />
            ) : previewUrl && !previewFailed && type === "video" ? (
              // biome-ignore lint/a11y/useMediaCaption: Local upload preview has no supplied caption track; do not invent one before processing.
              <video
                src={`${previewUrl}#t=0.1`}
                controls
                playsInline
                preload="metadata"
                aria-label="Selected video"
                onError={() => setPreviewFailed(true)}
              />
            ) : previewUrl && !previewFailed && type === "audio" ? (
              // biome-ignore lint/a11y/useMediaCaption: Local upload preview has no supplied transcript before processing.
              <audio
                src={previewUrl}
                controls
                preload="metadata"
                aria-label="Selected audio"
                onError={() => setPreviewFailed(true)}
              />
            ) : type === "link" ? (
              <LinkIcon aria-hidden="true" />
            ) : type === "audio" ? (
              <Music aria-hidden="true" />
            ) : (
              <FileText aria-hidden="true" />
            )}
            <div>
              <span className="rp-field__label">
                {type.charAt(0).toUpperCase() + type.slice(1)}
              </span>
              <p className="rp-field__hint">
                {props.file?.name ??
                  (type === "link"
                    ? new URL(props.text.trim()).hostname
                    : "Text content")}
              </p>
              {previewFailed && (
                <p className="rp-field__hint">
                  Preview unavailable on this device. The original file can
                  still be uploaded.
                </p>
              )}
            </div>
          </div>
        )}
        <div className="rp-intake-composer__attachment">
          <input
            hidden
            aria-label="Device file or document"
            ref={input}
            type="file"
            disabled={props.busy}
            onChange={(event) => selectFiles(event.target.files)}
          />
          <Button disabled={props.busy} onClick={() => input.current?.click()}>
            {props.file ? "Change file" : "Choose file"}
          </Button>
          {props.file ? (
            <Button
              disabled={props.busy}
              onClick={() => {
                if (input.current) input.current.value = "";
                props.onFile(null);
                props.onSurface(detectType(null, props.text));
                setFileError("");
              }}
            >
              Remove file
            </Button>
          ) : (
            <span className="rp-field__hint">Or drop a file here</span>
          )}
        </div>
        {fileError && <p role="alert">{fileError}</p>}
        <Button
          type="submit"
          variant="primary"
          disabled={props.busy || (!props.file && !props.text.trim())}
        >
          {props.busy ? "Saving" : "Submit intake"}
        </Button>
      </form>
    </Panel>
  );
}
