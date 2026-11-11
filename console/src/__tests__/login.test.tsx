import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { ConsoleRoot } from '../App';

// The FIX-WEB-04 login page against the FIX-WEB-03 browser face contract, mocked at
// the fetch boundary: the who-am-i probe signs the operator in and shows their role,
// a refused credential stays on the login page with the refusal word, an unreachable
// control plane says so, and the console routes without a session fall back to login.

const identity = {
  operatorId: 'gm-alice',
  role: 'gm',
  tenants: [],
  games: [],
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

async function signIn() {
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Operator token'), 'alice-token');
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
}

beforeEach(() => {
  sessionStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('the operator login page', () => {
  it('signs an operator in and carries their identity into the console', async () => {
    const fetchMock = vi.fn(async (_url: RequestInfo | URL, _init?: RequestInit) =>
      jsonResponse(200, identity));
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    await signIn();

    // The probe was the contract route with the credential as the bearer header.
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toBe('/admin/api/v1/who-am-i');
    expect((init?.headers as Record<string, string>).Authorization).toBe('Bearer alice-token');

    // The shell is visible with the operator's role, not the login card. The menu
    // words arrive with the responsive layout's desktop flip, so wait for them.
    await waitFor(() => expect(screen.getByText('gm')).toBeInTheDocument(), { timeout: 10000 });
    await waitFor(() => expect(screen.getAllByText('Home').length).toBeGreaterThan(0), { timeout: 10000 });
    expect(screen.queryByText('Operator login')).not.toBeInTheDocument();
    expect(sessionStorage.getItem('chorus.operator-token')).toBe('alice-token');
  });

  it('refuses a rejected credential on the login page', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(401, {})));

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    await signIn();

    expect(await screen.findByText('The control plane refused this credential.')).toBeInTheDocument();
    expect(screen.getByText('Operator login')).toBeInTheDocument();
    expect(sessionStorage.getItem('chorus.operator-token')).toBeNull();
  });

  it('reports an unreachable control plane as unreachable', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(503, {})));

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    await signIn();

    expect(
      await screen.findByText('The control plane is unreachable. Try again shortly.'),
    ).toBeInTheDocument();
    expect(screen.getByText('Operator login')).toBeInTheDocument();
  });

  it('falls back to the login page when there is no session', async () => {
    render(
      <MemoryRouter initialEntries={['/rooms']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );

    expect(await screen.findByText('Operator login')).toBeInTheDocument();
    expect(screen.queryByText('Rooms')).not.toBeInTheDocument();
  });

  it('restores a stored session by re-probing who-am-i', async () => {
    sessionStorage.setItem('chorus.operator-token', 'alice-token');
    const fetchMock = vi.fn(async () => jsonResponse(200, identity));
    vi.stubGlobal('fetch', fetchMock);

    render(
      <MemoryRouter initialEntries={['/home']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );

    // The gate resolves the stored token against the control plane before showing pages.
    await waitFor(() => expect(screen.getByText('gm')).toBeInTheDocument(), { timeout: 10000 });
    expect(fetchMock).toHaveBeenCalled();
  });

  it('signs the operator out and returns to the login page', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(200, identity)));
    const user = userEvent.setup();

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    await signIn();
    await waitFor(() => expect(screen.getByText('gm')).toBeInTheDocument(), { timeout: 10000 });

    await user.click(screen.getByRole('button', { name: /Sign out/ }));
    expect(await screen.findByText('Operator login')).toBeInTheDocument();
    expect(sessionStorage.getItem('chorus.operator-token')).toBeNull();
  });
});
