import { defineConfig } from 'vite';

// The renderer builds to dist/renderer with relative asset paths so Electron can load it from disk.
export default defineConfig({
  base: './',
  build: { outDir: 'dist/renderer', emptyOutDir: true },
});
