import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { ConsoleRoot } from '../App';

// The FIX-OBS-14 operations overview against the statistics faces, mocked at the fetch
// boundary: the seven faces answer independently, numbers render as the faces state
// them, one face's failure is that card's error while every other face still renders,
// and the refresh button refetches the faces.

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

function statsFaces() {
  return {
    '/ops/alerts': {
      status: 200,
      body: [
        {
          kind: 'machine-cpu',
          subject: 'node-1',
          observed: 95.5,
          threshold: 90,
          detail: 'latest sample reads 95.5% cpu.',
        },
      ],
    },
    '/telemetry/machine': {
      status: 200,
      body: [
        {
          nodeId: 'node-1',
          sampledAtUtc: '2026-11-11T00:00:00Z',
          cpuPercent: 12.5,
          memory: { totalBytes: 16_000_000_000, usedBytes: 10_000_000_000, usedPercent: 64 },
          load: null,
          disk: null,
          samples: 8,
        },
      ],
    },
    '/rooms/stats': {
      status: 200,
      body: {
        total: 3,
        active: 2,
        byState: { Running: 2, Preparing: 1 },
        byTenant: [{ tenant: 'tenant-a', rooms: 3 }],
      },
    },
    '/online/stats': {
      status: 200,
      body: {
        playersTotal: 7,
        streamersTotal: 2,
        playersByTenantGame: [{ tenant: 'tenant-a', game: 'game-a', count: 7 }],
        streamersByTenant: [{ tenant: 'tenant-a', count: 2 }],
        roomsByTenantGame: [],
      },
    },
    '/ingress/stats': {
      status: 200,
      body: {
        accepted: 101,
        duplicates: 2,
        unverified: 3,
        unmapped: 4,
        rateLimited: 5,
        tooLarge: 6,
        timedOut: 7,
        gateways: 1,
        inputEventsBySource: [],
      },
    },
    '/delivery/stats': {
      status: 200,
      body: {
        ingested: 202,
        applied: 200,
        merged: 0,
        dropped: 1,
        delivered: 199,
        effectsApplied: 10,
        effectsPending: 2,
        effectsDispatching: 1,
        effectsDone: 7,
        effectsFailed: 0,
        reporters: 1,
      },
    },
    '/platform-api/stats': {
      status: 200,
      body: {
        endpoints: [{ endpoint: 'river.execute', calls: 15, failures: 1, latency: [] }],
        gateways: [],
      },
    },
    '/ops/window': {
      status: 200,
      body: {
        windowMinutes: 60,
        windowStartUtc: '2026-11-11T00:00:00Z',
        families: [{ family: 'chorus_ingress_accepted_total', delta: 12 }],
        series: [],
      },
    },
  };
}

function overviewFetch(overrides: Record<string, { status: number; body: unknown }> = {}) {
  const routes = { '/who-am-i': { status: 200, body: gmIdentity }, ...statsFaces(), ...overrides };
  const calls: string[] = [];
  const fetchMock = vi.fn(async (url: RequestInfo | URL, _init?: RequestInit) => {
    const path = String(url);
    calls.push(path);
    // The statistics faces carry query strings (windowMinutes); match on the bare path.
    const bare = path.split('?')[0];
    const hit = Object.entries(routes).find(([suffix]) => bare.endsWith(suffix));
    if (hit === undefined) {
      throw new Error(`unexpected fetch: ${path}`);
    }
    return jsonResponse(hit[1].status, hit[1].body);
  });
  return { fetchMock, calls };
}

async function openOverview(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText('Operator token'), 'alice-token');
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
  // The shell's menu anchors have no href (navigation is onClick), so click the item
  // text. The menu mounts with the responsive layout's desktop flip, one effect tick
  // after the shell — the timeout absorbs that tick under load.
  await user.click(await screen.findByText('Home', { exact: true }, { timeout: 5000 }));
  await waitFor(() => expect(screen.getByText('Machines (15 min)')).toBeInTheDocument());
}

