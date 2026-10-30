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
import { ROLES, STAGES, type Db, type Role, type Stage } from './db.js';

/**
 * The records API: accounts with roles, contacts, and deals moving through a
 * pipeline. Roles, lowest to highest: viewer reads; staff adds and edits;
 * manager also deletes and closes deals as won or lost; owner also assigns
 * roles. Every write lands in the activity log. Errors are one JSON shape
 * with plain words: { error: { message } }.
 */
export const SESSION_COOKIE = 'records_session';

const RANK: Record<Role, number> = { viewer: 0, staff: 1, manager: 2, owner: 3 };

export function can(role: string, needed: Role): boolean {
  return (RANK[role as Role] ?? -1) >= RANK[needed];
}

type Env = { Variables: { user: User } };

function fail(c: Context, status: 400 | 401 | 403 | 404 | 409, message: string) {
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

const text = (value: unknown, max: number) => (typeof value === 'string' ? value.trim().slice(0, max) : '');

export function createApp(db: Db, options: { secureCookies?: boolean } = {}) {
  const app = new Hono<Env>();
  const secure = options.secureCookies ?? process.env.NODE_ENV === 'production';

  const log = (user: User, action: string, subject: string) =>
    db.prepare('INSERT INTO activity (user_id, action, subject, at) VALUES (?, ?, ?, ?)').run(
      user.id,
      action,
      subject,
      new Date().toISOString(),
    );

  const signIn = (c: Context, user: User) => {
    const session = startSession(db, user.id);
    setCookie(c, SESSION_COOKIE, session.token, { httpOnly: true, sameSite: 'Lax', secure, path: '/', expires: session.expires });
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
    if (typeof body.email !== 'string' || typeof body.password !== 'string') return fail(c, 400, 'Enter your email and password.');
    const user = await authenticate(db, body.email, body.password);
    if (!user) return fail(c, 401, 'That email and password do not match.');
    return signIn(c, user);
  });

  app.post('/api/auth/logout', (c) => {
    endSession(db, getCookie(c, SESSION_COOKIE));
    deleteCookie(c, SESSION_COOKIE, { path: '/' });
    return c.json({ ok: true });
  });

  /** Signed in with at least `role`, or a plain-words refusal. */
  const need = (role: Role) => async (c: Context<Env>, next: () => Promise<void>) => {
    const user = userForSession(db, getCookie(c, SESSION_COOKIE));
    if (!user) return fail(c, 401, 'Sign in to continue.');
    if (!can(user.role, role)) return fail(c, 403, `Your role (${user.role}) cannot do that. Ask the owner for ${role} access.`);
    c.set('user', user);
    await next();
    return undefined;
  };

  app.get('/api/me', need('viewer'), (c) => c.json({ user: c.get('user') }));

  /* Users and roles */
  app.get('/api/users', need('owner'), (c) =>
    c.json({ users: db.prepare('SELECT id, email, role, created_at AS createdAt FROM users ORDER BY id').all() }),
  );
  app.patch('/api/users/:id', need('owner'), async (c) => {
    const body = await readJson(c);
    const role = body.role as Role;
    if (!ROLES.includes(role)) return fail(c, 400, `Pick one of these roles: ${ROLES.join(', ')}.`);
    const id = Number(c.req.param('id'));
    if (id === c.get('user').id && role !== 'owner') return fail(c, 400, 'You cannot step down from owner yourself.');
    const result = db.prepare('UPDATE users SET role = ? WHERE id = ?').run(role, id);
    if (result.changes === 0) return fail(c, 404, 'That person was not found.');
    log(c.get('user'), 'role.set', `user ${id} to ${role}`);
    return c.json({ ok: true });
  });

  /* Contacts */
  app.get('/api/contacts', need('viewer'), (c) => {
    const query = `%${text(c.req.query('q'), 80)}%`;
    const contacts = db
      .prepare(
        `SELECT id, name, company, email, phone, notes, created_at AS createdAt FROM contacts
         WHERE name LIKE ? OR company LIKE ? OR email LIKE ? ORDER BY name COLLATE NOCASE`,
      )
      .all(query, query, query);
    return c.json({ contacts });
  });

  app.post('/api/contacts', need('staff'), async (c) => {
    const body = await readJson(c);
    const name = text(body.name, 120);
    if (!name) return fail(c, 400, 'Give the contact a name.');
    const row = { company: text(body.company, 120), email: text(body.email, 200), phone: text(body.phone, 40), notes: text(body.notes, 2000) };
    const result = db
      .prepare('INSERT INTO contacts (name, company, email, phone, notes, created_by, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)')
      .run(name, row.company, row.email, row.phone, row.notes, c.get('user').id, new Date().toISOString());
    log(c.get('user'), 'contact.add', name);
    return c.json({ contact: { id: Number(result.lastInsertRowid), name, ...row } }, 201);
  });

  app.delete('/api/contacts/:id', need('manager'), (c) => {
    const result = db.prepare('DELETE FROM contacts WHERE id = ?').run(Number(c.req.param('id')));
    if (result.changes === 0) return fail(c, 404, 'That contact was not found.');
    log(c.get('user'), 'contact.delete', `contact ${String(c.req.param('id'))}`);
    return c.json({ ok: true });
  });

  /* Deals and the pipeline */
  app.get('/api/pipeline', need('viewer'), (c) => {
    const deals = db
      .prepare(
        `SELECT deals.id AS id, deals.title AS title, deals.value_cents AS valueCents, deals.stage AS stage,
                deals.contact_id AS contactId, contacts.name AS contactName, deals.updated_at AS updatedAt
         FROM deals LEFT JOIN contacts ON contacts.id = deals.contact_id ORDER BY deals.updated_at DESC`,
      )
      .all() as Array<{ stage: Stage; valueCents: number }>;
    const stages = STAGES.map((stage) => {
      const inStage = deals.filter((deal) => deal.stage === stage);
      return { stage, deals: inStage, totalCents: inStage.reduce((sum, deal) => sum + deal.valueCents, 0) };
    });
    return c.json({ stages });
  });

  app.post('/api/deals', need('staff'), async (c) => {
    const body = await readJson(c);
    const title = text(body.title, 160);
    if (!title) return fail(c, 400, 'Give the deal a title.');
    const valueCents = Math.max(0, Math.round(Number(body.valueCents ?? 0)) || 0);
    const contactId = body.contactId === undefined || body.contactId === null ? null : Number(body.contactId);
    if (contactId !== null && !db.prepare('SELECT id FROM contacts WHERE id = ?').get(contactId)) {
      return fail(c, 400, 'That contact does not exist.');
    }
    const result = db
      .prepare('INSERT INTO deals (title, contact_id, value_cents, stage, owner_id, updated_at) VALUES (?, ?, ?, ?, ?, ?)')
      .run(title, contactId, valueCents, 'lead', c.get('user').id, new Date().toISOString());
    log(c.get('user'), 'deal.add', title);
    return c.json({ deal: { id: Number(result.lastInsertRowid), title, valueCents, stage: 'lead', contactId } }, 201);
  });

  app.patch('/api/deals/:id/stage', need('staff'), async (c) => {
    const body = await readJson(c);
    const stage = body.stage as Stage;
    if (!STAGES.includes(stage)) return fail(c, 400, `Pick one of these stages: ${STAGES.join(', ')}.`);
    if ((stage === 'won' || stage === 'lost') && !can(c.get('user').role, 'manager')) {
      return fail(c, 403, 'Only a manager or the owner can close a deal as won or lost.');
    }
    const id = Number(c.req.param('id'));
    const result = db.prepare('UPDATE deals SET stage = ?, updated_at = ? WHERE id = ?').run(stage, new Date().toISOString(), id);
    if (result.changes === 0) return fail(c, 404, 'That deal was not found.');
    log(c.get('user'), 'deal.stage', `deal ${id} to ${stage}`);
    return c.json({ ok: true });
  });

  app.get('/api/activity', need('manager'), (c) =>
    c.json({
      activity: db
        .prepare(
          `SELECT activity.id AS id, users.email AS who, activity.action AS action, activity.subject AS subject, activity.at AS at
           FROM activity LEFT JOIN users ON users.id = activity.user_id ORDER BY activity.id DESC LIMIT 100`,
        )
        .all(),
    }),
  );

  app.all('/api/*', (c) => fail(c, 404, `There is no API route ${c.req.method} ${c.req.path}.`));

  return app;
}
