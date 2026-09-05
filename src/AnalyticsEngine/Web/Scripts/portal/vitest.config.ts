import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// Vitest config for the portal's UI tests. Kept separate from vite.config.ts so the production
// build config (base path, output dir) stays focused on shipping assets, and so the test-only
// jsdom environment and setup file never leak into a real build.
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    // The Fluent components pull in a lot of CSS-in-JS; we don't assert on computed styles, so
    // skipping CSS processing keeps the tests fast without changing what they verify.
    css: false,
    include: ['src/**/*.test.{ts,tsx}'],
  },
});
