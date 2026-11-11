import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { ConsoleRoot } from '../App';

// The FIX-WEB-08 streamers and matching page against the match plane's four read faces,
// mocked at the fetch boundary: roster, rules, queue and matches render as the faces
// stated them, one face's failure is that card's error while the other faces still
// render, and the refresh button refetches the faces.

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

function matchFaces() {
  return {
    '/streamers': {
      status: 200,
      body: [{
        id: 's-a', displayName: 'Alice', platform: 'simulator', rating: 1042,
        games: 12, wins: 7, experience: 3400, influence: 8800, updatedAtMs: 1_770_000_000_000,
      }],
    },
    '/rules': {
      status: 200,
      body: {
        version: 3, season: 's1', tierWidth: 200, widenAfterMs: 30000, maxRatingGap: 400,
        timeoutMs: 120000, fillBots: false, levelExperience: 100, starExperience: 20,
        rematchCooldownMs: 60000,
      },
    },
    '/queue': {
      status: 200,
      body: [{
        streamerId: 's-a', mode: 'ranked', region: 'region-1', platform: 'simulator',
        joinedAtMs: 1_770_000_100_000, status: 'Queued', reason: 'Waiting for same-tier opponent',
      }],
    },
    '/matches': {
      status: 200,
      body: [{
        id: 'match-9', status: 'Running', mode: 'ranked', region: 'region-1', season: 's1',
        streamerIds: ['s-a', 's-b'], roomIds: ['room-a', 'room-b'], ratingGap: 36,
        crossTier: false, filledWithBots: false, createdAtMs: 1_770_000_200_000,
        winnerId: null,
      }],
    },
  };
}

function matchPlaneFetch(overrides: Record<string, { status: number; body: unknown }> = {}) {
  const routes = { '/who-am-i': { status: 200, body: gmIdentity }, ...matchFaces(), ...overrides };
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

async function openStreamers() {
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Operator token'), 'alice-token');
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
  // The shell's menu anchors have no href (navigation is onClick), so click the item
  // text. The menu mounts with the responsive layout's desktop flip — the timeout
  // absorbs that tick under load.
  await user.click(await screen.findByText('Streamers and matching', { exact: true }, { timeout: 5000 }));
  await waitFor(() => expect(screen.getByText('Streamer profiles')).toBeInTheDocument());
  return user;
}

beforeEach(() => {
  sessionStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('the streamers and matching page', () => {
  it('renders the roster, rules, queue and matches as the faces stated them', { timeout: 20000 }, async () => {
    const { fetchMock, calls } = matchPlaneFetch();
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    await openStreamers();

    const rosterCard = screen.getByText('Streamer profiles').closest('.ant-card') as HTMLElement;
    expect(within(rosterCard).getByText('s-a')).toBeInTheDocument();
    expect(within(rosterCard).getByText('Alice')).toBeInTheDocument();
    expect(within(rosterCard).getByText('1042')).toBeInTheDocument();
    expect(within(rosterCard).getByText('12 / 7')).toBeInTheDocument();

    const rulesCard = screen.getByText('Match rules').closest('.ant-card') as HTMLElement;
    expect(within(rulesCard).getByText('s1')).toBeInTheDocument();
    expect(within(rulesCard).getByText('200')).toBeInTheDocument();
    expect(within(rulesCard).getByText('30s')).toBeInTheDocument();
    expect(within(rulesCard).getByText('400')).toBeInTheDocument();

    const queueCard = screen.getByText('Queue').closest('.ant-card') as HTMLElement;
    expect(within(queueCard).getByText('s-a')).toBeInTheDocument();
    expect(within(queueCard).getByText('simulator / ranked / region-1')).toBeInTheDocument();
    expect(within(queueCard).getByText('Queued')).toBeInTheDocument();
    expect(within(queueCard).getByText('Waiting for same-tier opponent')).toBeInTheDocument();

    const matchesCard = screen.getByText('Matches').closest('.ant-card') as HTMLElement;
    expect(within(matchesCard).getByText('match-9')).toBeInTheDocument();
    expect(within(matchesCard).getByText('s-a, s-b')).toBeInTheDocument();
    expect(within(matchesCard).getByText('room-a, room-b')).toBeInTheDocument();
    expect(within(matchesCard).getByText('36')).toBeInTheDocument();
    expect(within(matchesCard).getByText('—')).toBeInTheDocument();

    // Every face answered under the control plane's /api group with the bearer header,
    // each exactly once — pinned against prefix drift.
    for (const path of ['/api/streamers', '/api/rules', '/api/queue', '/api/matches']) {
      const hits = calls.filter((call) => call === path);
      expect(hits).toHaveLength(1);
      const init = fetchMock.mock.calls.find(([u]) => String(u) === path);
      expect((init?.[1]?.headers as Record<string, string>).Authorization).toBe('Bearer alice-token');
    }
  });

  it('isolates a failing face inside its card and keeps the other faces rendered', { timeout: 20000 }, async () => {
    vi.stubGlobal('fetch', matchPlaneFetch({ '/matches': { status: 500, body: { error: 'boom' } } }).fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    await openStreamers();

    const matchesCard = screen.getByText('Matches').closest('.ant-card') as HTMLElement;
    expect(within(matchesCard).getByText('The face did not answer usable data')).toBeInTheDocument();
    expect(within(matchesCard).getByText('500')).toBeInTheDocument();

    // The other faces still answer.
    expect(screen.getByText('Alice')).toBeInTheDocument();
    expect(screen.getByText('30s')).toBeInTheDocument();
    expect(screen.getByText('Waiting for same-tier opponent')).toBeInTheDocument();
  });

  it('refetches the faces on refresh', { timeout: 20000 }, async () => {
    const { fetchMock, calls } = matchPlaneFetch();
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const user = await openStreamers();

    const streamersBefore = calls.filter((path) => path === '/api/streamers').length;
    expect(streamersBefore).toBe(1);

    await user.click(screen.getByRole('button', { name: /Refresh/ }));
    await waitFor(() => {
      expect(calls.filter((path) => path === '/api/streamers').length).toBe(2);
    });

    // The refreshed faces render again, not a stuck spinner.
    expect(await screen.findByText('Alice')).toBeInTheDocument();
    expect(screen.getByText('match-9')).toBeInTheDocument();
  });
});
