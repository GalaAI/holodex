import react from "@vitejs/plugin-react";
import { defaultClientConditions, defaultServerConditions } from "vite";
import { defineConfig } from "vitest/config";

/**
 * One vitest run for the whole workspace (`pnpm test`). Workspace packages
 * resolve to their TypeScript source through the "@4ge/source" export
 * condition, so tests never need a build first. Node is the default
 * environment; a React test opts into the DOM with
 * `// @vitest-environment happy-dom` on its first line.
 */
export default defineConfig({
  plugins: [react()],
  resolve: {
    conditions: ["@4ge/source", ...defaultClientConditions],
  },
  ssr: {
    resolve: {
      conditions: ["@4ge/source", ...defaultServerConditions],
      externalConditions: ["@4ge/source"],
    },
  },
  test: {
    include: [
      "packages/*/src/**/*.test.{ts,tsx}",
      "apps/*/src/**/*.test.{ts,tsx}",
      "sandbox/*/src/**/*.test.{ts,tsx}",
    ],
    environment: "node",
    setupFiles: ["./vitest.setup.ts"],
    testTimeout: 20_000,
    hookTimeout: 30_000,
  },
});
