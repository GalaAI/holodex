import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// The client builds to dist/client; in dev, /api goes to the API server on port 3000.
export default defineConfig({
  plugins: [react()],
  build: { outDir: 'dist/client', emptyOutDir: true },
  server: {
    host: '127.0.0.1',
    proxy: { '/api': 'http://127.0.0.1:3000' },
  },
});
