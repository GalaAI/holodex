import { mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { DatabaseSync } from 'node:sqlite';

/**
 * The records database: SQLite through Node's built-in node:sqlite. Holds
 * accounts with roles, contacts (people and companies), and deals that move
 * through a pipeline. ERP builds add their own tables (products, stock,
 * orders, invoices) as further migrations at the end of the list.
 */
export type Db = DatabaseSync;

export const ROLES = ['owner', 'manager', 'staff', 'viewer'] as const;
export type Role = (typeof ROLES)[number];

export const STAGES = ['lead', 'qualified', 'proposal', 'won', 'lost'] as const;
export type Stage = (typeof STAGES)[number];

const MIGRATIONS: string[] = [
  `CREATE TABLE IF NOT EXISTS users (
     id INTEGER PRIMARY KEY AUTOINCREMENT,
     email TEXT NOT NULL UNIQUE,
     password_hash TEXT NOT NULL,
     role TEXT NOT NULL DEFAULT 'viewer',
     created_at TEXT NOT NULL
   )`,
  `CREATE TABLE IF NOT EXISTS sessions (
     token_hash TEXT PRIMARY KEY,
     user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
     expires_at TEXT NOT NULL
   )`,
  `CREATE TABLE IF NOT EXISTS contacts (
     id INTEGER PRIMARY KEY AUTOINCREMENT,
     name TEXT NOT NULL,
     company TEXT NOT NULL DEFAULT '',
     email TEXT NOT NULL DEFAULT '',
     phone TEXT NOT NULL DEFAULT '',
     notes TEXT NOT NULL DEFAULT '',
     created_by INTEGER REFERENCES users(id) ON DELETE SET NULL,
     created_at TEXT NOT NULL
   )`,
  `CREATE TABLE IF NOT EXISTS deals (
     id INTEGER PRIMARY KEY AUTOINCREMENT,
     title TEXT NOT NULL,
     contact_id INTEGER REFERENCES contacts(id) ON DELETE SET NULL,
     value_cents INTEGER NOT NULL DEFAULT 0,
     stage TEXT NOT NULL DEFAULT 'lead',
     owner_id INTEGER REFERENCES users(id) ON DELETE SET NULL,
     updated_at TEXT NOT NULL
   )`,
  `CREATE TABLE IF NOT EXISTS activity (
     id INTEGER PRIMARY KEY AUTOINCREMENT,
     user_id INTEGER REFERENCES users(id) ON DELETE SET NULL,
     action TEXT NOT NULL,
     subject TEXT NOT NULL,
     at TEXT NOT NULL
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
