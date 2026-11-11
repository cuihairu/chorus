import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { ConsoleRoot } from '../App';

// The FIX-WEB-07 rooms page against the control plane's room faces, mocked at the fetch
// boundary: the OBS-08 stats face and the room records render as the faces stated them,
// clicking a room's row reads that room's detail face (lifecycle words, pending events,
// live connections), and a failing detail face is that card's error while the other faces
// still render.

const gmIdentity = {
  operatorId: 'gm-alice',
  role: 'gm',
  tenants: ['tenant-a'],
  games: ['game-a'],
  enabled: true,
};

function jsonResponse(status: number, body: unknown) {
  return {
    ok: status >= 200 && status < 300,
    status,
    text: async () => JSON.stringify(body),
    json: async () => body,
  } as Response;
}

function roomA() {
  return {
    id: 'room-a',
    workerId: 'worker-a',
    workerInstance: 'worker-a-1',
    epoch: 3,
    state: 'Running',
    updatedAtMs: 1_770_000_000_000,
    streamerId: 's-a',
    platform: 'simulator',
    mode: 'default',
    region: 'region-1',
    matchId: 'match-9',
    tickId: 1209,
    inbox: null,
    receipts: null,
    snapshot: null,
  };
}

function roomFaces() {
  return {
    '/rooms/stats': {
      status: 200,
      body: {
        total: 3,
        active: 2,
        byState: { Running: 2, Preparing: 1 },
        byTenant: [{ tenant: 'tenant-a', rooms: 3 }],
      },
    },
    '/rooms': {
      status: 200,
      body: [roomA(), {
        id: 'room-b', workerId: 'worker-b', workerInstance: 'worker-b-1', epoch: 1,
        state: 'Preparing', updatedAtMs: 1_770_000_100_000, streamerId: null,
        platform: 'simulator', mode: 'default', region: 'region-2', matchId: null,
        tickId: 0, inbox: null, receipts: null, snapshot: null,
      }],
    },
    '/rooms/room-a': {
      status: 200,
      body: {
        room: roomA(),
        pendingEvents: 4,
        connections: [{
          sessionId: 'room-a', clientId: 'client-1', userId: 'user-1', role: 'player',
          gatewayId: 'gateway-a', gatewayInstance: 'gw-1', generation: 'g-1', lastSeenMs: 1_770_000_050_000,
        }],
      },
    },
  };
}

function roomsFetch(overrides: Record<string, { status: number; body: unknown }> = {}) {
  const routes = { '/who-am-i': { status: 200, body: gmIdentity }, ...roomFaces(), ...overrides };
  const calls: string[] = [];
  const fetchMock = vi.fn(async (url: RequestInfo | URL, _init?: RequestInit) => {
    const path = String(url);
    calls.push(path);
    const bare = path.split('?')[0];
    const hit = Object.entries(routes).find(([suffix]) => bare.endsWith(suffix));
    if (hit === undefined) {
      throw new Error(`unexpected fetch: ${path}`);
    }
    return jsonResponse(hit[1].status, hit[1].body);
  });
  return { fetchMock, calls };
}

async function openRooms() {
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Operator token'), 'alice-token');
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
  // The shell's menu anchors have no href (navigation is onClick), so click the item
  // text. The menu mounts with the responsive layout's desktop flip — the timeout
  // absorbs that tick under load.
  await user.click(await screen.findByText('Rooms', { exact: true }, { timeout: 5000 }));
  await waitFor(() => expect(screen.getByText('Room records')).toBeInTheDocument());
  return user;
}

beforeEach(() => {
  sessionStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('the rooms page', () => {
  it('renders the live stats and the room records as the faces stated them', { timeout: 20000 }, async () => {
    const { fetchMock, calls } = roomsFetch();
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    await openRooms();

    const statsCard = screen.getByText('Live stats').closest('.ant-card') as HTMLElement;
    expect(within(statsCard).getByText('3')).toBeInTheDocument();
    expect(within(statsCard).getByText('2')).toBeInTheDocument();
    expect(within(statsCard).getByText('Running: 2')).toBeInTheDocument();
    expect(within(statsCard).getByText('tenant-a: 3')).toBeInTheDocument();

    const recordsCard = screen.getByText('Room records').closest('.ant-card') as HTMLElement;
    expect(within(recordsCard).getByText('room-a')).toBeInTheDocument();
    expect(within(recordsCard).getByText('room-b')).toBeInTheDocument();
    expect(within(recordsCard).getByText('simulator / default / region-1')).toBeInTheDocument();
    expect(within(recordsCard).getByText('simulator / default / region-2')).toBeInTheDocument();
    expect(within(recordsCard).getByText('s-a')).toBeInTheDocument();

    // The detail card waits for a named room — no detail fetch until a row is clicked.
    expect(screen.getByText('Select a room.')).toBeInTheDocument();
    expect(calls.filter((path) => path === '/api/rooms')).toHaveLength(1);
    const recordInit = fetchMock.mock.calls.find(([u]) => String(u) === '/api/rooms');
    expect((recordInit?.[1]?.headers as Record<string, string>).Authorization).toBe('Bearer alice-token');
    // The stats face rides the control plane's /api group too (the overview fires it once
    // on the /home landing; the rooms page's own call is the last one, pinned exactly).
    expect(String(calls.filter((path) => path === '/api/rooms/stats').at(-1))).toBe('/api/rooms/stats');
  });

  it('reads the clicked room\'s detail face with its pending events and connections', { timeout: 20000 }, async () => {
    const { fetchMock, calls } = roomsFetch();
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const user = await openRooms();

    await user.click(within(screen.getByText('Room records').closest('.ant-card') as HTMLElement).getByText('room-a'));

    const detailCard = screen.getByText('Room detail').closest('.ant-card') as HTMLElement;
    expect(await within(detailCard).findByText('worker-a / worker-a-1')).toBeInTheDocument();
    expect(within(detailCard).getByText('match-9')).toBeInTheDocument();
    expect(within(detailCard).getByText('s-a')).toBeInTheDocument();
    expect(within(detailCard).getByText('4')).toBeInTheDocument();
    expect(within(detailCard).getByText('client-1')).toBeInTheDocument();
    expect(within(detailCard).getByText('user-1')).toBeInTheDocument();
    expect(within(detailCard).getByText('gateway-a / gw-1')).toBeInTheDocument();

    // The detail face answered for exactly the named room, URL-pinned against drift.
    expect(calls.filter((path) => path === '/api/rooms/room-a')).toHaveLength(1);
    const detailInit = fetchMock.mock.calls.find(([u]) => String(u) === '/api/rooms/room-a');
    expect((detailInit?.[1]?.headers as Record<string, string>).Authorization).toBe('Bearer alice-token');
  });

  it('isolates a failing detail face inside its card and keeps the other faces rendered', { timeout: 20000 }, async () => {
    vi.stubGlobal('fetch', roomsFetch({ '/rooms/room-a': { status: 500, body: { error: 'boom' } } }).fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const user = await openRooms();

    await user.click(within(screen.getByText('Room records').closest('.ant-card') as HTMLElement).getByText('room-a'));

    const detailCard = screen.getByText('Room detail').closest('.ant-card') as HTMLElement;
    expect(await within(detailCard).findByText('The face did not answer usable data')).toBeInTheDocument();
    expect(within(detailCard).getByText('500')).toBeInTheDocument();

    // The stats and records faces still answer.
    expect(screen.getByText('Running: 2')).toBeInTheDocument();
    expect(screen.getByText('room-b')).toBeInTheDocument();
  });
});
