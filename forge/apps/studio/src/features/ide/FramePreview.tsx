import { apiErrorSchema, type Preview } from "@4ge/contracts";
import { CadenceDots } from "@4ge/kit";
import { useEffect, useState } from "react";
import { ForgeApiError, api } from "../../lib/api.js";
import { describeError } from "../../shell/ui.js";

/**
 * Renders one preview as the frame body: a live URL, the build's own HTML,
 * an image, or text and diffs, read from the server's build files route.
 * Iframes take pointer input only while the frame is selected, so the board
 * stays pannable (StageCanvas data-stage-interactive rule).
 */
export function buildFileUrl(buildId: string, path: string): string {
  return api.url("builds.files.get", { id: buildId }, { path });
}

type FileState =
  | { state: "loading" }
  | { state: "ok"; text: string; contentType: string }
  | { state: "error"; error: ForgeApiError };

/** GET a build file once; JSON error bodies become plain-word failures. */
export function useBuildFile(buildId: string, path: string | undefined): FileState {
  const [state, setState] = useState<FileState>({ state: "loading" });
  useEffect(() => {
    if (!path) return;
    const controller = new AbortController();
    setState({ state: "loading" });
    fetch(buildFileUrl(buildId, path), { signal: controller.signal })
      .then(async (response) => {
        const text = await response.text();
        const contentType = response.headers.get("content-type") ?? "";
        if (!response.ok) {
          let parsed: ReturnType<typeof apiErrorSchema.safeParse> | undefined;
          try {
            parsed = apiErrorSchema.safeParse(JSON.parse(text));
          } catch {
            parsed = undefined;
          }
          throw new ForgeApiError({
            status: response.status,
            code: parsed?.success ? parsed.data.error.code : "invalid_response",
            message: parsed?.success ? parsed.data.error.message : `The build file could not be read (HTTP ${response.status}).`,
            hint: parsed?.success ? parsed.data.error.hint : undefined,
            route: "builds.files.get",
          });
        }
        setState({ state: "ok", text, contentType });
      })
      .catch((failure: unknown) => {
        if (controller.signal.aborted) return;
        setState({
          state: "error",
          error:
            failure instanceof ForgeApiError
              ? failure
              : new ForgeApiError({ status: 0, code: "network", message: String(failure), route: "builds.files.get" }),
        });
      });
    return () => controller.abort();
  }, [buildId, path]);
  return state;
}

export function FramePreview({ buildId, preview, interactive }: { buildId: string; preview: Preview; interactive: boolean }) {
  switch (preview.kind) {
    case "url":
      return (
        <iframe
          className="frame-preview__iframe"
          title={preview.title}
          src={preview.ref}
          sandbox="allow-scripts allow-same-origin allow-forms allow-popups"
          style={{ pointerEvents: interactive ? "auto" : "none" }}
          data-stage-interactive={interactive ? "" : undefined}
        />
      );
    case "html":
      return <CheckedFile buildId={buildId} preview={preview} interactive={interactive} />;
    case "image":
      return <CheckedFile buildId={buildId} preview={preview} interactive={interactive} />;
    default:
      return <TextFile buildId={buildId} preview={preview} />;
  }
}

function FileProblem({ error }: { error: ForgeApiError }) {
  const text = describeError(error);
  return (
    <div className="frame-preview__problem" role="status">
      <strong>{text.title}</strong>
      <span>{text.body}</span>
      {text.hint ? <span>{text.hint}</span> : null}
    </div>
  );
}

function Loading() {
  return (
    <div className="frame-preview__loading">
      <CadenceDots tone="cream" label="Loading the preview" />
    </div>
  );
}

function CheckedFile({ buildId, preview, interactive }: { buildId: string; preview: Preview; interactive: boolean }) {
  const file = useBuildFile(buildId, preview.ref);
  if (file.state === "loading") return <Loading />;
  if (file.state === "error") return <FileProblem error={file.error} />;
  const url = buildFileUrl(buildId, preview.ref);
  if (preview.kind === "image") return <img className="frame-preview__image" src={url} alt={preview.title} draggable={false} />;
  return (
    <iframe
      className="frame-preview__iframe"
      title={preview.title}
      src={url}
      sandbox="allow-scripts"
      style={{ pointerEvents: interactive ? "auto" : "none" }}
      data-stage-interactive={interactive ? "" : undefined}
    />
  );
}

function TextFile({ buildId, preview }: { buildId: string; preview: Preview }) {
  const file = useBuildFile(buildId, preview.ref);
  if (file.state === "loading") return <Loading />;
  if (file.state === "error") return <FileProblem error={file.error} />;
  if (preview.kind === "diff") return <DiffText text={file.text} />;
  return <pre className="frame-preview__text">{file.text}</pre>;
}

export function DiffText({ text }: { text: string }) {
  return (
    <pre className="frame-preview__text frame-preview__diff">
      {text.split("\n").map((line, index) => (
        <span
          // biome-ignore lint/suspicious/noArrayIndexKey: diff lines have no identity beyond their position.
          key={index}
          className={
            line.startsWith("@@")
              ? "is-hunk"
              : line.startsWith("+")
                ? "is-add"
                : line.startsWith("-")
                  ? "is-del"
                  : undefined
          }
        >
          {`${line}\n`}
        </span>
      ))}
    </pre>
  );
}
