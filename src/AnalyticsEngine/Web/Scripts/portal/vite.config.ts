import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// MSBuild copies the build output to wwwroot, served by ASP.NET Core at the site root.
// Keep the output directory named "build" to match the existing MSBuild copy target.
export default defineConfig({
  plugins: [react()],
  base: '/',
  build: {
    outDir: 'build',
    emptyOutDir: true,
  },
});
