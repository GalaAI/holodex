import type { Adapter, AdapterAction, AdapterActionRun, Augment, ConnectAction, LibraryItem, Mod } from "@4ge/contracts";
import { Button, CheckboxField, Eyebrow, Led, Panel, TextArea, TextField, type LedState } from "@4ge/kit";
import { Plus } from "lucide-react";
import { useMemo, useState, type FormEvent } from "react";
import { NavLink, useNavigate, useParams, useSearchParams } from "react-router";
import { ForgeApiError, api, useRoute } from "../../lib/api.js";
import { ErrorNotice } from "../../shell/ui.js";
import { SmithPanel, useSmithSession } from "../library/SmithPanel.js";
import "../library/library.css";
import "./mods.css";

/**
 * Mods (SPEC "Mods, adapters, augments"): a minimal inventory of the user's
 * mods with the plus tile, and for one mod its adapters (connect, actions
 * run with the user's own CLI) and augments. A mod is agent-run: "Ask Smith"
 * opens a Smith session about it. Every status comes from the server's live
 * detection.
 */

const MOD_LED: Record<Mod["status"], LedState> = { ready: "ok", "needs-setup": "warn", error: "err" };
/** The rail reads the Mods group of GET /library, so it lists exactly what the Library shows. */
const TONE_LED: Record<LibraryItem["tone"], LedState | undefined> = { live: "live", ok: "ok", warn: "warn", err: "err", rest: undefined };
const ADAPTER_LED: Record<Adapter["status"], LedState> = {
  connected: "ok",
  disconnected: "warn",
  "needs-cli": "warn",
  expired: "warn",
  error: "err",
};
const ADAPTER_WORD: Record<Adapter["status"], string> = {
  connected: "Connected",
  disconnected: "Not connected",
  "needs-cli": "CLI missing",
  expired: "Key expired",
  error: "Problem",
};

function asApiError(failure: unknown, route: string): ForgeApiError {
  return failure instanceof ForgeApiError
    ? failure
    : new ForgeApiError({ status: 0, code: "network", message: failure instanceof Error ? failure.message : String(failure), route });
}

export default function Mods() {
  const { modId } = useParams();
  const [search] = useSearchParams();
  const creating = search.get("create") === "1";
  const library = useRoute("library.list", "library");
  const modItems = library.data?.groups.find((group) => group.origin === "mod")?.items ?? [];
  const smith = useSmithSession();
  const selected = modId ? decodeURIComponent(modId) : undefined;

  return (
    <section className="lib mods" data-testid="feature-mods">
      <div className="mods__body">
        <aside className="mods__rail" aria-label="Your mods">
          <Eyebrow>4GE // MODS</Eyebrow>
          {library.error ? <ErrorNotice error={library.error} compact onRetry={library.reload} /> : null}
          {library.loading && !library.data ? <p className="lib-muted">Checking your mods.</p> : null}
          <ul className="mods__list">
            {modItems.map((item) => (
              <li key={item.id}>
                <NavLink to={`/mods/${encodeURIComponent(item.id)}`} className={({ isActive }) => `mods__item${isActive ? " is-active" : ""}`}>
                  <span className="lib-tile__title">{item.title}</span>
                  <Led state={TONE_LED[item.tone]}>{item.status ?? item.subtitle ?? ""}</Led>
                </NavLink>
              </li>
            ))}
            <li>
              <NavLink to="/mods?create=1" className={`mods__item mods__item--plus${creating ? " is-active" : ""}`} aria-label="Make a mod">
                <Plus aria-hidden="true" size={18} />
                <span>Make a mod</span>
              </NavLink>
            </li>
          </ul>
        </aside>
        <div className="mods__main">
          {creating ? (
            <CreateMod onCreated={library.reload} />
          ) : selected ? (
            <ModDetail key={selected} modId={selected} onAskSmith={(mod) =>
              void smith.open({
                agent: "smith",
                kind: "mod",
                task: `Help me with ${mod.name}: what it does, what is not set up yet, and how to upgrade it.`,
                subject: { type: "mod", id: mod.id, label: mod.name },
              })
            } />
          ) : (
            <Panel className="mods__intro">
              <Eyebrow>MODS // INVENTORY</Eyebrow>
              <h1 className="lib__title">Mods</h1>
              <p>
                A mod bundles adapters (your accounts and services) and augments (MCP servers, skills, prompts, tool packs). Smith runs
                it. Pick a mod to connect its adapters and run its actions, or make a new one with the plus tile.
              </p>
            </Panel>
          )}
        </div>
      </div>
      <SmithPanel smith={smith} />
    </section>
  );
}

