// Page-level verification arrives with the page tasks (the WEB-02 boundary); this
// file is the entry the vitest jsdom environment loads before every page test.
import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach } from 'vitest';

// Vitest runs with globals: false, so testing-library's automatic cleanup never
// registers itself — every page test unmounts its tree explicitly.
afterEach(() => {
  cleanup();
});

// jsdom implements no matchMedia; antd's responsive observer subscribes to it on
// mount. Answer min-width queries as matched so the desktop layout renders — the
// default viewport is a desktop window and a mobile-resolved ProLayout hides the
// header chrome the page tests interact with.
if (typeof window.matchMedia !== 'function') {
  window.matchMedia = ((query: string) => ({
    matches: query.includes('min-width'),
    media: query,
    onchange: null,
    addListener: () => undefined,
    removeListener: () => undefined,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    dispatchEvent: () => false,
  })) as typeof window.matchMedia;
}
