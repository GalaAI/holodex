import assert from 'node:assert/strict';
import { test } from 'node:test';
import { SESSION_COOKIE, createApp } from './app.js';
import { openDb } from './db.js';

function setup() {
  const app = createApp(openDb(':memory:'), { secureCookies: false });
  let cookie = '';
  const call = async (method: string, path: string, body?: unknown) => {
    const response = await app.request(path, {
      method,
      headers: { 'content-type': 'application/json', ...(cookie ? { cookie } : {}) },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });
    const setCookie = response.headers.get('set-cookie');
    if (setCookie?.startsWith(`${SESSION_COOKIE}=`)) cookie = setCookie.split(';')[0] ?? '';
    return { status: response.status, body: (await response.json()) as Record<string, any> };
  };
  return { call };
}

test('sign up makes the first account the owner and signs it in', async () => {
  const { call } = setup();
  const signup = await call('POST', '/api/auth/signup', { email: 'Owner@Example.com', password: 'correct horse battery' });
  assert.equal(signup.status, 200);
  assert.equal(signup.body.user.role, 'owner');
  const me = await call('GET', '/api/me');
  assert.equal(me.body.user.email, 'owner@example.com');
});

test('refuses weak passwords, duplicate emails and wrong passwords in plain words', async () => {
  const { call } = setup();
  assert.equal((await call('POST', '/api/auth/signup', { email: 'a@b.co', password: 'short' })).status, 400);
  await call('POST', '/api/auth/signup', { email: 'a@b.co', password: 'long enough password' });
  const again = await call('POST', '/api/auth/signup', { email: 'a@b.co', password: 'long enough password' });
  assert.equal(again.status, 409);
  await call('POST', '/api/auth/logout');
  const wrong = await call('POST', '/api/auth/login', { email: 'a@b.co', password: 'not the password' });
  assert.equal(wrong.status, 401);
  assert.match(wrong.body.error.message, /do not match/);
});

test('items need a session and stay scoped to their owner', async () => {
  const { call } = setup();
  assert.equal((await call('GET', '/api/items')).status, 401);
  await call('POST', '/api/auth/signup', { email: 'me@example.com', password: 'long enough password' });
  const created = await call('POST', '/api/items', { title: 'First thing' });
  assert.equal(created.status, 201);
  const id = created.body.item.id;
  assert.equal((await call('PATCH', `/api/items/${id}`, { done: true })).status, 200);
  const list = await call('GET', '/api/items');
  assert.deepEqual(list.body.items.map((item: { title: string; done: boolean }) => [item.title, item.done]), [['First thing', true]]);
  await call('POST', '/api/auth/logout');
  await call('POST', '/api/auth/signup', { email: 'other@example.com', password: 'long enough password' });
  assert.equal((await call('GET', '/api/items')).body.items.length, 0);
  assert.equal((await call('DELETE', `/api/items/${id}`)).status, 404);
});
