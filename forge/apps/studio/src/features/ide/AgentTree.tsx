import { buildSessionTree, type Receipt, type SessionNode } from "@4ge/contracts";
import { CadenceMark, Eyebrow, Led } from "@4ge/kit";
import { ChevronRight } from "lucide-react";
import { useEffect, useState } from "react";
import { ForgeApiError, api } from "../../lib/api.js";
import { ErrorNotice, StatusLed, agentName, elapsed } from "../../shell/ui.js";
import { DiffText, useBuildFile } from "./FramePreview.js";
import { layerForSession, type BuildView } from "./state.js";

/**
 * The orchestra as a live, collapsible tree (Claude Code's nested agents):
 * Smith at the root, each delegated layer session under it, with status,
 * elapsed time and the latest one-line summary. Receipts and diffs open on
 * demand; nothing loads until asked.
 */
export interface AgentTreeProps {
  view: BuildView;
  onOpenThread: (sessionId: string) => void;
  onSelectLayer: (layerId: string) => void;
  onReceipts: (receipts: Receipt[]) => void;
}

const LIVE = new Set(["running", "queued", "waiting"]);

export function AgentTree({ view, onOpenThread, onSelectLayer, onReceipts }: AgentTreeProps) {
  const roots = buildSessionTree(Object.values(view.sessions));
  const running = Object.values(view.sessions).some((session) => LIVE.has(session.status));
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    if (!running) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [running]);

  const ordered = [...roots].sort((left, right) =>
    left.id === view.build.rootSessionId ? -1 : right.id === view.build.rootSessionId ? 1 : 0,
  );
  if (ordered.length === 0) {
    return (
      <p className="tree__empty">
        No agent sessions yet. Smith opens one when the build starts and nests a session under it for each layer it delegates.
      </p>
    );
  }
  return (
    <ul className="tree" role="tree" aria-label="Agent sessions" data-testid="agent-tree">
      {ordered.map((node) => (
        <TreeNode
          key={node.id}
          node={node}
          depth={0}
          now={now}
          view={view}
          onOpenThread={onOpenThread}
          onSelectLayer={onSelectLayer}
          onReceipts={onReceipts}
        />
      ))}
    </ul>
  );
}

function TreeNode({
  node,
  depth,
  now,
  view,
  onOpenThread,
  onSelectLayer,
  onReceipts,
}: {
  node: SessionNode;
  depth: number;
  now: number;
} & AgentTreeProps) {
  const [open, setOpen] = useState(depth === 0 || LIVE.has(node.status));
  const [detail, setDetail] = useState<"receipt" | "diff">();
  const [cancelling, setCancelling] = useState(false);
  const [error, setError] = useState<ForgeApiError>();
  const layer = layerForSession(view.build, node.id);
  const diff = layer?.previews.find((preview) => preview.kind === "diff");
  const live = LIVE.has(node.status);

  const cancel = async () => {
    setCancelling(true);
    setError(undefined);
    try {
      await api.call("sessions.cancel", { params: { id: node.id } });
    } catch (failure) {
      setError(failure as ForgeApiError);
    } finally {
      setCancelling(false);
    }
  };

  return (
    <li role="treeitem" aria-expanded={node.children.length ? open : undefined} aria-selected={false} className="tree__item">
      <div className={`tree__row is-${node.status}`} style={{ paddingLeft: `calc(var(--s3) * ${depth})` }}>
        <button
          type="button"
          className="tree__toggle"
          aria-label={open ? "Collapse" : "Expand"}
          onClick={() => setOpen((value) => !value)}
          disabled={node.children.length === 0}
        >
          <ChevronRight size={14} aria-hidden="true" className={open && node.children.length ? "is-open" : undefined} />
        </button>
        <span className="tree__mark">
          {live ? (
            <CadenceMark state="live" label={`${agentName(node.agent)} is working`} />
          ) : node.status === "done" ? (
            <Led state="ok" />
          ) : node.status === "failed" ? (
            <Led state="err" />
          ) : (
            <Led state="warn" />
          )}
        </span>
        <div className="tree__main">
          <div className="tree__head">
            <strong>{agentName(node.agent)}</strong>
            <span className="tree__status">{node.status}</span>
            <span className="tree__time">{elapsed(node.createdAt, node.finishedAt, now)}</span>
          </div>
          <div className="tree__task" title={layer?.title ?? node.task}>
            {layer?.title ?? node.task}
          </div>
          {node.summary ? <div className="tree__summary">{node.summary}</div> : null}
          {node.error ? <div className="tree__error">{node.error}</div> : null}
          <div className="tree__actions">
            <button type="button" className="tree__link" onClick={() => onOpenThread(node.id)}>
              Thread
            </button>
            {layer ? (
              <button type="button" className="tree__link" onClick={() => onSelectLayer(layer.id)}>
                Show on canvas
              </button>
            ) : null}
            {layer ? (
              <button
                type="button"
                className="tree__link"
                aria-expanded={detail === "receipt"}
                onClick={() => setDetail((value) => (value === "receipt" ? undefined : "receipt"))}
              >
                Receipt
              </button>
            ) : null}
            {diff ? (
              <button
                type="button"
                className="tree__link"
                aria-expanded={detail === "diff"}
                onClick={() => setDetail((value) => (value === "diff" ? undefined : "diff"))}
              >
                Diff
              </button>
            ) : null}
            {live ? (
              <button type="button" className="tree__link is-danger" onClick={() => void cancel()} disabled={cancelling}>
                {cancelling ? "Cancelling" : "Cancel"}
              </button>
            ) : null}
          </div>
          {error ? <ErrorNotice error={error} compact /> : null}
          {detail === "receipt" && layer ? (
            <ReceiptDetail receipt={layer.receiptId ? view.receipts[layer.receiptId] : undefined} receiptId={layer.receiptId} buildId={view.build.id} onReceipts={onReceipts} />
          ) : null}
          {detail === "diff" && diff ? <DiffDetail buildId={view.build.id} path={diff.ref} /> : null}
        </div>
      </div>
      {open && node.children.length ? (
        <ul role="group" className="tree__children">
          {node.children.map((child) => (
            <TreeNode
              key={child.id}
              node={child}
              depth={depth + 1}
              now={now}
              view={view}
              onOpenThread={onOpenThread}
              onSelectLayer={onSelectLayer}
              onReceipts={onReceipts}
            />
          ))}
        </ul>
      ) : null}
    </li>
  );
}

