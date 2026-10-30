import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError, type Item, type User } from './api';
import brand from './brand.json';

export function App() {
  const [user, setUser] = useState<User | null | undefined>(undefined);

  useEffect(() => {
    api
      .me()
      .then(({ user: current }) => setUser(current))
      .catch(() => setUser(null));
  }, []);

  if (user === undefined) return <main className="center">Loading</main>;
  return (
    <div className="shell">
      <header className="top">
        <span className="brand">{brand.name}</span>
        {user && (
          <span className="who">
            {user.email}
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
        )}
      </header>
      {user ? <Items /> : <SignIn onSignedIn={setUser} />}
    </div>
  );
}

function SignIn({ onSignedIn }: { onSignedIn: (user: User) => void }) {
  const [mode, setMode] = useState<'login' | 'signup'>('login');
  const [error, setError] = useState('');

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const email = String(form.get('email') ?? '');
    const password = String(form.get('password') ?? '');
    try {
      const { user } = mode === 'login' ? await api.login(email, password) : await api.signup(email, password);
      onSignedIn(user);
    } catch (problem) {
      setError(problem instanceof ApiError ? problem.message : 'Could not reach the server.');
    }
  }

  return (
    <main className="panel">
      <p className="eyebrow">// {mode === 'login' ? 'Sign in' : 'Create account'}</p>
      <h1>{brand.tagline}</h1>
      <form onSubmit={submit} className="stack">
        <label>
          Email
          <input name="email" type="email" autoComplete="email" required />
        </label>
        <label>
          Password
          <input
            name="password"
            type="password"
            autoComplete={mode === 'login' ? 'current-password' : 'new-password'}
            minLength={10}
            required
          />
        </label>
        {error && <p className="error" role="alert">{error}</p>}
        <button type="submit" className="primary">
          {mode === 'login' ? 'Sign in' : 'Create account'}
        </button>
      </form>
      <button type="button" className="link" onClick={() => setMode(mode === 'login' ? 'signup' : 'login')}>
        {mode === 'login' ? 'New here? Create an account' : 'Have an account? Sign in'}
      </button>
    </main>
  );
}

function Items() {
  const [items, setItems] = useState<Item[]>([]);
  const [title, setTitle] = useState('');
  const [error, setError] = useState('');

  useEffect(() => {
    api
      .items()
      .then(({ items: loaded }) => setItems(loaded))
      .catch((problem: unknown) => setError(problem instanceof Error ? problem.message : 'Could not load items.'));
  }, []);

  async function add(event: FormEvent) {
    event.preventDefault();
    try {
      const { item } = await api.addItem(title);
      setItems((current) => [item, ...current]);
      setTitle('');
      setError('');
    } catch (problem) {
      setError(problem instanceof Error ? problem.message : 'Could not add that.');
    }
  }

  return (
    <main className="panel wide">
      <p className="eyebrow">// Your items</p>
      <form onSubmit={add} className="row">
        <input aria-label="New item" value={title} onChange={(event) => setTitle(event.target.value)} placeholder="Add an item" />
        <button type="submit" className="primary">Add</button>
      </form>
      {error && <p className="error" role="alert">{error}</p>}
      {items.length === 0 ? (
        <p className="muted">Nothing yet. Add your first item above.</p>
      ) : (
        <ul className="list">
          {items.map((item) => (
            <li key={item.id}>
              <label>
                <input
                  type="checkbox"
                  checked={item.done}
                  onChange={async (event) => {
                    const done = event.target.checked;
                    await api.setDone(item.id, done);
                    setItems((current) => current.map((entry) => (entry.id === item.id ? { ...entry, done } : entry)));
                  }}
                />
                <span className={item.done ? 'done' : ''}>{item.title}</span>
              </label>
              <button
                type="button"
                className="link"
                onClick={async () => {
                  await api.removeItem(item.id);
                  setItems((current) => current.filter((entry) => entry.id !== item.id));
                }}
              >
                Remove
              </button>
            </li>
          ))}
        </ul>
      )}
    </main>
  );
}
