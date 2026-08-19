/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Test-only Vite config. Kept separate from vite.config.ts so the dev-server settings there
// (HTTPS certs, SPA proxy) are not needed just to run unit tests in CI.
export default defineConfig({
    plugins: [react()],
    test: {
        environment: 'jsdom',
        globals: true,
        setupFiles: ['./src/test/setup.ts'],
        include: ['src/**/*.{test,spec}.{ts,tsx}'],
        server: {
            deps: {
                // @fluentui/react-icons ships extensionless ESM chunk imports that Vite's node
                // resolver cannot follow, so it has to be transformed rather than externalised.
                inline: [/@fluentui/],
            },
        },
        coverage: {
            provider: 'v8',
            reporter: ['text-summary', 'cobertura'],
            include: ['src/**/*.{ts,tsx}'],
            exclude: ['src/**/*.{test,spec}.{ts,tsx}', 'src/test/**', 'src/main.tsx', 'src/vite-env.d.ts'],
        },
    },
});
