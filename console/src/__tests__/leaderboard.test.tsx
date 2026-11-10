import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { ConsoleRoot } from '../App';

// The FIX-WEB-13 leaderboard back office against the FIX-WEB-12 read face, mocked at
// the fetch boundary: the operator names the read key only, the top walk pages through
// one snapshot (continuation carries the token, the version tag stays, a changed board
// answers the restart word), the rank face answers found and found=false alike, and the
// around window returns ranked rows. Reads sit on the viewer floor — a viewer reads,
// a scope-less caller is refused.

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

/** Each route serves a queue of replies, one per call, in order. */
function sequenceFetch(routes: Record<string, Array<{ status: number; body: unknown }>>) {
  const queues: Record<string, Array<{ status: number; body: unknown }>> = {};
  for (const [suffix, replies] of Object.entries(routes)) queues[suffix] = [...replies];
  return vi.fn(async (url: RequestInfo | URL, _init?: RequestInit) => {
    const path = String(url);
    const hit = Object.keys(queues).find((suffix) => path.endsWith(suffix));
    if (hit === undefined) {
      throw new Error(`unexpected fetch: ${path}`);
    }
    const reply = queues[hit].shift();
    if (reply === undefined) {
      throw new Error(`exhausted replies for: ${path}`);
    }
    return jsonResponse(reply.status, reply.body);
  });
}

const firstPage = {
  entries: [
    { subjectId: 'a', score: 5 },
    { subjectId: 'c', score: 5 },
  ],
  readVersionUnixMs: 1720000000000,
  hasMore: true,
  nextPageToken: 'tok-1',
};

const secondPage = {
  entries: [{ subjectId: 'b', score: 3 }],
  readVersionUnixMs: 1720000000000,
  hasMore: false,
  nextPageToken: '',
};

const rankReply = { found: true, rank: 3, score: 3, readVersionUnixMs: 1720000000000 };

async function openLeaderboards() {
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Operator token'), 'alice-token');
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
  await user.click(await screen.findByText('Leaderboards', { exact: true }));
  await waitFor(() => expect(screen.getByText('Top N')).toBeInTheDocument());

  // Three query cards stay mounted, so field queries are card-scoped.
  const topCard = screen.getByText('Top N').closest('.ant-card') as HTMLElement;
  const rankCard = screen.getByText('Rank').closest('.ant-card') as HTMLElement;
  const aroundCard = screen.getByText('Around').closest('.ant-card') as HTMLElement;
  return { user, topCard, rankCard, aroundCard };
}

/** Fills the shared read key of one card; empty period and scope ride as the defaults. */
async function fillKey(
  card: HTMLElement,
  user: { type: (element: Element, text: string) => Promise<void> },
) {
  await user.type(within(card).getByLabelText('Tenant'), 'tenant-a');
  await user.type(within(card).getByLabelText('Game'), 'game-a');
  await user.type(within(card).getByLabelText('Board'), 'score-alltime');
}

function bodies(mock: ReturnType<typeof sequenceFetch>, suffix: string): Record<string, unknown>[] {
  return mock.mock.calls
    .filter(([u]: unknown[]) => String(u).endsWith(suffix))
    .map(([, init]) => JSON.parse(String((init as RequestInit).body)));
}