beforeEach(() => {
  sessionStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('the operations overview page', () => {
  it('renders the numbers every statistics face stated', { timeout: 20000 }, async () => {
    const { fetchMock } = overviewFetch();
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const user = userEvent.setup();
    await openOverview(user);

    // Alerts face: the breach row with its kind, subject and observed word.
    const alertsCard = screen.getByText('Alerts').closest('.ant-card') as HTMLElement;
    expect(within(alertsCard).getByText('machine-cpu')).toBeInTheDocument();
    expect(within(alertsCard).getByText('node-1')).toBeInTheDocument();
    expect(within(alertsCard).getByText('95.5')).toBeInTheDocument();

    // Machines face: the node row with cpu and memory.
    const machinesCard = screen.getByText('Machines (15 min)').closest('.ant-card') as HTMLElement;
    expect(within(machinesCard).getByText('node-1')).toBeInTheDocument();
    expect(within(machinesCard).getByText('12.5%')).toBeInTheDocument();
    expect(within(machinesCard).getByText('64.0%')).toBeInTheDocument();
    expect(within(machinesCard).getByText('8')).toBeInTheDocument();

    // Rooms face: totals plus state and tenant tags.
    const roomsCard = screen.getByText('Rooms (live)').closest('.ant-card') as HTMLElement;
    expect(within(roomsCard).getByText('Running: 2')).toBeInTheDocument();
    expect(within(roomsCard).getByText('tenant-a: 3')).toBeInTheDocument();

    // Online face: the players and streamers totals.
    const onlineCard = screen.getByText('Online').closest('.ant-card') as HTMLElement;
    expect(within(onlineCard).getByText('7')).toBeInTheDocument();
    expect(within(onlineCard).getByText('tenant-a/game-a: 7')).toBeInTheDocument();

    // Raw ingress and event delivery faces.
    const ingressCard = screen.getByText('Raw ingress').closest('.ant-card') as HTMLElement;
    expect(within(ingressCard).getByText('101')).toBeInTheDocument();
    const deliveryCard = screen.getByText('Event delivery').closest('.ant-card') as HTMLElement;
    expect(within(deliveryCard).getByText('202')).toBeInTheDocument();

    // Platform API face: the observed endpoint row.
    const platformCard = screen.getByText('Platform API').closest('.ant-card') as HTMLElement;
    expect(within(platformCard).getByText('river.execute')).toBeInTheDocument();
    expect(within(platformCard).getByText('15')).toBeInTheDocument();

    // Ops window face: the counter family with its windowed increment.
    const windowCard = screen.getByText('Counter increments (60 min)').closest('.ant-card') as HTMLElement;
    expect(within(windowCard).getByText('chorus_ingress_accepted_total')).toBeInTheDocument();
    expect(within(windowCard).getByText('12')).toBeInTheDocument();

    // Every face answered with the operator's bearer header — the stats faces live
    // under the control plane's /api group, pinned here against prefix drift.
    const statsCalls = fetchMock.mock.calls.filter(([u]) => String(u).endsWith('/rooms/stats'));
    expect(statsCalls).toHaveLength(1);
    expect(String(statsCalls[0][0])).toBe('/api/rooms/stats');
    expect((statsCalls[0][1]?.headers as Record<string, string>).Authorization).toBe('Bearer alice-token');
    expect(String(fetchMock.mock.calls.find(([u]) => String(u).includes('/ops/alerts'))?.[0])).toBe('/api/ops/alerts');
  });

  it('isolates a failing face inside its card and keeps the other faces rendered', { timeout: 20000 }, async () => {
    vi.stubGlobal('fetch', overviewFetch({ '/delivery/stats': { status: 500, body: { error: 'boom' } } }).fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const user = userEvent.setup();
    await openOverview(user);

    const deliveryCard = screen.getByText('Event delivery').closest('.ant-card') as HTMLElement;
    expect(within(deliveryCard).getByText('The face did not answer usable data')).toBeInTheDocument();
    expect(within(deliveryCard).getByText('500')).toBeInTheDocument();

    // The other faces still answer.
    expect(screen.getByText('12.5%')).toBeInTheDocument();
    expect(screen.getByText('river.execute')).toBeInTheDocument();
    expect(screen.getByText('chorus_ingress_accepted_total')).toBeInTheDocument();
  });

  it('turns a wrong-shaped answer into its card error instead of a page crash', { timeout: 20000 }, async () => {
    // A 200 that carries some other face's shape (an older control plane, a proxy
    // interposing) is that card's error — the page keeps rendering the rest.
    vi.stubGlobal('fetch', overviewFetch({ '/rooms/stats': { status: 200, body: { operatorId: 'x', role: 'gm' } } }).fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const user = userEvent.setup();
    await openOverview(user);

    const roomsCard = screen.getByText('Rooms (live)').closest('.ant-card') as HTMLElement;
    expect(within(roomsCard).getByText('The face did not answer usable data')).toBeInTheDocument();
    expect(within(roomsCard).getByText('unexpected shape')).toBeInTheDocument();

    // The other faces still answer.
    expect(screen.getByText('12.5%')).toBeInTheDocument();
    expect(screen.getByText('river.execute')).toBeInTheDocument();
    expect(screen.getByText('chorus_ingress_accepted_total')).toBeInTheDocument();
  });

  it('refetches the faces on refresh', { timeout: 20000 }, async () => {
    const { fetchMock, calls } = overviewFetch();
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const user = userEvent.setup();
    await openOverview(user);

    const statsBefore = calls.filter((path) => path.endsWith('/rooms/stats')).length;
    expect(statsBefore).toBe(1);

    await user.click(screen.getByRole('button', { name: /Refresh/ }));
    await waitFor(() => {
      expect(calls.filter((path) => path.endsWith('/rooms/stats')).length).toBe(2);
    });

    // The refreshed faces render again, not a stuck spinner.
    expect(await screen.findByText('12.5%')).toBeInTheDocument();
    expect(screen.getByText('chorus_ingress_accepted_total')).toBeInTheDocument();
  });
});
