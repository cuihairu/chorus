import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { ConsoleRoot } from '../App';

// The FIX-WEB-16 language face: the console answers in English until the toggle is
// clicked, the click flips every page word to its Chinese pair and stores the choice,
// a stored choice survives reloads, and the toggle names the language it leads to.

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

beforeEach(() => {
  sessionStorage.clear();
  localStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('the console language face', () => {
  it('answers in English by default and flips to Chinese on the toggle', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(401, {})));
    const user = userEvent.setup();

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );
    expect(await screen.findByText('Operator login')).toBeInTheDocument();
    expect(screen.queryByText('操作员登录')).not.toBeInTheDocument();

    // The toggle names the language the click leads to.
    await user.click(screen.getByRole('button', { name: 'Switch language' }));
    expect(await screen.findByText('操作员登录')).toBeInTheDocument();
    expect(screen.queryByText('Operator login')).not.toBeInTheDocument();
    expect(localStorage.getItem('chorus-console-language')).toBe('zh');

    // And the click back names English again.
    await user.click(screen.getByRole('button', { name: 'Switch language' }));
    expect(await screen.findByText('Operator login')).toBeInTheDocument();
    expect(localStorage.getItem('chorus-console-language')).toBe('en');
  });

  it('opens in the stored language and carries the shell words too', async () => {
    localStorage.setItem('chorus-console-language', 'zh');
    const fetchMock = vi.fn(async () => jsonResponse(200, identity));
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(
      <MemoryRouter initialEntries={['/login']}>
        <ConsoleRoot />
      </MemoryRouter>,
    );

    // The stored choice holds before any click, and the login form reads in Chinese.
    expect(await screen.findByText('操作员登录')).toBeInTheDocument();
    await user.type(screen.getByLabelText('操作员令牌'), 'alice-token');
    // antd spreads a space between a two-character CJK button label, so the
    // accessible name is '登 录' — the match tolerates it.
    await user.click(screen.getByRole('button', { name: /登\s*录/ }));

    // The shell renders its menu and session words in Chinese; the toggle now
    // names English as the language it leads to.
    await waitFor(() => expect(screen.getByText('gm')).toBeInTheDocument());
    expect(screen.getAllByText('主页').length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: /退\s*出/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Switch language' })).toHaveTextContent('EN');
  });
});
