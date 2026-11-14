import { AdminClient } from './contract';

// The browser transport of the typed Admin client (FIX-WEB-05): the generated
// contract class owns the routes and the message shapes straight from the descriptors,
// this core owns the credential header, the JSON body and the error mapping. Replaces
// the FIX-WEB-04 minimal helper — every console page calls the client from here.
export class AdminError extends Error {
  constructor(readonly status: number, message: string) {
    super(message);
    this.name = 'AdminError';
  }
}

export function createAdminClient(token: string): AdminClient {
  return new AdminClient(async <Req, Rep>(path: string, request: Req): Promise<Rep> => {
    const response = await fetch(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
      body: JSON.stringify(request),
    });
    if (!response.ok) {
      throw new AdminError(response.status, await response.text());
    }
    return (await response.json()) as Rep;
  });
}

// The statistics faces answer plain GETs (FIX-OBS-08..15) under the control plane's
// `/api` group — not the transcoded contract's `/admin/api/v1` prefix above: no body,
// the same bearer header and the same error mapping as the typed client.
export async function getAdminJson<T>(token: string, path: string): Promise<T> {
  const response = await fetch(path, { headers: { Authorization: `Bearer ${token}` } });
  if (!response.ok) {
    throw new AdminError(response.status, await response.text());
  }
  return (await response.json()) as T;
}

// The bots panel's commands (BOTS-2) ride plain POST/DELETE under the same /api group:
// the same bearer header and error mapping, a JSON body when the command carries one.
export async function postAdminJson<T>(token: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) {
    throw new AdminError(response.status, await response.text());
  }
  return (await response.json()) as T;
}

export async function deleteAdminJson<T>(token: string, path: string): Promise<T> {
  const response = await fetch(path, { method: 'DELETE', headers: { Authorization: `Bearer ${token}` } });
  if (!response.ok) {
    throw new AdminError(response.status, await response.text());
  }
  return (await response.json()) as T;
}
