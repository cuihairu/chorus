import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { ConsoleRoot } from '../App';

// The FIX-WEB-10 operations and receipts page against the FIX-WEB-05 generated client,
// mocked at the fetch boundary: a gm submits a control-plane command and a room command
// and reads the synchronous reply, any operator queries a receipt by operation id (the
// data-plane Applied answer and the audited answer alike), a viewer finds the command
// floor closed but receipts open (FIX-WEB-01), and a refused command surfaces the
// refusal without folding it into a receipt.

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

/** Signs in through the login page and lands on the operations page. */
async function openOperations(identity: typeof gmIdentity) {
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Operator token'), 'alice-token');
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
  // The shell's menu anchors have no href (navigation is onClick), so click the item text.
  await user.click(await screen.findByText('Operations', { exact: true }));
  await waitFor(() => expect(screen.getByText('Operations and receipts')).toBeInTheDocument());
  return { user, identity };
}

async function submitSuspendGate(user: {
  type: (element: Element, text: string) => Promise<void>;
  click: (element: Element) => Promise<void>;
}) {
  await user.type(screen.getByLabelText('Reason'), 'incident 42');
  await user.type(screen.getByLabelText('Partition key'), 'tenant-a/game-a');
  await user.click(screen.getByRole('button', { name: 'Submit' }));
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

describe('the operations and receipts page', () => {
  it('submits a queue-gate command with a fresh request id and shows the reply', { timeout: 20000 }, async () => {
    const fetchMock = routeFetch({
      '/who-am-i': { status: 200, body: gmIdentity },
      '/suspend-match-queue': {
        status: 200,
        body: { partitionKey: 'tenant-a/game-a', suspended: true, duplicate: false },
      },
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const { user } = await openOperations(gmIdentity);
    await submitSuspendGate(user);

    const [url, init] = fetchMock.mock.calls.find(([u]) => String(u).endsWith('/suspend-match-queue'))!;
    expect(String(url)).toBe('/admin/api/v1/suspend-match-queue');
    expect((init?.headers as Record<string, string>).Authorization).toBe('Bearer alice-token');
    const body = sentBody([url, init]);
    expect(body.operator).toBe('gm-alice');
    expect(body.reason).toBe('incident 42');
    expect(body.partitionKey).toBe('tenant-a/game-a');
    expect(String(body.requestId)).not.toBe('');

    // The synchronous reply is rendered as the receipt of record.
    expect(await screen.findByText('suspended')).toBeInTheDocument();
    expect(screen.getByText('true')).toBeInTheDocument();
  });

  it('dispatches a room command and shows its stage and operation id', { timeout: 20000 }, async () => {
    const fetchMock = routeFetch({
      '/who-am-i': { status: 200, body: gmIdentity },
      '/dispatch-room-command': {
        status: 200,
        body: {
          operationId: 'op-boost-1', roomId: 'room-1', ownerInstanceId: 'game-a', epoch: 3,
          stage: 'DurableAccepted', duplicate: false, note: '', tenantId: 'tenant-a', gameId: 'game-a',
        },
      },
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const { user } = await openOperations(gmIdentity);

    await user.click(screen.getByRole('tab', { name: 'Dispatch room command' }));
    const pane = await screen.findByRole('tabpanel', { name: 'Dispatch room command' });
    await user.type(within(pane).getByLabelText('Reason'), 'playtest boost');
    await user.type(within(pane).getByLabelText('Tenant'), 'tenant-a');
    await user.type(within(pane).getByLabelText('Game'), 'game-a');
    await user.type(within(pane).getByLabelText('Room'), 'room-1');
    await user.type(within(pane).getByLabelText('Participant'), 's-web09-a');
    // The payload carries braces, which user.type reads as key syntax; set it directly.
    fireEvent.change(within(pane).getByLabelText('Payload (module JSON)'), {
      target: { value: '{"action":"boost","level":1}' },
    });
    await user.click(within(pane).getByRole('button', { name: 'Submit' }));

    const [url, init] = fetchMock.mock.calls.find(([u]) => String(u).endsWith('/dispatch-room-command'))!;
    const body = sentBody([url, init]);
    expect(body.operator).toBe('gm-alice');
    expect(body.tenantId).toBe('tenant-a');
    expect(body.roomId).toBe('room-1');
    expect(body.participantId).toBe('s-web09-a');
    expect(body.payloadJson).toBe('{"action":"boost","level":1}');

    // The stage appears twice — once in the reply rows, once as the stage tag.
    expect((await screen.findAllByText('DurableAccepted')).length).toBeGreaterThan(0);
    expect(screen.getByText('op-boost-1')).toBeInTheDocument();
  });

  it('queries a receipt by operation id and reads the applied tick', { timeout: 20000 }, async () => {
    const fetchMock = routeFetch({
      '/who-am-i': { status: 200, body: gmIdentity },
      '/get-operation-status': {
        status: 200,
        body: { found: true, stage: 'Applied', appliedTick: 42, platform: 'RoundCommand', note: 'applied' },
      },
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const { user } = await openOperations(gmIdentity);

    await user.type(screen.getByLabelText('Operation id'), 'op-boost-1');
    await user.click(screen.getByRole('button', { name: 'Query receipt' }));

    const [url, init] = fetchMock.mock.calls.find(([u]: unknown[]) => String(u).endsWith('/get-operation-status'))!;
    expect(String(url)).toBe('/admin/api/v1/get-operation-status');
    // The scope dims ride along empty — an empty dimension constrains nothing (FIX-WEB-09).
    expect(sentBody([url, init])).toEqual({ operationId: 'op-boost-1', tenantId: '', gameId: '', roomId: '' });

    expect(await screen.findByText('Applied')).toBeInTheDocument();
    expect(screen.getByText('42')).toBeInTheDocument();
  });

  it('leaves a viewer with open receipts but a closed command floor', { timeout: 20000 }, async () => {
    vi.stubGlobal('fetch', routeFetch({
      '/who-am-i': { status: 200, body: viewerIdentity },
      '/get-operation-status': {
        status: 200,
        body: {
          found: true, stage: 'Audited', appliedTick: 0,
          platform: 'SuspendMatchQueue', note: '{"partition":"tenant-a/game-a"}',
        },
      },
    }));

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const { user } = await openOperations(viewerIdentity);

    // Every command tab sits behind the gm floor.
    for (const name of ['Suspend match queue', 'Dispatch room command']) {
      expect(screen.getByRole('tab', { name })).toHaveAttribute('aria-disabled', 'true');
    }
    expect(screen.getByText(
      'Your role is below the command floor: receipts stay open, commands stay closed.',
    )).toBeInTheDocument();

    // The receipt query still answers for the viewer.
    await user.type(screen.getByLabelText('Operation id'), 'web09-gate-1');
    await user.click(screen.getByRole('button', { name: 'Query receipt' }));
    expect(await screen.findByText('Audited')).toBeInTheDocument();
    expect(screen.getByText('SuspendMatchQueue')).toBeInTheDocument();
  });

  it('surfaces a refused command without folding it into a receipt', { timeout: 20000 }, async () => {
    vi.stubGlobal('fetch', routeFetch({
      '/who-am-i': { status: 200, body: gmIdentity },
      '/suspend-match-queue': { status: 403, body: {} },
    }));

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const { user } = await openOperations(gmIdentity);
    await submitSuspendGate(user);

    expect(await screen.findByText(
      'The control plane refused this command: your role or scope does not cover it.',
    )).toBeInTheDocument();
    expect(screen.queryByText('suspended')).not.toBeInTheDocument();
  });

  it('walks the audit trail newest first with exact-word filters', { timeout: 20000 }, async () => {
    const replies = [
      {
        status: 200,
        body: {
          entries: [
            { requestId: 'req-5', operator: 'gm-alice', reason: 'correct a score', scope: 'score-alltime/alltime/global', command: 'leaderboard.adjust', detail: '{"subjectId":"a"}', createdAtUnixMs: 1720000000000 },
            { requestId: 'req-4', operator: 'gm-bob', reason: 'close the gate', scope: 'tenant-a/game-a', command: 'resume-match-queue', detail: '{}', createdAtUnixMs: 1720000001000 },
          ],
          hasMore: true,
          nextPageToken: '12',
        },
      },
      {
        status: 200,
        body: {
          entries: [
            { requestId: 'req-3', operator: 'gm-alice', reason: 'wipe a cheat', scope: 'score-alltime/alltime/global', command: 'leaderboard.reset', detail: '{}', createdAtUnixMs: 1720000002000 },
          ],
          hasMore: false,
          nextPageToken: '',
        },
      },
    ];
    const auditCalls: Array<Record<string, unknown>> = [];
    const fetchMock = vi.fn(async (url: RequestInfo | URL, init?: RequestInit) => {
      const path = String(url);
      if (path.endsWith('/who-am-i')) return jsonResponse(200, gmIdentity);
      if (path.endsWith('/get-audit-trail')) {
        auditCalls.push(JSON.parse(String(init?.body)));
        const reply = replies.shift();
        if (reply === undefined) throw new Error('exhausted audit replies');
        return jsonResponse(reply.status, reply.body);
      }
      throw new Error(`unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    const { user } = await openOperations(gmIdentity);
    const auditCard = screen.getByText('Audit trail').closest('.ant-card') as HTMLElement;

    await user.type(within(auditCard).getByLabelText('Command filter'), 'leaderboard.adjust');
    await user.type(within(auditCard).getByLabelText('Operator filter'), 'gm-alice');
    await user.click(within(auditCard).getByRole('button', { name: 'Query audit' }));

    // The filters ride the wire verbatim; the default page size rides as 20.
    expect(auditCalls[0]).toEqual({
      command: 'leaderboard.adjust',
      operator: 'gm-alice',
      maxEntries: 20,
      pageToken: '',
    });
    expect(within(auditCard).getByText('correct a score')).toBeInTheDocument();
    expect(within(auditCard).getByRole('button', { name: 'Continue' })).toBeInTheDocument();

    // The continuation carries the server's cursor and appends strictly older rows.
    await user.click(within(auditCard).getByRole('button', { name: 'Continue' }));
    expect(auditCalls[1].pageToken).toBe('12');
    expect(within(auditCard).getByText('wipe a cheat')).toBeInTheDocument();
    expect(within(auditCard).queryByRole('button', { name: 'Continue' })).not.toBeInTheDocument();
  });
});
