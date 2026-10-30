// Copied from GalaAI/ark packages/ui/src/surfaces/GateCard.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import { useId, useState, type ReactNode } from "react";
import { Button } from "../actions/Button";
import { SpineLabel } from "../brand/SpineLabel";

/**
 * GateCard: the operator approval surface a run raises at a gate. Static
 * status edge, hatch bands as state, approve / hold / reject footer.
 *
 * pending: waiting on the operator (ember, glowing). held: paused by the operator. approved: moss, unlit. rejected: rust, unlit.
 */
export type GateState = "pending" | "held" | "approved" | "rejected";
export type CheckResult = "ok" | "err" | "warn" | "run";

export interface GateFile {
  path: string;
  added?: number;
  removed?: number;
}
export interface GateCheck {
  label: string;
  result: CheckResult;
}

export interface GateCardProps {
  /** Eyebrow, e.g. "Gate // 03 // ARK://4GE // BUILD LOADER SET". Ties the gate to the RunCard step that raised it. */
  source: string;
  /** What the operator is approving, in plain words. */
  title: string;
  /** One or two sentences: what happens on approve, and that nothing runs until then. */
  summary: string;
  state: GateState;
  /** Vertical label down the outside of the edge; false hides it. */
  spine?: string | false;
  changes?: GateFile[];
  checks?: GateCheck[];
  /** Risk tags, e.g. ["write", "reversible"]. "write" reads ember, "destructive" reads rust. */
  risk?: string[];
  /** Unified diff text for the disclosure. Lines starting with + / - / @@ are colored. */
  diff?: string;
  /** Who decided and when, shown once the gate is no longer pending ("You // 14:15"). */
  decided?: string;
  onApprove?: () => void;
  onHold?: () => void;
  onReject?: () => void;
  onResume?: () => void;
  /** Extra footer content, e.g. a link back to the run. */
  footer?: ReactNode;
  className?: string;
}

const STATUS: Record<GateState, { led: string; text: string }> = {
  pending: { led: "warn", text: "Needs you" },
  held: { led: "", text: "Held" },
  approved: { led: "ok", text: "Approved" },
  rejected: { led: "err", text: "Rejected" },
};
const CHECK_LED: Record<CheckResult, string> = {
  ok: "ok",
  err: "err",
  warn: "warn",
  run: "",
};
const ledClass = (led: string) =>
  led ? `rp-gate-card__led rp-gate-card__led--${led}` : "rp-gate-card__led";

function DiffLines({ text }: { text: string }) {
  return (
    <>
      {text.split("\n").map((l, i) => (
        <span
          key={i}
          className={
            l.startsWith("@@")
              ? "rp-gate-card__diff-hunk"
              : l.startsWith("+")
                ? "rp-gate-card__diff-add"
                : l.startsWith("-")
                  ? "rp-gate-card__diff-del"
                  : undefined
          }
        >
          {l + "\n"}
        </span>
      ))}
    </>
  );
}

