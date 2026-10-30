import react from "@vitejs/plugin-react";
import { defaultClientConditions, defineConfig } from "vite";

/**
 * Studio dev server on 127.0.0.1:17401 (never an Ark port). /api goes to
 * the 4GE server on 17400, or FORGE_SERVER_URL. Workspace packages resolve
 * to their TypeScript source through the "@4ge/source" condition, so no
 * package build is needed during development.
 */
const serverUrl = process.env.FORGE_SERVER_URL ?? "http://127.0.0.1:17400";

export default defineConfig({
  plugins: [react()],
  resolve: {
    conditions: ["@4ge/source", ...defaultClientConditions],
  },
  server: {
    host: "127.0.0.1",
    port: 17401,
    strictPort: true,
    proxy: {
      "/api": { target: serverUrl, changeOrigin: false },
    },
  },
  preview: {
    host: "127.0.0.1",
    port: 17401,
    strictPort: true,
  },
  build: {
    outDir: "dist",
    emptyOutDir: true,
    sourcemap: true,
  },
});
