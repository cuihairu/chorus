import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// The console builds as a standalone SPA. WEB-18 decides the production hosting
// (static assets served by the ControlServer); for now the dev server proxies the
// browser Admin API prefix to a local ControlServer so pages work against a real
// cluster during development.
export default defineConfig({
  plugins: [react()],
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
