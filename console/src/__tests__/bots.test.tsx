import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { ConsoleRoot } from '../App';

// The BOTS-2/BOTS-3 bots page against its faces, mocked at the fetch boundary:
// scenarios and runs render as the fleet service stated them, the pressure runs
// carry their one-click capture (speedscope path and hottest frames), the ingress
// and delivery statistics faces answer as the overview's own, one face's failure is
// that card's error, and the commands (create a scenario, start a run, start a
// pressure run) speak to the admin group's bots and pressure routes with the bodies
// the fleet services parse.

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

function botsFaces() {
  return {
    '/api/bots/scenarios': {
      status: 200,
      body: [{
        id: 'sc-1', name: 'demo script', toml: '[script]\nname = "demo"\n',
        updatedBy: 'gm-alice', updatedAtMs: 1_770_000_000_000,
      }],
    },
    '/api/bots/runs': {
      status: 200,
      body: [{
        id: 'bots-run-1', scenarioId: 'sc-1', state: 'Running',
        startedAt: '2026-11-14T10:00:00Z', finishedAt: null, error: null, deviations: [],
        snapshot: {
          scheduled: 312, posted: 312,
          poster: { accepted: 310, duplicated: 2, unauthorized: 0, rateLimited: 0, other: 0 },
        },
      }],
    },
    '/api/pressure/runs': {
      status: 200,
      body: [{
        id: 'pressure-run-1', state: 'Completed',
        sample: {
          sent: 58, accepted: 58, duplicated: 0, rateLimited: 0, laneFull: 0,
          unauthorized: 0, other: 0, targetPerSecond: 60, elapsedSeconds: 30,
          achievedPerSecond: 1.9,
        },
        startedAt: '2026-11-14T10:00:00Z', finishedAt: '2026-11-14T10:00:30Z', error: null,
        tracePath: 'flamegraphs/pressure_pressure-run-1_20261114.nettrace',
        speedscopePath: 'flamegraphs/pressure_pressure-run-1_20261114.speedscope.json',
        hotFrames: [
          { frame: 'HandleIngress', selfSharePercent: 85.7 },
          { frame: 'Run', selfSharePercent: 14.3 },
        ],
      }],
    },
    '/api/ingress/stats': {
      status: 200,
      body: {
        accepted: 310, duplicates: 2, unverified: 0, unmapped: 0, rateLimited: 0,
        tooLarge: 0, timedOut: 0, inputEventsBySource: [],
      },
    },
    '/api/delivery/stats': {
      status: 200,
      body: {
        ingested: 312, applied: 312, merged: 0, dropped: 0, delivered: 312,
        effectsApplied: 30, effectsPending: 0, effectsDispatching: 0, effectsDone: 30,
        effectsFailed: 0,
      },
    },
    '/api/ops/alerts': {
      status: 200,
      body: [],
    },
  };
}

function botsFetch(overrides: Record<string, { status: number; body: unknown }> = {}) {
  const routes = { '/who-am-i': { status: 200, body: gmIdentity }, ...botsFaces(), ...overrides };
  const gets: string[] = [];
  const posts: Array<{ path: string; body: Record<string, unknown> }> = [];
  const fetchMock = vi.fn(async (url: RequestInfo | URL, init?: RequestInit) => {
    const path = String(url);
    const bare = path.split('?')[0];
    const hit = Object.entries(routes).find(([suffix]) => bare.endsWith(suffix));
    if (hit === undefined) {
      throw new Error(`unexpected fetch: ${path}`);
    }
    if (init?.method === 'POST' || init?.method === 'DELETE') {
      posts.push({ path: bare, body: init.body ? JSON.parse(String(init.body)) : {} });
      return jsonResponse(hit[1].status, hit[1].body);
    }
    gets.push(bare);
    return jsonResponse(hit[1].status, hit[1].body);
  });
  return { fetchMock, gets, posts };
}

async function openBots() {
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Operator token'), 'alice-token');
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
  // The shell's menu anchors have no href (navigation is onClick), so click the item
  // text. The timeout absorbs the responsive layout's desktop flip under load.
  await user.click(await screen.findByText('Bots', { exact: true }, { timeout: 5000 }));
  await waitFor(() => expect(screen.getByText('Scenarios')).toBeInTheDocument());
  return user;
}

