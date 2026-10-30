import { LIBRARY_GROUPS, type LibraryGroup, type LibraryItem } from "@4ge/contracts";
import { Button, Eyebrow, Led, TextField, type LedState } from "@4ge/kit";
import {
  BookOpen,
  Cable,
  ExternalLink,
  GitBranch,
  Hammer,
  Megaphone,
  MessageSquareText,
  Monitor,
  Plug,
  Plus,
  Puzzle,
  Rocket,
  Server,
  Wrench,
  X,
  type LucideIcon,
} from "lucide-react";
import { useState, type FormEvent } from "react";
import { useNavigate } from "react-router";
import { ForgeApiError, api, useRoute } from "../../lib/api.js";
import { ErrorNotice } from "../../shell/ui.js";
import { SmithPanel, useSmithSession } from "./SmithPanel.js";
import "./library.css";

/**
 * The Library (SPEC "Library placement"): every item grouped by origin, in
 * order: My projects, Repos, Mods (with the plus tile), Augments, Adapters.
 * Clicking an item opens the matching Smith session; a build opens in the
 * IDE. Everything shown comes from GET /library.
 */

const ICONS: Record<string, LucideIcon> = {
  Hammer,
  Megaphone,
  GitBranch,
  Rocket,
  Monitor,
  Wrench,
  Puzzle,
  Server,
  BookOpen,
  MessageSquareText,
  Plug,
  Cable,
};

const TONE_LED: Record<LibraryItem["tone"], LedState | undefined> = {
  live: "live",
  ok: "ok",
  warn: "warn",
  err: "err",
  rest: undefined,
};

export default function Library() {
  const library = useRoute("library.list", "library");
  const navigate = useNavigate();
  const smith = useSmithSession();
  const [refreshing, setRefreshing] = useState(false);

  const openItem = (item: LibraryItem) => {
    if (item.open.subject.type === "build") {
      navigate(`/ide/${item.open.subject.id}`);
      return;
    }
    void smith.open(item.open);
  };

  const refresh = async () => {
    setRefreshing(true);
    try {
      await api.call("adapters.list", { query: { refresh: "true" } });
    } catch {
      // The Library reload below shows any error in plain words.
    } finally {
      setRefreshing(false);
      library.reload();
    }
  };

  return (
    <section className="lib" data-testid="feature-library">
      <div className="lib__main">
        <header className="lib__head">
          <div>
            <Eyebrow>4GE // LIBRARY</Eyebrow>
            <h1 className="lib__title">Library</h1>
            <p className="lib-muted">
              {library.data ? `${library.data.total} items, grouped by where they come from.` : "Your apps, repos, mods, augments and adapters."}
            </p>
          </div>
          <Button onClick={refresh} disabled={refreshing}>
            {refreshing ? "Checking" : "Check connections"}
          </Button>
        </header>
        {library.error ? <ErrorNotice error={library.error} onRetry={library.reload} /> : null}
        {library.loading && !library.data ? <p className="lib-muted">Reading your Library. The first check of your CLIs can take a few seconds.</p> : null}
        {library.data?.groups.map((group) => (
          <Group
            key={group.origin}
            group={group}
            onOpen={openItem}
            onCreateMod={() => navigate("/mods?create=1")}
            onChanged={library.reload}
          />
        ))}
      </div>
      <SmithPanel smith={smith} />
    </section>
  );
}

function Group({
  group,
  onOpen,
  onCreateMod,
  onChanged,
}: {
  group: LibraryGroup;
  onOpen: (item: LibraryItem) => void;
  onCreateMod: () => void;
  onChanged: () => void;
}) {
  return (
    <section className="lib-group" aria-labelledby={`lib-group-${group.origin}`} data-origin={group.origin}>
      <div className="lib-group__head">
        <h2 id={`lib-group-${group.origin}`} className="lib-group__label">
          {group.label}
          <span className="lib-group__count">{group.items.length}</span>
        </h2>
        <p className="lib-muted">{LIBRARY_GROUPS[group.origin].holds}</p>
      </div>
      <ul className="lib-grid">
        {group.items.map((item) => (
          <li key={item.id}>
            <Tile item={item} onOpen={onOpen} onChanged={onChanged} />
          </li>
        ))}
        {group.canCreate ? (
          <li>
            <button type="button" className="lib-tile lib-tile--plus" onClick={onCreateMod} aria-label="Make a mod">
              <Plus aria-hidden="true" size={22} />
              <span className="lib-tile__title">Make a mod</span>
              <span className="lib-tile__sub">From your adapters and augments</span>
            </button>
          </li>
        ) : null}
      </ul>
      {group.origin === "repo" ? <AddRepo onAdded={onChanged} empty={group.items.length === 0} /> : null}
    </section>
  );
}

function Tile({ item, onOpen, onChanged }: { item: LibraryItem; onOpen: (item: LibraryItem) => void; onChanged: () => void }) {
  const Icon = (item.icon ? ICONS[item.icon] : undefined) ?? Puzzle;
  const led = TONE_LED[item.tone];
  const [removing, setRemoving] = useState(false);
  const remove = async () => {
    setRemoving(true);
    try {
      await api.call("library.repos.remove", { params: { id: item.id } });
    } finally {
      setRemoving(false);
      onChanged();
    }
  };
  return (
    <div className="lib-tile" data-tone={item.tone}>
      <button type="button" className="lib-tile__open" onClick={() => onOpen(item)} title={`Open ${item.title} with Smith`}>
        <span className="lib-tile__icon">
          <Icon aria-hidden="true" size={20} />
        </span>
        <span className="lib-tile__title">{item.title}</span>
        {item.subtitle ? <span className="lib-tile__sub">{item.subtitle}</span> : null}
        {item.status ? (
          <span className="lib-tile__status">{led ? <Led state={led}>{item.status}</Led> : item.status}</span>
        ) : null}
      </button>
      <span className="lib-tile__links">
        {item.url ? (
          <a className="lib-tile__link" href={item.url} target="_blank" rel="noreferrer" aria-label={`Open ${item.title} in the browser`}>
            <ExternalLink aria-hidden="true" size={14} />
          </a>
        ) : null}
        {item.origin === "repo" ? (
          <button type="button" className="lib-tile__link" onClick={remove} disabled={removing} aria-label={`Remove ${item.title}`}>
            <X aria-hidden="true" size={14} />
          </button>
        ) : null}
      </span>
    </div>
  );
}

function AddRepo({ onAdded, empty }: { onAdded: () => void; empty: boolean }) {
  const [url, setUrl] = useState("");
  const [error, setError] = useState<ForgeApiError>();
  const [busy, setBusy] = useState(false);
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!url.trim()) return;
    setBusy(true);
    setError(undefined);
    try {
      await api.call("library.repos.add", { body: { url: url.trim() } });
      setUrl("");
      onAdded();
    } catch (failure) {
      if (failure instanceof ForgeApiError) setError(failure);
    } finally {
      setBusy(false);
    }
  };
  return (
    <form className="lib-repo-form" onSubmit={submit}>
      <TextField
        label={empty ? "Add your first repo shortcut" : "Add a repo shortcut"}
        value={url}
        onValueChange={setUrl}
        placeholder="https://github.com/owner/repo"
        mono
      />
      <Button type="submit" disabled={busy || url.trim().length === 0}>
        Add repo
      </Button>
      {error ? <ErrorNotice error={error} compact /> : null}
    </form>
  );
}
