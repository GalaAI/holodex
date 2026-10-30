/** Typed calls to the app's API. Errors carry the server's plain-words message. */
export interface User {
  id: number;
  email: string;
  role: string;
}

export interface Item {
  id: number;
  title: string;
  done: boolean;
  createdAt: string;
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

export const api = {
  me: () => call<{ user: User }>('GET', '/api/me'),
  signup: (email: string, password: string) => call<{ user: User }>('POST', '/api/auth/signup', { email, password }),
  login: (email: string, password: string) => call<{ user: User }>('POST', '/api/auth/login', { email, password }),
  logout: () => call<{ ok: true }>('POST', '/api/auth/logout'),
  items: () => call<{ items: Item[] }>('GET', '/api/items'),
  addItem: (title: string) => call<{ item: Item }>('POST', '/api/items', { title }),
  setDone: (id: number, done: boolean) => call<{ ok: true }>('PATCH', `/api/items/${id}`, { done }),
  removeItem: (id: number) => call<{ ok: true }>('DELETE', `/api/items/${id}`),
};
