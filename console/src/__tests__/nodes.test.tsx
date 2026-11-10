import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { ConsoleRoot } from '../App';

// The FIX-WEB-06 nodes and processes page against the OBS-07 telemetry faces, mocked
// at the fetch boundary: machines and processes render as the faces state them, one
// face's failure is that card's error while the other face still renders, naming a
// node refetches both faces narrowed to it, and refresh refetches.

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

function telemetryFaces() {
  return {
    '/telemetry/machine': {
      status: 200,
      body: [
        {
          nodeId: 'node-1',
          sampledAtUtc: '2026-11-11T00:00:00Z',
          reportSequence: 42,
          cpuPercent: 12.5,
          memory: { totalBytes: 16_000_000_000, usedBytes: 10_000_000_000, usedPercent: 64 },
          load: { l1: 1.5, l5: 1.2, l15: 0.9 },
          disk: [{ mount: '/', totalBytes: 500_000_000_000, usedBytes: 200_000_000_000, usedPercent: 40 }],
          samples: 8,
        },
      ],
    },
    '/telemetry/process': {
      status: 200,
      body: [
        {
          nodeId: 'node-1',
          processesRunning: 2,
          processes: [
            { nodeId: 'node-1', processInstanceId: 'engine-a', role: 'engine', sampledAtUtc: '2026-11-11T00:00:00Z', cpuPercent: 30, memoryUsedBytes: 536_870_912, uptimeSeconds: 90061 },
            { nodeId: 'node-1', processInstanceId: 'gw-b', role: 'gateway', sampledAtUtc: '2026-11-11T00:00:00Z', cpuPercent: 4, memoryUsedBytes: 104_857_600, uptimeSeconds: 3600 },
          ],
        },
      ],
    },
  };
}

function nodesFetch(overrides: Record<string, { status: number; body: unknown }> = {}) {
  const routes = { '/who-am-i': { status: 200, body: gmIdentity }, ...telemetryFaces(), ...overrides };
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

async function openNodes() {
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Operator token'), 'alice-token');
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
  // The shell's menu anchors have no href (navigation is onClick), so click the item
  // text. The menu mounts with the responsive layout's desktop flip — the timeout
  // absorbs that tick under load.
  await user.click(await screen.findByText('Nodes and processes', { exact: true }, { timeout: 5000 }));
  await waitFor(() => expect(screen.getByText('Machines')).toBeInTheDocument());
  return user;
}

beforeEach(() => {
  sessionStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('the nodes and processes page', () => {
  it('renders machines and processes as the faces stated them', { timeout: 20000 }, async () => {
    const { fetchMock, calls } = nodesFetch();
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const user = await openNodes();

    const machinesCard = screen.getByText('Machines').closest('.ant-card') as HTMLElement;
    expect(within(machinesCard).getByText('node-1')).toBeInTheDocument();
    expect(within(machinesCard).getByText('12.5%')).toBeInTheDocument();
    expect(within(machinesCard).getByText('1.50 / 1.20 / 0.90')).toBeInTheDocument();
    expect(within(machinesCard).getByText('8')).toBeInTheDocument();

    // The disk rows ride the expandable row: expand and read the mount.
    const expandIcon = machinesCard.querySelector('.ant-table-row-expand-icon') as HTMLElement;
    await user.click(expandIcon);
    expect(await within(machinesCard).findByText('/')).toBeInTheDocument();
    expect(within(machinesCard).getByText('40.0%')).toBeInTheDocument();

    const processesCard = screen.getByText('Processes').closest('.ant-card') as HTMLElement;
    expect(within(processesCard).getByText('engine-a')).toBeInTheDocument();
    expect(within(processesCard).getByText('30.0%')).toBeInTheDocument();
    expect(within(processesCard).getByText('512 MB')).toBeInTheDocument();
    expect(within(processesCard).getByText('1d 1h')).toBeInTheDocument();
    expect(within(processesCard).getByText('gw-b')).toBeInTheDocument();

    // Both faces answered with the 60-minute window and the bearer header — the
    // telemetry faces live under the control plane's /api group, pinned against drift.
    const machineCalls = calls.filter((path) => path === '/api/telemetry/machine?windowMinutes=60');
    expect(machineCalls).toHaveLength(1);
    const processCalls = calls.filter((path) => path === '/api/telemetry/process?windowMinutes=60');
    expect(processCalls).toHaveLength(1);
    const init = fetchMock.mock.calls.find(([u]) => String(u) === '/api/telemetry/process?windowMinutes=60');
    expect((init?.[1]?.headers as Record<string, string>).Authorization).toBe('Bearer alice-token');
  });

  it('isolates a failing face inside its card and keeps the other face rendered', { timeout: 20000 }, async () => {
    vi.stubGlobal('fetch', nodesFetch({ '/telemetry/process': { status: 500, body: { error: 'boom' } } }).fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    await openNodes();

    const processesCard = screen.getByText('Processes').closest('.ant-card') as HTMLElement;
    expect(within(processesCard).getByText('The face did not answer usable data')).toBeInTheDocument();
    expect(within(processesCard).getByText('500')).toBeInTheDocument();

    // The machines face still answers.
    expect(screen.getByText('node-1')).toBeInTheDocument();
    expect(screen.getByText('12.5%')).toBeInTheDocument();
  });

  it('narrows both faces to the named node and refetches on refresh', { timeout: 20000 }, async () => {
    const { fetchMock, calls } = nodesFetch();
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const user = await openNodes();

    // Name the node from the select; both faces refetch narrowed to it.
    await user.click(screen.getByRole('combobox'));
    await user.click(await screen.findByTitle('node-1'));
    await waitFor(() => {
      expect(calls.filter((path) => path === '/api/telemetry/machine?windowMinutes=60&nodeId=node-1').length).toBe(1);
    });
    expect(calls.filter((path) => path === '/api/telemetry/process?windowMinutes=60&nodeId=node-1').length).toBe(1);

    // Refresh refetches both faces with the same narrowing.
    const machineBefore = calls.filter((path) => path.includes('/telemetry/machine')).length;
    await user.click(screen.getByRole('button', { name: /Refresh/ }));
    await waitFor(() => {
      expect(calls.filter((path) => path.includes('/telemetry/machine')).length).toBe(machineBefore + 1);
    });
    expect(calls.filter((path) => path.includes('nodeId=node-1')).length).toBe(4);
  });
});
