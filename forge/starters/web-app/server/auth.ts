import { createHash, randomBytes, scrypt as scryptCallback, timingSafeEqual } from 'node:crypto';
import { promisify } from 'node:util';
import type { Db } from './db.js';

/**
 * Email and password accounts with server-side sessions. Passwords are
 * hashed with scrypt (node:crypto); session tokens are random, and only
 * their SHA-256 hash is stored, so a copied database cannot sign anyone in.
 * Follows the OWASP Password Storage and Session Management cheat sheets.
 */
const scrypt = promisify(scryptCallback) as (password: string, salt: Buffer, keylen: number) => Promise<Buffer>;

const KEY_LENGTH = 64;
export const SESSION_DAYS = 14;
export const MIN_PASSWORD = 10;

export interface User {
  id: number;
  email: string;
  role: string;
}

export async function hashPassword(password: string): Promise<string> {
  const salt = randomBytes(16);
  const key = await scrypt(password, salt, KEY_LENGTH);
  return `scrypt$${salt.toString('base64')}$${key.toString('base64')}`;
}

export async function verifyPassword(password: string, stored: string): Promise<boolean> {
  const [scheme, saltText, keyText] = stored.split('$');
  if (scheme !== 'scrypt' || !saltText || !keyText) return false;
  const expected = Buffer.from(keyText, 'base64');
  const actual = await scrypt(password, Buffer.from(saltText, 'base64'), expected.length);
  return actual.length === expected.length && timingSafeEqual(actual, expected);
}

function hashToken(token: string): string {
  return createHash('sha256').update(token).digest('hex');
}

export function normalizeEmail(email: string): string {
  return email.trim().toLowerCase();
}

export function checkCredentials(email: unknown, password: unknown): string | undefined {
  if (typeof email !== 'string' || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email.trim())) return 'Enter a valid email.';
  if (typeof password !== 'string' || password.length < MIN_PASSWORD) {
    return `Use a password of at least ${MIN_PASSWORD} characters.`;
  }
  return undefined;
}

/** Create a user. The first account becomes the owner. Returns undefined when the email is taken. */
export async function createUser(db: Db, email: string, password: string): Promise<User | undefined> {
  const normalized = normalizeEmail(email);
  const existing = db.prepare('SELECT id FROM users WHERE email = ?').get(normalized);
  if (existing) return undefined;
  const count = db.prepare('SELECT COUNT(*) AS n FROM users').get() as { n: number };
  const role = count.n === 0 ? 'owner' : 'member';
  const result = db
    .prepare('INSERT INTO users (email, password_hash, role, created_at) VALUES (?, ?, ?, ?)')
    .run(normalized, await hashPassword(password), role, new Date().toISOString());
  return { id: Number(result.lastInsertRowid), email: normalized, role };
}

export async function authenticate(db: Db, email: string, password: string): Promise<User | undefined> {
  const row = db
    .prepare('SELECT id, email, role, password_hash FROM users WHERE email = ?')
    .get(normalizeEmail(email)) as (User & { password_hash: string }) | undefined;
  if (!row || !(await verifyPassword(password, row.password_hash))) return undefined;
  return { id: row.id, email: row.email, role: row.role };
}

export function startSession(db: Db, userId: number): { token: string; expires: Date } {
  const token = randomBytes(32).toString('base64url');
  const expires = new Date(Date.now() + SESSION_DAYS * 24 * 60 * 60 * 1000);
  db.prepare('INSERT INTO sessions (token_hash, user_id, expires_at) VALUES (?, ?, ?)').run(
    hashToken(token),
    userId,
    expires.toISOString(),
  );
  return { token, expires };
}

export function userForSession(db: Db, token: string | undefined): User | undefined {
  if (!token) return undefined;
  const row = db
    .prepare(
      `SELECT users.id AS id, users.email AS email, users.role AS role, sessions.expires_at AS expires_at
       FROM sessions JOIN users ON users.id = sessions.user_id WHERE sessions.token_hash = ?`,
    )
    .get(hashToken(token)) as (User & { expires_at: string }) | undefined;
  if (!row) return undefined;
  if (new Date(row.expires_at).getTime() < Date.now()) {
    endSession(db, token);
    return undefined;
  }
  return { id: row.id, email: row.email, role: row.role };
}

export function endSession(db: Db, token: string | undefined): void {
  if (token) db.prepare('DELETE FROM sessions WHERE token_hash = ?').run(hashToken(token));
}
