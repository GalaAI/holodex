/** Typed calls to the records API. Errors carry the server's plain-words message. */
export type Role = 'owner' | 'manager' | 'staff' | 'viewer';
export type Stage = 'lead' | 'qualified' | 'proposal' | 'won' | 'lost';
export const STAGES: Stage[] = ['lead', 'qualified', 'proposal', 'won', 'lost'];

export interface User {
  id: number;
  email: string;
  role: Role;
}

export interface Contact {
  id: number;
  name: string;
  company: string;
  email: string;
  phone: string;
  notes: string;
}

export interface Deal {
  id: number;
  title: string;
  valueCents: number;
  stage: Stage;
  contactId: number | null;
  contactName?: string | null;
}

export interface StageColumn {
  stage: Stage;
  deals: Deal[];
  totalCents: number;
}

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
  }
}

async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method,
    credentials: 'same-origin',
    headers: body === undefined ? {} : { 'content-type': 'application/json' },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  const data = (await response.json().catch(() => ({}))) as { error?: { message?: string } };
  if (!response.ok) throw new ApiError(data.error?.message ?? `The server answered ${response.status}.`, response.status);
  return data as T;
}

const RANK: Record<Role, number> = { viewer: 0, staff: 1, manager: 2, owner: 3 };
export const can = (role: Role, needed: Role) => RANK[role] >= RANK[needed];

export const api = {
  me: () => call<{ user: User }>('GET', '/api/me'),
  signup: (email: string, password: string) => call<{ user: User }>('POST', '/api/auth/signup', { email, password }),
  login: (email: string, password: string) => call<{ user: User }>('POST', '/api/auth/login', { email, password }),
  logout: () => call<{ ok: true }>('POST', '/api/auth/logout'),
  contacts: (q = '') => call<{ contacts: Contact[] }>('GET', `/api/contacts?q=${encodeURIComponent(q)}`),
  addContact: (contact: Partial<Contact>) => call<{ contact: Contact }>('POST', '/api/contacts', contact),
  pipeline: () => call<{ stages: StageColumn[] }>('GET', '/api/pipeline'),
  addDeal: (title: string, valueCents: number, contactId: number | null) =>
    call<{ deal: Deal }>('POST', '/api/deals', { title, valueCents, contactId }),
  moveDeal: (id: number, stage: Stage) => call<{ ok: true }>('PATCH', `/api/deals/${id}/stage`, { stage }),
  users: () => call<{ users: User[] }>('GET', '/api/users'),
  setRole: (id: number, role: Role) => call<{ ok: true }>('PATCH', `/api/users/${id}`, { role }),
};

export const money = (cents: number) =>
  new Intl.NumberFormat(undefined, { style: 'currency', currency: 'USD', maximumFractionDigits: 0 }).format(cents / 100);
