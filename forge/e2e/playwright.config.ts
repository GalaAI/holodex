import { existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { defineConfig, devices } from "@playwright/test";
import { chromium } from "playwright-core";

/**
 * Playwright against the 4GE studio as Holodex sees it: the server on
 * 127.0.0.1:17400 serving the built studio. `pnpm -C forge build` first;
 * the web server below starts the built server unless one already runs.
 *
 * Data goes to a throwaway FORGE_DATA_ROOT so tests never touch the user's
 * Atlas, Seeds or builds. FORGE_E2E_URL points the suite at another server
 * (the sandbox, for the flow suite). The browser is Playwright's bundled
 * Chromium when installed, else Microsoft Edge (always on Windows).
 */
const port = Number(process.env.FORGE_E2E_PORT ?? 17400);
const baseURL = process.env.FORGE_E2E_URL ?? `http://127.0.0.1:${port}`;
const dataRoot = process.env.FORGE_DATA_ROOT ?? join(tmpdir(), "4ge-e2e-data");
const bundled = existsSync(chromium.executablePath());

export default defineConfig({
  testDir: "./tests",
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  // One worker: the sandbox owns fixed ports (17410 to 17412), so suites take turns.
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: [["list"]],
  outputDir: "test-results",
  use: {
    baseURL,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [
    {
      name: "studio",
      use: {
        ...devices["Desktop Chrome"],
        viewport: { width: 1440, height: 900 },
        ...(bundled ? {} : { channel: "msedge" }),
      },
    },
  ],
  webServer: process.env.FORGE_E2E_URL
    ? undefined
    : {
        command: "node ../apps/server/dist/main.js",
        url: `${baseURL}/health`,
        reuseExistingServer: true,
        timeout: 60_000,
        env: { FORGE_PORT: String(port), FORGE_DATA_ROOT: dataRoot },
        stdout: "pipe",
        stderr: "pipe",
      },
});
