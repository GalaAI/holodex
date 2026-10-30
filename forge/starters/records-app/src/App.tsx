import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { api, ApiError, can, money, STAGES, type Contact, type Role, type StageColumn, type User } from './api';
import brand from './brand.json';

type Tab = 'pipeline' | 'contacts' | 'team';

export function App() {
  const [user, setUser] = useState<User | null | undefined>(undefined);
  const [tab, setTab] = useState<Tab>('pipeline');

  useEffect(() => {
    api
      .me()
      .then(({ user: current }) => setUser(current))
      .catch(() => setUser(null));
  }, []);

  if (user === undefined) return <main className="center">Loading</main>;
  if (!user) return <SignIn onSignedIn={setUser} />;
  return (
    <div className="shell">
      <header className="top">
        <span className="brand">{brand.name}</span>
        <nav aria-label="Sections">
          {(['pipeline', 'contacts', ...(can(user.role, 'owner') ? ['team' as const] : [])] as Tab[]).map((name) => (
            <button key={name} type="button" className={tab === name ? 'tab on' : 'tab'} onClick={() => setTab(name)}>
              {name[0]?.toUpperCase()}
              {name.slice(1)}
            </button>
          ))}
        </nav>
        <span className="who">
          {user.email} <span className="chip">{user.role}</span>
          <button
            type="button"
            className="link"
            onClick={async () => {
              await api.logout();
              setUser(null);
            }}
          >
            Sign out
          </button>
        </span>
      </header>
      {tab === 'pipeline' && <Pipeline user={user} />}
      {tab === 'contacts' && <Contacts user={user} />}
      {tab === 'team' && <Team user={user} />}
    </div>
  );
}

function useError() {
  const [error, setError] = useState('');
  const run = useCallback(async (work: () => Promise<void>) => {
    try {
      await work();
      setError('');
    } catch (problem) {
      setError(problem instanceof ApiError ? problem.message : 'Could not reach the server.');
    }
  }, []);
  return { error, run };
}

function Pipeline({ user }: { user: User }) {
  const [stages, setStages] = useState<StageColumn[]>([]);
  const [contacts, setContacts] = useState<Contact[]>([]);
  const { error, run } = useError();
  const load = useCallback(
    () =>
      run(async () => {
        setStages((await api.pipeline()).stages);
        setContacts((await api.contacts()).contacts);
      }),
    [run],
  );
  useEffect(() => {
    void load();
  }, [load]);

  async function add(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const contact = String(form.get('contact') ?? '');
    await run(async () => {
      await api.addDeal(String(form.get('title') ?? ''), Math.round(Number(form.get('value') ?? 0) * 100), contact ? Number(contact) : null);
    });
    event.currentTarget.reset();
    await load();
  }

  return (
    <main className="page">
      <p className="eyebrow">// Pipeline</p>
      {can(user.role, 'staff') && (
        <form className="row" onSubmit={add}>
          <input name="title" aria-label="Deal title" placeholder="New deal" required />
          <input name="value" aria-label="Value" type="number" min="0" step="1" placeholder="Value" />
          <select name="contact" aria-label="Contact" defaultValue="">
            <option value="">No contact</option>
            {contacts.map((contact) => (
              <option key={contact.id} value={contact.id}>
                {contact.name}
              </option>
            ))}
          </select>
          <button type="submit" className="primary">Add deal</button>
        </form>
      )}
      {error && <p className="error" role="alert">{error}</p>}
      <div className="board">
        {stages.map((column) => (
          <section key={column.stage} className="column" aria-label={column.stage}>
            <h2>
              {column.stage} <small>{money(column.totalCents)}</small>
            </h2>
            {column.deals.length === 0 && <p className="muted">No deals</p>}
            {column.deals.map((deal) => (
              <article key={deal.id} className="card">
                <strong>{deal.title}</strong>
                <span className="muted">
                  {deal.contactName ?? 'No contact'} &middot; {money(deal.valueCents)}
                </span>
                {can(user.role, 'staff') && (
                  <select
                    aria-label={`Move ${deal.title}`}
                    value={deal.stage}
                    onChange={(event) =>
                      run(async () => {
                        await api.moveDeal(deal.id, event.target.value as typeof deal.stage);
                        await load();
                      })
                    }
                  >
                    {STAGES.map((stage) => (
                      <option key={stage} value={stage}>
                        {stage}
                      </option>
                    ))}
                  </select>
                )}
              </article>
            ))}
          </section>
        ))}
      </div>
    </main>
  );
}

