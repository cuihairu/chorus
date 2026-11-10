// The minimal browser Admin API face the login page needs (FIX-WEB-04): POST the
// contract prefix with the operator bearer credential. FIX-WEB-05 replaces this with
// the typed client derived from the Admin contract; the shape contract below and the
// status mapping are the parts that survive that line.
export interface WhoAmIReply {
  operatorId: string;
  role: string;
  tenants: string[];
  games: string[];
  enabled: boolean;
}

/** A non-2xx answer from the admin face; `status` is the gRPC-to-HTTP mapping. */
export class AdminError extends Error {
  constructor(readonly status: number, message: string) {
    super(message);
    this.name = 'AdminError';
  }
}

export async function postAdmin<T>(path: string, token: string, body: unknown = {}): Promise<T> {
  const response = await fetch(`/admin/api/v1/${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    throw new AdminError(response.status, await response.text());
  }
  return (await response.json()) as T;
}

export const whoAmI = (token: string) => postAdmin<WhoAmIReply>('who-am-i', token);