beforeEach(() => {
  sessionStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('the leaderboard page', () => {
  it('reads the top page and continues the same snapshot', { timeout: 20000 }, async () => {
    const fetchMock = sequenceFetch({
      '/who-am-i': [{ status: 200, body: gmIdentity }],
      '/get-leaderboard-top': [
        { status: 200, body: firstPage },
        { status: 200, body: secondPage },
      ],
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const { user, topCard } = await openLeaderboards();
    await fillKey(topCard, user);
    await user.click(within(topCard).getByRole('button', { name: 'Read top' }));

    // The first page: the read key spelled on the wire, empty period and scope riding
    // as the all-time global defaults.
    const [firstBody] = bodies(fetchMock, '/get-leaderboard-top');
    expect(firstBody).toEqual({
      tenantId: 'tenant-a',
      gameId: 'game-a',
      board: 'score-alltime',
      periodId: '',
      scopeId: '',
      subject: 0,
      maxEntries: 10,
      pageToken: '',
    });
    expect(within(topCard).getAllByText('a').length).toBeGreaterThan(0);
    expect(within(topCard).getAllByText('c').length).toBeGreaterThan(0);
    expect(within(topCard).getByText('read version 1720000000000')).toBeInTheDocument();
    expect(within(topCard).getByRole('button', { name: 'Continue' })).toBeInTheDocument();

    await user.click(within(topCard).getByRole('button', { name: 'Continue' }));
    const walkBodies = bodies(fetchMock, '/get-leaderboard-top');
    expect(walkBodies).toHaveLength(2);
    expect(walkBodies[1].pageToken).toBe('tok-1');
    expect(within(topCard).getAllByText('b').length).toBeGreaterThan(0);
    // The walk stays on one snapshot: the version tag survives the continuation.
    expect(within(topCard).getByText('read version 1720000000000')).toBeInTheDocument();
    // The walk is over — the continuation button goes.
    expect(within(topCard).queryByRole('button', { name: 'Continue' })).not.toBeInTheDocument();
  });

  it('answers the rank face and treats found=false as an answer', { timeout: 20000 }, async () => {
    const fetchMock = sequenceFetch({
      '/who-am-i': [{ status: 200, body: gmIdentity }],
      '/get-leaderboard-rank': [
        { status: 200, body: rankReply },
        { status: 200, body: { found: false, rank: 0, score: 0, readVersionUnixMs: 0 } },
      ],
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const { user, rankCard } = await openLeaderboards();
    await fillKey(rankCard, user);
    await user.type(within(rankCard).getByLabelText('Subject ID'), 'b');
    await user.click(within(rankCard).getByRole('button', { name: 'Find rank' }));

    const [firstBody] = bodies(fetchMock, '/get-leaderboard-rank');
    expect(firstBody.subjectId).toBe('b');
    expect(within(rankCard).getByText('rank')).toBeInTheDocument();
    // Rank and score both answer 3 in this fixture — two cells share the value.
    expect(within(rankCard).getAllByText('3').length).toBeGreaterThanOrEqual(2);
    expect(within(rankCard).getByText('read version 1720000000000')).toBeInTheDocument();

    await user.clear(within(rankCard).getByLabelText('Subject ID'));
    await user.type(within(rankCard).getByLabelText('Subject ID'), 'z-never-scored');
    await user.click(within(rankCard).getByRole('button', { name: 'Find rank' }));
    expect(within(rankCard).getByText('That subject has no score on this board.')).toBeInTheDocument();
  });

  it('reads the around window as ranked rows at one view', { timeout: 20000 }, async () => {
    const fetchMock = sequenceFetch({
      '/who-am-i': [{ status: 200, body: gmIdentity }],
      '/get-leaderboard-around': [
        {
          status: 200,
          body: {
            found: true,
            entries: [
              { rank: 2, subjectId: 'c', score: 5 },
              { rank: 3, subjectId: 'b', score: 3 },
            ],
            readVersionUnixMs: 1720000000000,
          },
        },
      ],
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const { user, aroundCard } = await openLeaderboards();
    await fillKey(aroundCard, user);
    await user.type(within(aroundCard).getByLabelText('Subject ID'), 'b');
    const windowField = within(aroundCard).getByLabelText('Window');
    await user.clear(windowField);
    await user.type(windowField, '1');
    await user.click(within(aroundCard).getByRole('button', { name: 'Read window' }));

    const [wireBody] = bodies(fetchMock, '/get-leaderboard-around');
    expect(wireBody.subjectId).toBe('b');
    expect(wireBody.window).toBe(1);
    expect(within(aroundCard).getAllByText('c').length).toBeGreaterThan(0);
    expect(within(aroundCard).getAllByText('b').length).toBeGreaterThan(0);
    expect(within(aroundCard).getByText('Rank #')).toBeInTheDocument();
    expect(within(aroundCard).getByText('read version 1720000000000')).toBeInTheDocument();
  });

  it('refuses a stale continuation with the restart word', { timeout: 20000 }, async () => {
    const fetchMock = sequenceFetch({
      '/who-am-i': [{ status: 200, body: gmIdentity }],
      '/get-leaderboard-top': [
        { status: 200, body: firstPage },
        {
          status: 400,
          body: { code: 9, message: "The board changed since the cursor's read version; restart the read.", details: [] },
        },
      ],
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const { user, topCard } = await openLeaderboards();
    await fillKey(topCard, user);
    await user.click(within(topCard).getByRole('button', { name: 'Read top' }));
    await user.click(within(topCard).getByRole('button', { name: 'Continue' }));

    // The walk is refused visibly — no cross-version page, and the dead walk clears.
    expect(within(topCard).getByText(
      'The board changed since this walk started — start a fresh read.',
    )).toBeInTheDocument();
    expect(within(topCard).queryByRole('button', { name: 'Continue' })).not.toBeInTheDocument();
    expect(within(topCard).queryByText('a')).not.toBeInTheDocument();
  });

  it('keeps reads on the viewer floor and names a scope refusal', { timeout: 20000 }, async () => {
    const fetchMock = sequenceFetch({
      '/who-am-i': [{ status: 200, body: viewerIdentity }],
      '/get-leaderboard-top': [{ status: 200, body: secondPage }],
      '/get-leaderboard-rank': [{ status: 403, body: { code: 7, message: 'out of scope', details: [] } }],
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const { user, topCard, rankCard } = await openLeaderboards();
    await fillKey(topCard, user);
    await user.click(within(topCard).getByRole('button', { name: 'Read top' }));
    expect(within(topCard).getAllByText('b').length).toBeGreaterThan(0);

    await fillKey(rankCard, user);
    await user.type(within(rankCard).getByLabelText('Subject ID'), 'b');
    await user.click(within(rankCard).getByRole('button', { name: 'Find rank' }));
    expect(within(rankCard).getByText('Your scope does not cover this board.')).toBeInTheDocument();
  });
});
