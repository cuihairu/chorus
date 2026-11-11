import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { ConsoleRoot } from '../App';

// The FIX-WEB-11 room-epoch form against the FIX-WEB-05 generated client, mocked at
// the fetch boundary: the operator looks a room's epoch up, bumps against the expected
// value, and reads the honest split of the reply — old/new epoch, the Recovering park,
// the route sync status that separates committed from confirmed, and a duplicate replay
// carrying the recorded values. The whole epoch floor is gm (FIX-WEB-17, GM-06): a
// viewer gets neither the lookup nor the bump.

const gmIdentity = {
  operatorId: 'gm-alice',
  role: 'gm',
  tenants: ['tenant-a'],
  games: ['game-a'],
  enabled: true,
};

const viewerIdentity = { ...gmIdentity, operatorId: 'watcher', role: 'viewer' };

function jsonResponse(status: number, body: unknown) {
  return {
    ok: status >= 200 && status < 300,
    status,
    text: async () => JSON.stringify(body),
    json: async () => body,
  } as Response;
}

function routeFetch(routes: Record<string, { status: number; body: unknown }>) {
  return vi.fn(async (url: RequestInfo | URL, _init?: RequestInit) => {
    const path = String(url);
    const hit = Object.entries(routes).find(([suffix]) => path.endsWith(suffix));
    if (hit === undefined) {
      throw new Error(`unexpected fetch: ${path}`);
    }
    return jsonResponse(hit[1].status, hit[1].body);
  });
}

const epochReply = {
  found: true, epoch: 3, ownerInstanceId: 'game-a', status: 'Active',
  tenantId: 'tenant-a', gameId: 'game-a', roomId: 'room-1',
};

const bumpReply = {
  operationId: 'op-bump-1', roomId: 'room-1', ownerInstanceId: 'game-a',
  oldEpoch: 3, newEpoch: 4, ownershipStatus: 'Recovering', routeSyncStatus: 'Pending',
  duplicate: false, tenantId: 'tenant-a', gameId: 'game-a',
};

/** Signs in through the login page and lands on the epoch page; returns the two cards. */
async function openEpoch(identity: typeof gmIdentity) {
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Operator token'), 'alice-token');
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
  await user.click(await screen.findByText('Room epoch', { exact: true }, { timeout: 10000 }));
  await waitFor(() => expect(screen.getByText('Lookup')).toBeInTheDocument(), { timeout: 10000 });

  // The lookup and bump cards both stay mounted, so field queries are card-scoped.
  const lookupCard = screen.getByText('Lookup').closest('.ant-card') as HTMLElement;
  const bumpCard = screen.getByText('Bump').closest('.ant-card') as HTMLElement;
  return { user, identity, lookupCard, bumpCard };
}

async function fillLookup(
  card: HTMLElement,
  user: { type: (element: Element, text: string) => Promise<void> },
) {
  await user.type(within(card).getByLabelText('Tenant'), 'tenant-a');
  await user.type(within(card).getByLabelText('Game'), 'game-a');
  await user.type(within(card).getByLabelText('Room'), 'room-1');
}

function sentBody(call: unknown[]): Record<string, unknown> {
  return JSON.parse(String((call[1] as RequestInit).body));
}

beforeEach(() => {
  sessionStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('the room epoch page', () => {
  it('looks a room up and shows its epoch, owner and ownership status', async () => {
    const fetchMock = routeFetch({
      '/who-am-i': { status: 200, body: gmIdentity },
      '/get-room-epoch': { status: 200, body: epochReply },
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const { user, lookupCard } = await openEpoch(gmIdentity);
    await fillLookup(lookupCard, user);
    await user.click(within(lookupCard).getByRole('button', { name: 'Look up' }));

    const [url, init] = fetchMock.mock.calls.find(([u]: unknown[]) =>
      String(u).endsWith('/get-room-epoch'))!;
    expect(String(url)).toBe('/admin/api/v1/get-room-epoch');
    const body = sentBody([url, init]);
    expect(body.operator).toBe('gm-alice');
    expect(body.tenantId).toBe('tenant-a');
    expect(body.roomId).toBe('room-1');
    expect(String(body.requestId)).not.toBe('');

    // The status appears twice — reply row and tag.
    expect((await screen.findAllByText('Active')).length).toBeGreaterThan(0);
    expect(screen.getAllByText('game-a').length).toBeGreaterThan(0);
  });

  it('bumps the epoch against the expected value and shows the honest split', async () => {
    const fetchMock = routeFetch({
      '/who-am-i': { status: 200, body: gmIdentity },
      '/bump-room-epoch': { status: 200, body: bumpReply },
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const { user, bumpCard } = await openEpoch(gmIdentity);
    await user.type(within(bumpCard).getByLabelText('Reason'), 'suspect a stuck room');
    await fillLookup(bumpCard, user);
    await user.type(within(bumpCard).getByLabelText('Expected epoch'), '3');
    await user.click(within(bumpCard).getByRole('button', { name: 'Bump epoch' }));

    const [url, init] = fetchMock.mock.calls.find(([u]: unknown[]) =>
      String(u).endsWith('/bump-room-epoch'))!;
    expect(String(url)).toBe('/admin/api/v1/bump-room-epoch');
    const body = sentBody([url, init]);
    expect(body.operator).toBe('gm-alice');
    expect(body.reason).toBe('suspect a stuck room');
    expect(body.expectedEpoch).toBe(3);
    // An absent target rides as the 0 sentinel — the store resolves expected+1.
    expect(body.targetEpoch).toBe(0);

    expect((await screen.findAllByText('Recovering')).length).toBeGreaterThan(0);
    expect((await screen.findAllByText('Pending')).length).toBeGreaterThan(0);
    expect(screen.getByText('op-bump-1')).toBeInTheDocument();
    expect(screen.getByText(
      /Receipt op-bump-1: replaying it answers a duplicate/,
    )).toBeInTheDocument();
  });

  it('marks a replayed request id as a duplicate with the recorded values', async () => {
    const fetchMock = routeFetch({
      '/who-am-i': { status: 200, body: gmIdentity },
      '/bump-room-epoch': { status: 200, body: { ...bumpReply, duplicate: true } },
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const { user, bumpCard } = await openEpoch(gmIdentity);
    await user.type(within(bumpCard).getByLabelText('Reason'), 'lost the first reply');
    await fillLookup(bumpCard, user);
    await user.type(within(bumpCard).getByLabelText('Expected epoch'), '4');
    await user.click(within(bumpCard).getByRole('button', { name: 'Bump epoch' }));

    // The word appears twice — the reply row label and the gold tag.
    expect((await screen.findAllByText('duplicate')).length).toBeGreaterThan(0);
    expect(screen.getByText('op-bump-1')).toBeInTheDocument();
    // The recorded epoch, not a second lift.
    expect(screen.getByText('4')).toBeInTheDocument();
  });

  it('keeps the whole epoch floor closed for a viewer', async () => {
    vi.stubGlobal('fetch', routeFetch({
      '/who-am-i': { status: 200, body: viewerIdentity },
      '/get-room-epoch': { status: 200, body: epochReply },
    }));

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const { lookupCard, bumpCard } = await openEpoch(viewerIdentity);

    expect(screen.getByText(
      'Your role is below the gm floor: epoch lookups and bumps both stay closed.',
    )).toBeInTheDocument();
    expect(within(lookupCard).getByRole('button', { name: 'Look up' })).toBeDisabled();
    expect(within(bumpCard).getByRole('button', { name: 'Bump epoch' })).toBeDisabled();
  });
});
