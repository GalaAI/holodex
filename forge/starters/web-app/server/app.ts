import { Hono, type Context } from 'hono';
import { deleteCookie, getCookie, setCookie } from 'hono/cookie';
import {
  authenticate,
  checkCredentials,
  createUser,
  endSession,
  startSession,
  userForSession,
  type User,
} from './auth.js';
import type { Db } from './db.js';

/**
 * The API: accounts (sign up, sign in, sign out, who am I) and one real
 * resource, items, scoped to the signed-in user. Every error is one JSON
 * shape with plain words: { error: { message } }.
 */
export const SESSION_COOKIE = 'app_session';

type Env = { Variables: { user: User } };

function fail(c: Context, status: 400 | 401 | 404 | 409, message: string) {
  return c.json({ error: { message } }, status);
}

async function readJson(c: Context): Promise<Record<string, unknown>> {
  try {
    const body: unknown = await c.req.json();
    return body && typeof body === 'object' ? (body as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}

export function createApp(db: Db, options: { secureCookies?: boolean } = {}) {
  const app = new Hono<Env>();
  const secure = options.secureCookies ?? process.env.NODE_ENV === 'production';

  const signIn = (c: Context, user: User) => {
    const session = startSession(db, user.id);
    setCookie(c, SESSION_COOKIE, session.token, {
      httpOnly: true,
      sameSite: 'Lax',
      secure,
      path: '/',
      expires: session.expires,
    });
    return c.json({ user });
  };

  app.get('/api/health', (c) => c.json({ ok: true }));

  app.post('/api/auth/signup', async (c) => {
    const body = await readJson(c);
    const problem = checkCredentials(body.email, body.password);
    if (problem) return fail(c, 400, problem);
    const user = await createUser(db, String(body.email), String(body.password));
    if (!user) return fail(c, 409, 'That email already has an account. Sign in instead.');
    return signIn(c, user);
  });

  app.post('/api/auth/login', async (c) => {
    const body = await readJson(c);
    if (typeof body.email !== 'string' || typeof body.password !== 'string') {
      return fail(c, 400, 'Enter your email and password.');
    }
    const user = await authenticate(db, body.email, body.password);
    if (!user) return fail(c, 401, 'That email and password do not match.');
    return signIn(c, user);
  });

  app.post('/api/auth/logout', (c) => {
    endSession(db, getCookie(c, SESSION_COOKIE));
    deleteCookie(c, SESSION_COOKIE, { path: '/' });
    return c.json({ ok: true });
  });

  // Everything below needs a signed-in user.
  app.use('/api/me', requireUser);
  app.use('/api/items', requireUser);
  app.use('/api/items/*', requireUser);

  async function requireUser(c: Context<Env>, next: () => Promise<void>) {
    const user = userForSession(db, getCookie(c, SESSION_COOKIE));
    if (!user) return fail(c, 401, 'Sign in to continue.');
    c.set('user', user);
    await next();
    return undefined;
  }

  app.get('/api/me', (c) => c.json({ user: c.get('user') }));

  app.get('/api/items', (c) => {
    const items = db
      .prepare('SELECT id, title, done, created_at AS createdAt FROM items WHERE user_id = ? ORDER BY id DESC')
      .all(c.get('user').id)
      .map((row) => ({ ...row, done: Boolean((row as { done: number }).done) }));
    return c.json({ items });
  });

  app.post('/api/items', async (c) => {
    const body = await readJson(c);
    const title = typeof body.title === 'string' ? body.title.trim() : '';
    if (title.length === 0 || title.length > 200) return fail(c, 400, 'Give the item a title of up to 200 characters.');
    const createdAt = new Date().toISOString();
    const result = db
      .prepare('INSERT INTO items (user_id, title, created_at) VALUES (?, ?, ?)')
      .run(c.get('user').id, title, createdAt);
    return c.json({ item: { id: Number(result.lastInsertRowid), title, done: false, createdAt } }, 201);
  });

  app.patch('/api/items/:id', async (c) => {
    const body = await readJson(c);
    const result = db
      .prepare('UPDATE items SET done = ? WHERE id = ? AND user_id = ?')
      .run(body.done ? 1 : 0, Number(c.req.param('id')), c.get('user').id);
    if (result.changes === 0) return fail(c, 404, 'That item was not found.');
    return c.json({ ok: true });
  });

  app.delete('/api/items/:id', (c) => {
    const result = db
      .prepare('DELETE FROM items WHERE id = ? AND user_id = ?')
      .run(Number(c.req.param('id')), c.get('user').id);
    if (result.changes === 0) return fail(c, 404, 'That item was not found.');
    return c.json({ ok: true });
  });

  app.all('/api/*', (c) => fail(c, 404, `There is no API route ${c.req.method} ${c.req.path}.`));

  return app;
}