function Contacts({ user }: { user: User }) {
  const [contacts, setContacts] = useState<Contact[]>([]);
  const [query, setQuery] = useState('');
  const { error, run } = useError();
  const load = useCallback(() => run(async () => setContacts((await api.contacts(query)).contacts)), [run, query]);
  useEffect(() => {
    void load();
  }, [load]);

  async function add(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    await run(async () => {
      await api.addContact({
        name: String(form.get('name') ?? ''),
        company: String(form.get('company') ?? ''),
        email: String(form.get('email') ?? ''),
      });
    });
    event.currentTarget.reset();
    await load();
  }

  return (
    <main className="page">
      <p className="eyebrow">// Contacts</p>
      <input className="search" aria-label="Search contacts" placeholder="Search" value={query} onChange={(event) => setQuery(event.target.value)} />
      {can(user.role, 'staff') && (
        <form className="row" onSubmit={add}>
          <input name="name" aria-label="Name" placeholder="Name" required />
          <input name="company" aria-label="Company" placeholder="Company" />
          <input name="email" aria-label="Email" type="email" placeholder="Email" />
          <button type="submit" className="primary">Add contact</button>
        </form>
      )}
      {error && <p className="error" role="alert">{error}</p>}
      <table className="table">
        <thead>
          <tr>
            <th>Name</th>
            <th>Company</th>
            <th>Email</th>
          </tr>
        </thead>
        <tbody>
          {contacts.map((contact) => (
            <tr key={contact.id}>
              <td>{contact.name}</td>
              <td>{contact.company}</td>
              <td>{contact.email}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {contacts.length === 0 && <p className="muted">No contacts yet.</p>}
    </main>
  );
}

function Team({ user }: { user: User }) {
  const [users, setUsers] = useState<User[]>([]);
  const { error, run } = useError();
  const load = useCallback(() => run(async () => setUsers((await api.users()).users)), [run]);
  useEffect(() => {
    void load();
  }, [load]);
  return (
    <main className="page">
      <p className="eyebrow">// Team and roles</p>
      {error && <p className="error" role="alert">{error}</p>}
      <table className="table">
        <tbody>
          {users.map((member) => (
            <tr key={member.id}>
              <td>{member.email}</td>
              <td>
                <select
                  aria-label={`Role for ${member.email}`}
                  value={member.role}
                  disabled={member.id === user.id}
                  onChange={(event) =>
                    run(async () => {
                      await api.setRole(member.id, event.target.value as Role);
                      await load();
                    })
                  }
                >
                  {(['owner', 'manager', 'staff', 'viewer'] as Role[]).map((role) => (
                    <option key={role} value={role}>
                      {role}
                    </option>
                  ))}
                </select>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </main>
  );
}

function SignIn({ onSignedIn }: { onSignedIn: (user: User) => void }) {
  const [mode, setMode] = useState<'login' | 'signup'>('login');
  const { error, run } = useError();

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const email = String(form.get('email') ?? '');
    const password = String(form.get('password') ?? '');
    await run(async () => {
      const { user } = mode === 'login' ? await api.login(email, password) : await api.signup(email, password);
      onSignedIn(user);
    });
  }

  return (
    <main className="panel">
      <p className="eyebrow">// {brand.name}</p>
      <h1>{brand.tagline}</h1>
      <form onSubmit={submit} className="stack">
        <label>
          Email
          <input name="email" type="email" autoComplete="email" required />
        </label>
        <label>
          Password
          <input name="password" type="password" autoComplete={mode === 'login' ? 'current-password' : 'new-password'} minLength={10} required />
        </label>
        {error && <p className="error" role="alert">{error}</p>}
        <button type="submit" className="primary">{mode === 'login' ? 'Sign in' : 'Create account'}</button>
      </form>
      <button type="button" className="link" onClick={() => setMode(mode === 'login' ? 'signup' : 'login')}>
        {mode === 'login' ? 'New here? Create an account' : 'Have an account? Sign in'}
      </button>
    </main>
  );
}