export function GateCard(p: GateCardProps) {
  const uid = useId();
  const [diffOpen, setDiffOpen] = useState(false);
  const st = STATUS[p.state];
  const riskCls = (r: string) =>
    r === "write"
      ? "rp-gate-card__tag rp-gate-card__tag--write"
      : r === "destructive"
        ? "rp-gate-card__tag rp-gate-card__tag--danger"
        : "rp-gate-card__tag";

  return (
    <div
      className={["rp-gate-card", p.className].filter(Boolean).join(" ")}
      data-state={p.state}
    >
      <span className="rp-gate-card__halo" aria-hidden="true" />
      {p.spine !== false ? (
        <SpineLabel
          text={typeof p.spine === "string" ? p.spine : "Gate // " + st.text}
          tone={
            p.state === "pending"
              ? "gate"
              : p.state === "rejected"
                ? "error"
                : p.state === "approved"
                  ? "ok"
                  : "rest"
          }
        />
      ) : null}
      <section
        className="rp-gate-card__panel"
        data-diff={diffOpen ? "true" : "false"}
        aria-label={"Gate: " + p.title}
      >
        <div className="rp-gate-card__hatch" aria-hidden="true" />
        <header className="rp-gate-card__head">
          <div className="rp-gate-card__titles">
            <span className="rp-gate-card__eyebrow">{p.source}</span>
            <h3 className="rp-gate-card__title">{p.title}</h3>
          </div>
          <span
            className="rp-gate-card__status"
            role="status"
            aria-live="polite"
          >
            <i className={ledClass(st.led)} aria-hidden="true" />
            {st.text}
          </span>
        </header>
        <div className="rp-gate-card__body">
          <p className="rp-gate-card__summary">{p.summary}</p>
          <div className="rp-gate-card__grid">
            {p.changes?.length ? (
              <div className="rp-gate-card__section">
                <span className="rp-gate-card__eyebrow">What changes</span>
                <div className="rp-gate-card__files">
                  {p.changes.map((f) => (
                    <span key={f.path}>
                      {f.path}
                      {f.added ? <b>+{f.added}</b> : null}
                      {f.removed ? <i>-{f.removed}</i> : null}
                    </span>
                  ))}
                </div>
              </div>
            ) : null}
            {p.checks?.length ? (
              <div className="rp-gate-card__section">
                <span className="rp-gate-card__eyebrow">Checks</span>
                <ul className="rp-gate-card__checks">
                  {p.checks.map((c) => (
                    <li key={c.label}>
                      <i
                        className={ledClass(CHECK_LED[c.result])}
                        aria-hidden="true"
                      />
                      {c.label} {"//"}{" "}
                      {c.result === "run"
                        ? "running"
                        : c.result === "err"
                          ? "failed"
                          : c.result === "warn"
                            ? "warning"
                            : "passed"}
                    </li>
                  ))}
                </ul>
              </div>
            ) : null}
          </div>
          {p.risk?.length ? (
            <div className="rp-gate-card__risk">
              <span className="rp-gate-card__eyebrow">Risk</span>
              {p.risk.map((r) => (
                <span key={r} className={riskCls(r)}>
                  {r}
                </span>
              ))}
            </div>
          ) : null}
          {p.diff ? (
            <>
              <button
                type="button"
                className="rp-gate-card__diff-toggle"
                aria-expanded={diffOpen}
                aria-controls={uid + "-diff"}
                onClick={() => setDiffOpen((o) => !o)}
              >
                <svg
                  className="rp-gate-card__chev"
                  viewBox="0 0 16 16"
                  aria-hidden="true"
                >
                  <path d="M6 3.5 10.5 8 6 12.5" />
                </svg>
                {diffOpen ? "Hide diff" : "Show diff"}
              </button>
              <div className="rp-gate-card__more" id={uid + "-diff"}>
                <div className="rp-gate-card__more-in">
                  <pre className="rp-gate-card__diff" aria-hidden={!diffOpen}>
                    <DiffLines text={p.diff} />
                  </pre>
                </div>
              </div>
            </>
          ) : null}
        </div>
        <footer className="rp-gate-card__foot">
          {p.state !== "pending" && p.decided ? (
            <span className="rp-gate-card__who">
              {st.text} {"//"} {p.decided}
            </span>
          ) : null}
          {p.footer}
          <div className="rp-gate-card__actions">
            {p.state === "pending" ? (
              <>
                {p.onReject ? (
                  <Button
                    variant="danger"
                    className="rp-gate-card__btn rp-gate-card__btn--danger"
                    onClick={p.onReject}
                  >
                    Reject
                  </Button>
                ) : null}
                {p.onHold ? (
                  <Button className="rp-gate-card__btn" onClick={p.onHold}>
                    Hold
                  </Button>
                ) : null}
                {p.onApprove ? (
                  <Button
                    variant="primary"
                    className="rp-gate-card__btn rp-gate-card__btn--primary"
                    onClick={p.onApprove}
                  >
                    Approve
                  </Button>
                ) : null}
              </>
            ) : p.state === "held" ? (
              <>
                {p.onReject ? (
                  <Button
                    variant="danger"
                    className="rp-gate-card__btn rp-gate-card__btn--danger"
                    onClick={p.onReject}
                  >
                    Reject
                  </Button>
                ) : null}
                {p.onResume ? (
                  <Button className="rp-gate-card__btn" onClick={p.onResume}>
                    Resume review
                  </Button>
                ) : null}
              </>
            ) : null}
          </div>
        </footer>
        {p.state === "approved" ? null : (
          <div className="rp-gate-card__hatch" aria-hidden="true" />
        )}
      </section>
      <span className="rp-gate-card__ear" aria-hidden="true" />
    </div>
  );
}