function ModDetail({ modId, onAskSmith }: { modId: string; onAskSmith: (mod: Mod) => void }) {
  const mod = useRoute("mods.get", `mod:${modId}`, { params: { id: modId } });
  const adapters = useRoute("adapters.list", "adapters");
  const augments = useRoute("augments.list", "augments");
  if (mod.error) return <ErrorNotice error={mod.error} onRetry={mod.reload} />;
  if (!mod.data) return <p className="lib-muted">Loading the mod.</p>;
  const current = mod.data.mod;
  const own = (adapters.data?.adapters ?? []).filter((adapter) => current.adapters.includes(adapter.id));
  const ownAugments = (augments.data?.augments ?? []).filter((augment) => current.augments.includes(augment.id));
  const reload = () => {
    mod.reload();
    adapters.reload();
    augments.reload();
  };
  return (
    <div className="mods__detail">
      <header className="lib__head">
        <div>
          <Eyebrow>{`MOD // ${current.kind.toUpperCase()}`}</Eyebrow>
          <h1 className="lib__title">{current.name}</h1>
          <p className="lib-muted">{current.description}</p>
          <Led state={MOD_LED[current.status]}>{current.statusMessage ?? current.status}</Led>
        </div>
        <Button variant="primary" onClick={() => onAskSmith(current)}>
          Ask Smith
        </Button>
      </header>
      <section className="lib-group" aria-label="Adapters">
        <h2 className="lib-group__label">
          Adapters <span className="lib-group__count">{current.adapters.length}</span>
        </h2>
        {adapters.error ? <ErrorNotice error={adapters.error} compact onRetry={adapters.reload} /> : null}
        {adapters.loading && !adapters.data ? <p className="lib-muted">Checking which CLIs are installed and signed in.</p> : null}
        {own.map((adapter) => (
          <AdapterCard key={adapter.id} adapter={adapter} onChanged={reload} />
        ))}
      </section>
      <section className="lib-group" aria-label="Augments">
        <h2 className="lib-group__label">
          Augments <span className="lib-group__count">{current.augments.length}</span>
        </h2>
        {augments.error ? <ErrorNotice error={augments.error} compact onRetry={augments.reload} /> : null}
        <ul className="mods__augments">
          {ownAugments.map((augment) => (
            <AugmentRow key={augment.id} augment={augment} />
          ))}
        </ul>
      </section>
    </div>
  );
}

function AugmentRow({ augment }: { augment: Augment }) {
  const state: LedState = augment.status === "ready" ? "ok" : augment.status === "error" ? "err" : "warn";
  const where =
    augment.mcp?.transport === "streamable-http"
      ? augment.mcp.url
      : augment.mcp?.transport === "stdio"
        ? [augment.mcp.command, ...augment.mcp.args].join(" ")
        : augment.skillPath;
  return (
    <li className="mods__augment">
      <span className="lib-tile__title">{augment.name}</span>
      <span className="mods__kind">{augment.kind}</span>
      <Led state={state}>{augment.statusMessage ?? augment.status}</Led>
      {where ? <code className="mods__code">{where}</code> : null}
    </li>
  );
}

