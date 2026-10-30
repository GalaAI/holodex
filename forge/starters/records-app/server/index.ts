import { existsSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { serve } from '@hono/node-server';
import { serveStatic } from '@hono/node-server/serve-static';
import { createApp } from './app.js';
import { openDb } from './db.js';

/**
 * Production entry: the API plus the built client from dist/client.
 * PORT (default 3000), HOST (default 127.0.0.1) and DATABASE_PATH
 * (default ./data/app.db) come from the environment.
 */
const port = Number(process.env.PORT ?? 3000);
const host = process.env.HOST ?? '127.0.0.1';
const db = openDb(resolve(process.env.DATABASE_PATH ?? 'data/app.db'));
const app = createApp(db);

const clientDir = resolve('dist/client');
const indexHtml = join(clientDir, 'index.html');
if (existsSync(indexHtml)) {
  app.use('/*', serveStatic({ root: 'dist/client' }));
  app.get('*', (c) => c.html(readFileSync(indexHtml, 'utf8')));
}

serve({ fetch: app.fetch, port, hostname: host }, (info) => {
  console.log(`Listening on http://${host}:${info.port}`);
});
