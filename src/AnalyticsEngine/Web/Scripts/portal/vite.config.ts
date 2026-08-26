import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The built app is served by the ASP.NET site from /Scripts/portal/build/, so every
// emitted asset URL must resolve under that path (mirrors the old CRA "homepage" setting).
// We also keep the output directory named "build" so the existing MSBuild / HomeController
// wiring (which reads build/index.html) does not need to change.
export default defineConfig({
  plugins: [react()],
  base: '/Scripts/portal/build/',
  build: {
    outDir: 'build',
    emptyOutDir: true,
  },
});