function AdapterCard({ adapter, onChanged }: { adapter: Adapter; onChanged: () => void }) {
  const [token, setToken] = useState("");
  const [busy, setBusy] = useState(false);
  const [next, setNext] = useState<ConnectAction>();
  const [error, setError] = useState<ForgeApiError>();
  const isToken = adapter.auth.method === "token";

  const connect = async (event?: FormEvent) => {
    event?.preventDefault();
    setBusy(true);
    setError(undefined);
    try {
      const result = await api.call("adapters.connect", {
        params: { id: adapter.id },
        body: isToken && token.trim() ? { token: token.trim() } : {},
      });
      setToken("");
      setNext(result.action);
      onChanged();
    } catch (failure) {
      setError(asApiError(failure, "adapters.connect"));
    } finally {
      setBusy(false);
    }
  };

  const actions = (adapter.actions ?? []).filter((action) => action.id !== "sign-in");
  return (
    <Panel className="mods__adapter" data-adapter={adapter.id}>
      <div className="mods__adapter-head">
        <div>
          <span className="lib-tile__title">{adapter.name}</span>
          <Led state={ADAPTER_LED[adapter.status]}>
            {ADAPTER_WORD[adapter.status]}
            {adapter.account ? `: ${adapter.account}` : ""}
          </Led>
        </div>
        {adapter.status !== "connected" ? (
          <a className="mods__link" href={adapter.signUpUrl} target="_blank" rel="noreferrer">
            No account yet? Sign up
          </a>
        ) : null}
      </div>
      {adapter.statusMessage ? <p className="lib-muted">{adapter.statusMessage}</p> : null}
      {adapter.cli ? (
        <p className="lib-muted">
          CLI <code className="mods__code">{adapter.cli.bin}</code> {adapter.cli.installed ? "is installed." : `is not installed: ${adapter.cli.install}`}
        </p>
      ) : null}
      {adapter.mcpNote ? <p className="lib-muted">{adapter.mcpNote}</p> : null}
      {adapter.status !== "connected" ? (
        <form className="lib-repo-form" onSubmit={connect}>
          {isToken ? (
            <TextField label="Paste your key" type="password" value={token} onValueChange={setToken} autoComplete="off" mono />
          ) : null}
          <Button type="submit" disabled={busy}>
            {busy ? "Connecting" : adapter.cli && !adapter.cli.installed ? "Check again" : "Connect"}
          </Button>
        </form>
      ) : null}
      {next ? <ConnectNext action={next} /> : null}
      {error ? <ErrorNotice error={error} compact /> : null}
      {actions.length > 0 ? (
        <details className="mods__actions">
          <summary>Actions ({actions.length})</summary>
          {actions.map((action) => (
            <ActionForm key={action.id} adapter={adapter} action={action} onRan={onChanged} />
          ))}
        </details>
      ) : null}
    </Panel>
  );
}

function ConnectNext({ action }: { action: ConnectAction }) {
  return (
    <div className="studio-notice" role="status">
      <span>{action.message}</span>
      {action.kind === "run-cli" ? <code className="mods__code">{action.command}</code> : null}
      {action.kind === "open-url" || action.kind === "install-cli" ? (
        <a className="mods__link" href={action.url} target="_blank" rel="noreferrer">
          Open the page
        </a>
      ) : null}
      {action.kind === "enter-token" ? (
        <a className="mods__link" href={action.tokenUrl} target="_blank" rel="noreferrer">
          Make a key
        </a>
      ) : null}
    </div>
  );
}

