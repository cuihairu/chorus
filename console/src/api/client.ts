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

// The statistics faces answer plain GETs (FIX-OBS-08..13): no body, the same bearer
// header and the same error mapping as the typed client above.
export async function getAdminJson<T>(token: string, path: string): Promise<T> {
  const response = await fetch(path, { headers: { Authorization: `Bearer ${token}` } });
  if (!response.ok) {
    throw new AdminError(response.status, await response.text());
  }
  return (await response.json()) as T;
}
