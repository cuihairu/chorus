/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// The console builds as a standalone SPA. WEB-18 decides the production hosting
// (static assets served by the ControlServer); for now the dev server proxies the
// browser Admin API prefix to a local ControlServer so pages work against a real
// cluster during development. The page tests (from FIX-WEB-04 on) run in jsdom and
// mock the fetch boundary — the browser face itself is host-tested server-side.
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    setupFiles: './src/test-setup.ts',
  },
  server: {
    proxy: {
      '/admin/api': {
        target: process.env.CHORUS_CONSOLE_CONTROL ?? 'https://localhost:5000',
        changeOrigin: true,
        secure: false,
      },
    },
  },
});