function ReceiptDetail({
  receipt,
  receiptId,
  buildId,
  onReceipts,
}: {
  receipt: Receipt | undefined;
  receiptId: string | undefined;
  buildId: string;
  onReceipts: (receipts: Receipt[]) => void;
}) {
  const [error, setError] = useState<ForgeApiError>();
  const [loading, setLoading] = useState(false);
  useEffect(() => {
    if (receipt || !receiptId) return;
    setLoading(true);
    api
      .call("builds.receipts", { params: { id: buildId } })
      .then((result) => onReceipts(result.receipts))
      .catch((failure: unknown) => setError(failure as ForgeApiError))
      .finally(() => setLoading(false));
  }, [receipt, receiptId, buildId, onReceipts]);
  if (!receiptId) return <p className="tree__detail">No receipt yet. A layer records one when its checks run.</p>;
  if (error) return <ErrorNotice error={error} compact />;
  if (!receipt) return <p className="tree__detail">{loading ? "Loading the receipt" : "The receipt is not on record."}</p>;
  return (
    <div className="tree__detail" data-testid="receipt">
      <Eyebrow>Receipt // {receipt.id}</Eyebrow>
      <ul className="tree__checks">
        {receipt.checks.map((check) => (
          <li key={check.name}>
            <StatusLed status={check.passed ? "passed" : "failed"}>{check.name}</StatusLed>
            {check.detail ? <span className="tree__muted">{check.detail}</span> : null}
          </li>
        ))}
      </ul>
      <Eyebrow>Artifacts</Eyebrow>
      <ul className="tree__files">
        {receipt.artifacts.map((artifact) => (
          <li key={artifact}>{artifact}</li>
        ))}
      </ul>
      {receipt.commit ? <span className="tree__muted">commit {receipt.commit.slice(0, 12)}</span> : null}
    </div>
  );
}

function DiffDetail({ buildId, path }: { buildId: string; path: string }) {
  const file = useBuildFile(buildId, path);
  if (file.state === "loading") return <p className="tree__detail">Loading the diff</p>;
  if (file.state === "error") return <ErrorNotice error={file.error} compact />;
  return (
    <div className="tree__detail">
      <DiffText text={file.text} />
    </div>
  );
}