function ActionForm({ adapter, action, onRan }: { adapter: Adapter; action: AdapterAction; onRan: () => void }) {
  const [values, setValues] = useState<Record<string, string>>({});
  const [confirm, setConfirm] = useState(false);
  const [busy, setBusy] = useState(false);
  const [run, setRun] = useState<AdapterActionRun>();
  const [error, setError] = useState<ForgeApiError>();
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setError(undefined);
    try {
      const result = await api.call("adapters.actions.run", {
        params: { id: adapter.id, action: action.id },
        body: { params: values, ...(action.spends ? { confirm } : {}) },
      });
      setRun(result.run);
      onRan();
    } catch (failure) {
      setError(asApiError(failure, "adapters.actions.run"));
    } finally {
      setBusy(false);
    }
  };
  return (
    <form className="mods__action" onSubmit={submit}>
      <span className="lib-tile__title">{action.title}</span>
      <span className="lib-muted">{action.description}</span>
      <code className="mods__code">{action.command}</code>
      {action.params.map((param) => (
        <TextField
          key={param.name}
          label={param.required ? param.label : `${param.label} (optional)`}
          value={values[param.name] ?? ""}
          onValueChange={(value) => setValues((current) => ({ ...current, [param.name]: value }))}
          placeholder={param.placeholder}
          mono
        />
      ))}
      {action.spends ? (
        <CheckboxField label={`I want to spend money on my ${adapter.provider} account`} checked={confirm} onCheckedChange={setConfirm} />
      ) : null}
      <Button type="submit" disabled={busy || adapter.status !== "connected" || (action.spends && !confirm)}>
        {busy ? "Running" : action.interactive ? "Open in CLI" : "Run"}
      </Button>
      {adapter.status !== "connected" ? <span className="lib-muted">Connect {adapter.name} first.</span> : null}
      {run ? (
        <div className={`studio-notice${run.status === "failed" || run.status === "refused" ? " studio-notice--err" : ""}`} role="status">
          <strong>{run.message}</strong>
          {run.output ? <pre className="mods__output">{run.output}</pre> : null}
        </div>
      ) : null}
      {error ? <ErrorNotice error={error} compact /> : null}
    </form>
  );
}

function CreateMod({ onCreated }: { onCreated: () => void }) {
  const navigate = useNavigate();
  const adapters = useRoute("adapters.list", "adapters");
  const augments = useRoute("augments.list", "augments");
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ForgeApiError>();
  const toggle = (id: string, on: boolean) =>
    setPicked((current) => {
      const nextSet = new Set(current);
      if (on) nextSet.add(id);
      else nextSet.delete(id);
      return nextSet;
    });
  const ready = name.trim().length > 0 && description.trim().length > 0;
  const chosen = useMemo(() => [...picked], [picked]);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!ready) return;
    setBusy(true);
    setError(undefined);
    try {
      const { mod } = await api.call("mods.create", {
        body: {
          name: name.trim(),
          description: description.trim(),
          adapters: chosen.filter((id) => id.startsWith("adapter:")),
          augments: chosen.filter((id) => id.startsWith("augment:")),
        },
      });
      onCreated();
      navigate(`/mods/${encodeURIComponent(mod.id)}`);
    } catch (failure) {
      setError(asApiError(failure, "mods.create"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="mods__detail" onSubmit={submit}>
      <header>
        <Eyebrow>MODS // NEW</Eyebrow>
        <h1 className="lib__title">Make a mod</h1>
        <p className="lib-muted">Pick the adapters and augments it composes. 4GE writes a mod folder with mod.json, .mcp.json and a Claude Code plugin manifest.</p>
      </header>
      <TextField label="Name" value={name} onValueChange={setName} placeholder="Shop launch" />
      <TextArea label="What it is for" value={description} onValueChange={setDescription} rows={3} />
      <fieldset className="mods__pick">
        <legend>Adapters</legend>
        {adapters.error ? <ErrorNotice error={adapters.error} compact onRetry={adapters.reload} /> : null}
        {adapters.data?.adapters.map((adapter) => (
          <CheckboxField
            key={adapter.id}
            label={`${adapter.name} (${ADAPTER_WORD[adapter.status].toLowerCase()})`}
            checked={picked.has(adapter.id)}
            onCheckedChange={(on) => toggle(adapter.id, on)}
          />
        ))}
      </fieldset>
      <fieldset className="mods__pick">
        <legend>Augments</legend>
        {augments.error ? <ErrorNotice error={augments.error} compact onRetry={augments.reload} /> : null}
        {augments.data?.augments.map((augment) => (
          <CheckboxField
            key={augment.id}
            label={`${augment.name} (${augment.kind})`}
            checked={picked.has(augment.id)}
            onCheckedChange={(on) => toggle(augment.id, on)}
          />
        ))}
      </fieldset>
      <div>
        <Button type="submit" variant="primary" disabled={!ready || busy}>
          {busy ? "Making" : "Make mod"}
        </Button>
      </div>
      {error ? <ErrorNotice error={error} /> : null}
    </form>
  );
}
