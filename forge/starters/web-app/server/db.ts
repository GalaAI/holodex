import { mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { DatabaseSync } from 'node:sqlite';

/**
 * The app's database: SQLite through Node's built-in node:sqlite, so the
 * starter needs no database server. Swap for Postgres (for example a
 * Supabase project) when the app outgrows one machine; the queries live
 * here and in the route handlers only.
 */
export type Db = DatabaseSync;

const MIGRATIONS: string[] = [
  `CREATE TABLE IF NOT EXISTS users (
     id INTEGER PRIMARY KEY AUTOINCREMENT,
     email TEXT NOT NULL UNIQUE,
     password_hash TEXT NOT NULL,
     role TEXT NOT NULL DEFAULT 'member',
     created_at TEXT NOT NULL
   )`,
  `CREATE TABLE IF NOT EXISTS sessions (
     token_hash TEXT PRIMARY KEY,
     user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
     expires_at TEXT NOT NULL
   )`,
  `CREATE TABLE IF NOT EXISTS items (
     id INTEGER PRIMARY KEY AUTOINCREMENT,
     user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
     title TEXT NOT NULL,
     done INTEGER NOT NULL DEFAULT 0,
     created_at TEXT NOT NULL
   )`,
];

/** Open (or create) the database at `path` and bring its schema up to date. ':memory:' works for tests. */
export function openDb(path: string): Db {
  if (path !== ':memory:') mkdirSync(dirname(path), { recursive: true });
  const db = new DatabaseSync(path);
  db.exec('PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;');
  for (const statement of MIGRATIONS) db.exec(statement);
  return db;
}
