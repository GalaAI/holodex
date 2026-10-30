import assert from 'node:assert/strict';
import { test } from 'node:test';
import { SESSION_COOKIE, can, createApp } from './app.js';
import { openDb } from './db.js';

function setup() {
  const app = createApp(openDb(':memory:'), { secureCookies: false });
  const client = () => {
    let cookie = '';
    return async (method: string, path: string, body?: unknown) => {
      const response = await app.request(path, {
        method,
        headers: { 'content-type': 'application/json', ...(cookie ? { cookie } : {}) },
        ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      });
      const setCookie = response.headers.get('set-cookie');
      if (setCookie?.startsWith(`${SESSION_COOKIE}=`)) cookie = setCookie.split(';')[0] ?? '';
      return { status: response.status, body: (await response.json()) as Record<string, any> };
    };
  };
  return { client };
}

const PASSWORD = 'long enough password';

test('roles rank viewer, staff, manager, owner', () => {
  assert.equal(can('owner', 'manager'), true);
  assert.equal(can('staff', 'manager'), false);
  assert.equal(can('nobody', 'viewer'), false);
});

test('the owner assigns roles and each role is held to its rights', async () => {
  const { client } = setup();
  const owner = client();
  const staff = client();
  await owner('POST', '/api/auth/signup', { email: 'owner@example.com', password: PASSWORD });
  const joined = await staff('POST', '/api/auth/signup', { email: 'staff@example.com', password: PASSWORD });
  assert.equal(joined.body.user.role, 'viewer');

  const refused = await staff('POST', '/api/contacts', { name: 'Ada' });
  assert.equal(refused.status, 403);
  assert.match(refused.body.error.message, /Ask the owner/);

  assert.equal((await owner('PATCH', `/api/users/${joined.body.user.id}`, { role: 'staff' })).status, 200);
  const contact = await staff('POST', '/api/contacts', { name: 'Ada', company: 'Analytical' });
  assert.equal(contact.status, 201);
  assert.equal((await staff('DELETE', `/api/contacts/${contact.body.contact.id}`)).status, 403);
  assert.equal((await staff('GET', '/api/users')).status, 403);
});

test('deals move through the pipeline and only managers close them', async () => {
  const { client } = setup();
  const owner = client();
  const staff = client();
  await owner('POST', '/api/auth/signup', { email: 'owner@example.com', password: PASSWORD });
  const joined = await staff('POST', '/api/auth/signup', { email: 'staff@example.com', password: PASSWORD });
  await owner('PATCH', `/api/users/${joined.body.user.id}`, { role: 'staff' });

  const contact = await staff('POST', '/api/contacts', { name: 'Grace' });
  const deal = await staff('POST', '/api/deals', { title: 'Annual plan', valueCents: 120000, contactId: contact.body.contact.id });
  assert.equal(deal.status, 201);
  const id = deal.body.deal.id;
  assert.equal((await staff('PATCH', `/api/deals/${id}/stage`, { stage: 'proposal' })).status, 200);
  assert.equal((await staff('PATCH', `/api/deals/${id}/stage`, { stage: 'won' })).status, 403);
  assert.equal((await owner('PATCH', `/api/deals/${id}/stage`, { stage: 'won' })).status, 200);
  assert.equal((await owner('PATCH', `/api/deals/${id}/stage`, { stage: 'sideways' })).status, 400);

  const pipeline = await owner('GET', '/api/pipeline');
  const won = pipeline.body.stages.find((stage: { stage: string }) => stage.stage === 'won');
  assert.equal(won.totalCents, 120000);
  assert.equal(won.deals[0].contactName, 'Grace');
  const activity = await owner('GET', '/api/activity');
  assert.ok(activity.body.activity.length >= 4);
});