beforeEach(() => {
  sessionStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('the bots page', () => {
  it('renders scenarios, runs and the statistics faces as they stated themselves', { timeout: 20000 }, async () => {
    const { fetchMock, gets } = botsFetch();
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const user = await openBots();

    const scenariosCard = screen.getByText('Scenarios').closest('.ant-card') as HTMLElement;
    expect(within(scenariosCard).getAllByText('sc-1').length).toBeGreaterThan(0);
    expect(within(scenariosCard).getAllByText('demo script').length).toBeGreaterThan(0);
    expect(within(scenariosCard).getAllByText('gm-alice').length).toBeGreaterThan(0);

    const runsCard = screen.getByText('Runs').closest('.ant-card') as HTMLElement;
    expect(within(runsCard).getAllByText('bots-run-1').length).toBeGreaterThan(0);
    expect(within(runsCard).getAllByText('sc-1').length).toBeGreaterThan(0);
    expect(within(runsCard).getAllByText('Running').length).toBeGreaterThan(0);
    expect(within(runsCard).getAllByText('312').length).toBeGreaterThan(0);

    // The pressure face: the run, its counts, the achieved rate against the target,
    // and — one expansion — the speedscope path the one-click capture landed. antd's
    // expand icon carries no accessible name, so the chevron is queried by its class.
    const pressureCard = screen.getByText('Pressure runs').closest('.ant-card') as HTMLElement;
    expect(within(pressureCard).getAllByText('pressure-run-1').length).toBeGreaterThan(0);
    expect(within(pressureCard).getAllByText('Completed').length).toBeGreaterThan(0);
    expect(within(pressureCard).getByText('58')).toBeInTheDocument();
    expect(within(pressureCard).getByText('1.9 / 60')).toBeInTheDocument();
    expect(within(pressureCard).getAllByText('HandleIngress').length).toBeGreaterThan(0);
    const expand = pressureCard.querySelector('.ant-table-row-expand-icon') as HTMLElement;
    expect(expand).not.toBeNull();
    await user.click(expand);
    await waitFor(() => {
      expect(within(pressureCard).getByText(
        'flamegraphs/pressure_pressure-run-1_20261114.speedscope.json',
      )).toBeInTheDocument();
    });

    const ingressCard = screen.getByText('Ingress face').closest('.ant-card') as HTMLElement;
    expect(within(ingressCard).getByText('310')).toBeInTheDocument();
    expect(within(ingressCard).getByText('2')).toBeInTheDocument();

    const deliveryCard = screen.getByText('Delivery face').closest('.ant-card') as HTMLElement;
    expect(within(deliveryCard).getAllByText('312').length).toBeGreaterThan(0);

    const alertsCard = screen.getByText('Alerts').closest('.ant-card') as HTMLElement;
    expect(within(alertsCard).getByText('No active alerts.')).toBeInTheDocument();

    // Every face answered under the control plane's /api group with the bearer header —
    // pinned against prefix drift. The bots-unique faces answer exactly once; the two
    // statistics faces are shared with the home overview, which also fetched them while
    // the session landed on /home before the menu click.
    for (const path of ['/api/bots/scenarios', '/api/bots/runs', '/api/pressure/runs', '/api/ingress/stats', '/api/delivery/stats', '/api/ops/alerts']) {
      const hits = gets.filter((call) => call === path);
      expect(hits.length).toBeGreaterThanOrEqual(1);
      if (path === '/api/bots/scenarios' || path === '/api/bots/runs' || path === '/api/pressure/runs') {
        expect(hits).toHaveLength(1);
      }
      const init = fetchMock.mock.calls.filter(([u]) => String(u).split('?')[0] === path).at(-1);
      expect((init?.[1]?.headers as Record<string, string>).Authorization).toBe('Bearer alice-token');
    }
  });

  it('isolates a failing face inside its card and keeps the other faces rendered', { timeout: 20000 }, async () => {
    vi.stubGlobal('fetch', botsFetch({ '/api/bots/runs': { status: 500, body: { error: 'boom' } } }).fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    await openBots();

    const runsCard = screen.getByText('Runs').closest('.ant-card') as HTMLElement;
    expect(within(runsCard).getByText('The face did not answer usable data')).toBeInTheDocument();
    expect(within(runsCard).getByText('500')).toBeInTheDocument();

    // The other faces still answer.
    expect(screen.getAllByText('demo script').length).toBeGreaterThan(0);
    expect(screen.getByText('310')).toBeInTheDocument();
    expect(screen.getByText('No active alerts.')).toBeInTheDocument();
  });

  it('refetches the faces on refresh', { timeout: 20000 }, async () => {
    const { fetchMock, gets } = botsFetch();
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const user = await openBots();

    expect(gets.filter((path) => path === '/api/bots/scenarios')).toHaveLength(1);

    await user.click(screen.getByRole('button', { name: /Refresh/ }));
    await waitFor(() => {
      expect(gets.filter((path) => path === '/api/bots/scenarios')).toHaveLength(2);
    });

    // The refreshed faces render again, not a stuck spinner.
    expect(await screen.findByText('demo script')).toBeInTheDocument();
    expect(screen.getByText('bots-run-1')).toBeInTheDocument();
  });

  it('creates a scenario through the editor and speaks to the scenarios route', { timeout: 20000 }, async () => {
    const { fetchMock, posts } = botsFetch({
      '/api/bots/scenarios': {
        status: 200,
        body: [
          {
            id: 'sc-1', name: 'demo script', toml: '[script]\nname = "demo"\n',
            updatedBy: 'gm-alice', updatedAtMs: 1_770_000_000_000,
          },
          { id: 'sc-2', name: 'burst', toml: '[script]\nname = "burst"\n', updatedBy: 'gm-alice', updatedAtMs: 1_770_000_100_000 },
        ],
      },
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const user = await openBots();

    await user.click(screen.getByRole('button', { name: /New scenario/ }));
    await user.type(screen.getByLabelText('Id'), 'sc-2');
    await user.type(screen.getByLabelText('Name'), 'burst');
    // user-event parses [[ as a literal [, so the typed script reads [script] on the wire.
    await user.type(screen.getByLabelText('Script (TOML)'), '[[script]]\nname = "burst"\nduration = 2\n');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(posts.some((post) => post.path === '/api/bots/scenarios')).toBe(true);
    });
    const saved = posts.find((post) => post.path === '/api/bots/scenarios');
    expect(saved?.body.id).toBe('sc-2');
    expect(String(saved?.body.toml)).toContain('[script]');

    // The panel reloads the faces after the command, so the new row shows. The closed
    // editor may linger through its leave motion, so the row is asserted inside the card.
    const scenariosCard = screen.getByText('Scenarios').closest('.ant-card') as HTMLElement;
    await waitFor(() => {
      expect(within(scenariosCard).getAllByText('burst').length).toBeGreaterThan(0);
    });
  });

  it('starts a run from a scenario row and speaks to the start route with the fleet config', { timeout: 20000 }, async () => {
    const { fetchMock, posts } = botsFetch({
      '/api/bots/scenarios/sc-1/start': {
        status: 200,
        body: {
          id: 'bots-run-1', scenarioId: 'sc-1', state: 'Starting', startedAt: null,
          finishedAt: null, error: null, deviations: [], snapshot: null,
        },
      },
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const user = await openBots();

    // The row button carries the icon's a11y label, so the name matches loosely here;
    // the modal's own submit has no icon and matches exactly.
    await user.click(screen.getByRole('button', { name: /Start/ }));
    await waitFor(() => expect(screen.getByText('Start run')).toBeInTheDocument());

    const modal = screen.getByText('Start run').closest('.ant-modal') as HTMLElement;
    await user.type(within(modal).getByLabelText('Streamer endpoint'), 'https://streamer-gateway:443');
    await user.type(within(modal).getByLabelText('Ingress base URL'), 'https://platform-gateway:443');
    await user.type(within(modal).getByLabelText('App id'), 'app-1');
    await user.type(within(modal).getByLabelText('Game id'), 'game-bots');
    await user.type(within(modal).getByLabelText('Signing key (hex)'), 'aa'.repeat(32));
    await user.type(within(modal).getByLabelText('Cluster id'), 'cluster-a');
    await user.type(within(modal).getByLabelText('Client certificate (PEM path)'), '/etc/chorus/client.pem');
    await user.type(within(modal).getByLabelText('Client key (PEM path)'), '/etc/chorus/client.key');
    await user.type(within(modal).getByLabelText('Root CA (PEM path)'), '/etc/chorus/root.pem');
    await user.type(within(modal).getByLabelText('Node authority (PEM path)'), '/etc/chorus/node.pem');
    await user.click(within(modal).getByRole('button', { name: 'Start' }));

    await waitFor(() => {
      expect(posts.some((post) => post.path === '/api/bots/scenarios/sc-1/start')).toBe(true);
    });
    const started = posts.find((post) => post.path === '/api/bots/scenarios/sc-1/start');
    expect(started?.body.streamerEndpoint).toBe('https://streamer-gateway:443');
    expect(started?.body.ingressBaseUrl).toBe('https://platform-gateway:443');
    expect(started?.body.appId).toBe('app-1');
    expect(started?.body.gameId).toBe('game-bots');
    expect(started?.body.simulatorSigningKeyHex).toBe('aa'.repeat(32));
    expect(started?.body.clusterId).toBe('cluster-a');
    expect(started?.body.clientCertificatePemPath).toBe('/etc/chorus/client.pem');
    expect(started?.body.clientKeyPemPath).toBe('/etc/chorus/client.key');
    expect(started?.body.rootCaPemPath).toBe('/etc/chorus/root.pem');
    expect(started?.body.nodeAuthorityPemPath).toBe('/etc/chorus/node.pem');
    expect(started?.body.serverRole).toBe('streamer-gateway');
    expect(started?.body.viewerCount).toBe(8);
  });

  it('starts a pressure run through the dialog and speaks to the pressure route with the driver config', { timeout: 20000 }, async () => {
    const { fetchMock, posts } = botsFetch();
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const user = await openBots();

    await user.click(screen.getByRole('button', { name: /New pressure run/ }));
    // The header button shares the dialog's text, so the modal is pinned by its title.
    const modal = await waitFor(() => {
      const title = screen.getByText('New pressure run', { selector: '.ant-modal-title' });
      return title.closest('.ant-modal') as HTMLElement;
    });
    await user.type(within(modal).getByLabelText('Ingress base URL'), 'https://platform-gateway:443');
    await user.clear(within(modal).getByLabelText('Events / second'));
    await user.type(within(modal).getByLabelText('Events / second'), '120');
    // The profile window is the one-click capture: 30s of dotnet-trace over the
    // driver's own process unless the operator names another pid.
    await user.clear(within(modal).getByLabelText('Profile window (s, 0 = off)'));
    await user.type(within(modal).getByLabelText('Profile window (s, 0 = off)'), '30');
    await user.type(within(modal).getByLabelText('Root CA (PEM path, optional)'), '/etc/chorus/root.pem');
    // ByRole matches string names exactly; the row button's icon label stays outside.
    await user.click(within(modal).getByRole('button', { name: 'Start' }));

    await waitFor(() => {
      expect(posts.some((post) => post.path === '/api/pressure/runs')).toBe(true);
    });
    const pressure = posts.find((post) => post.path === '/api/pressure/runs');
    expect(pressure?.body.baseUrl).toBe('https://platform-gateway:443');
    expect(pressure?.body.platform).toBe('douyin');
    expect(pressure?.body.eventsPerSecond).toBe(120);
    expect(pressure?.body.sessions).toBe(4);
    expect(pressure?.body.producerConnections).toBe(4);
    expect(pressure?.body.durationSeconds).toBe(30);
    expect(pressure?.body.signSecret).toBeNull();
    expect(pressure?.body.profileSeconds).toBe(30);
    expect(pressure?.body.profileProcessId).toBe(0);
    expect(pressure?.body.rootCaPemPath).toBe('/etc/chorus/root.pem');
  });
});
