// @vitest-environment happy-dom
import type { SandboxRun, TestFlight } from "@4ge/contracts";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { RouterProvider, createMemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fixtureBuild } from "../ide/fixtures.js";
import { routes } from "../../router.js";
import { FakeEventSource, fakeServer } from "../../shell/testing.js";

const AT = "2026-10-30T12:00:00.000Z";
const NO_MODEL = { route: { provider: "none", models: { lead: "nvidia/nemotron-3-super-120b-a12b", worker: "nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B", vision: "nvidia/Nemotron-3.5-Super-VL" }, reason: "No key.", checkedAt: AT }, models: [] };

const RUN: SandboxRun = {
  id: "run_1",
  buildId: "bld_t1",
  status: "running",
  ports: { app: 17410, cdp: 17411, viewer: 17412 },
  composeProject: "4ge-bld_t1",
  appUrl: "http://127.0.0.1:17410/",
  startedAt: AT,
};

const FLIGHT: TestFlight = {
  id: "tfl_1",
  buildId: "bld_t1",
  runId: "run_1",
  status: "running",
  personas: [
    { id: "staff", role: "staff", name: "Rae", brief: "Front desk at the shop.", goals: ["Confirm a booking"] },
    { id: "customer", role: "customer", name: "Sam", brief: "Wants a small flash piece.", goals: ["Book a slot"] },
  ],
  steps: [
    { id: "s1", personaId: "customer", action: "Opens the flash gallery", expectation: "Flash pieces are listed", status: "passed", screenshot: "evidence/s1.png", at: AT },
    { id: "s2", personaId: "staff", action: "Confirms Sam's booking", expectation: "The booking shows as confirmed", status: "failed", error: "No confirm button on the booking", at: AT },
  ],
  layers: [],
  startedAt: AT,
};

function status(available: boolean) {
  return {
    docker: available ? { available: true, version: "27.3.1" } : { available: false, message: "The Docker engine is not running." },
    ports: [
      { name: "app", port: 17410, free: true },
      { name: "browser", port: 17411, free: false },
    ],
    active: [],
  };
}

function open(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  render(<RouterProvider router={router} />);
}

beforeEach(() => {
  FakeEventSource.install();
  window.localStorage.clear();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("Sandbox", () => {
  it("says Docker is down and will not start the sandbox", async () => {
    fakeServer([
      { method: "GET", path: "/models", handler: () => NO_MODEL },
      { method: "GET", path: "/sandbox/status", handler: () => status(false) },
      { method: "GET", path: "/builds/:id", handler: () => ({ build: fixtureBuild() }) },
      { method: "GET", path: "/sandbox/runs", handler: () => ({ runs: [] }) },
    ]);
    open("/sandbox/bld_t1");
    expect(await screen.findByText("Docker is not running")).toBeTruthy();
    expect(screen.getByText("The Docker engine is not running.")).toBeTruthy();
    expect(screen.getByText("browser 17411 taken")).toBeTruthy();
    await waitFor(() => expect((screen.getByTestId("sandbox-start") as HTMLButtonElement).disabled).toBe(true));
  });

  it("explains a taken port when the sandbox fails to start", async () => {
    fakeServer([
      { method: "GET", path: "/models", handler: () => NO_MODEL },
      { method: "GET", path: "/sandbox/status", handler: () => status(true) },
      { method: "GET", path: "/builds/:id", handler: () => ({ build: fixtureBuild() }) },
      { method: "GET", path: "/sandbox/runs", handler: () => ({ runs: [] }) },
      {
        method: "POST",
        path: "/sandbox/start",
        handler: () => ({
          run: { ...RUN, status: "failed", failure: { code: "port_taken", message: "Port 17411 is in use.", port: 17411 } },
        }),
      },
    ]);
    open("/sandbox/bld_t1");
    const start = (await screen.findByTestId("sandbox-start")) as HTMLButtonElement;
    await waitFor(() => expect(start.disabled).toBe(false));
    fireEvent.click(start);
    expect(await screen.findByText("A sandbox port is taken by another program.")).toBeTruthy();
    expect(screen.getByText(/Port 17411 is in use. Close the program/)).toBeTruthy();
  });

  it("runs a test flight and shows each persona's steps with screenshots", async () => {
    const seen = fakeServer([
      { method: "GET", path: "/models", handler: () => NO_MODEL },
      { method: "GET", path: "/sandbox/status", handler: () => status(true) },
      { method: "GET", path: "/builds/:id", handler: () => ({ build: fixtureBuild() }) },
      { method: "GET", path: "/sandbox/runs", handler: () => ({ runs: [RUN] }) },
      { method: "POST", path: "/sandbox/test-flight", handler: () => ({ testFlight: FLIGHT }) },
      { method: "GET", path: "/sandbox/test-flight/:id", handler: () => ({ testFlight: FLIGHT }) },
    ]);
    open("/sandbox/bld_t1");
    const fly = (await screen.findByTestId("testflight-start")) as HTMLButtonElement;
    await waitFor(() => expect(fly.disabled).toBe(false));
    fireEvent.click(fly);
    expect(await screen.findByText("Opens the flash gallery")).toBeTruthy();
    expect(screen.getByText("No confirm button on the booking")).toBeTruthy();
    expect(screen.getByText("Staff user")).toBeTruthy();
    expect(screen.getByText("Customer user")).toBeTruthy();
    const shot = screen.getByAltText("Screenshot after: Opens the flash gallery") as HTMLImageElement;
    expect(shot.getAttribute("src")).toBe("/api/builds/bld_t1/files?path=evidence%2Fs1.png");
    expect(seen.find((request) => request.path === "/sandbox/test-flight")?.body).toEqual({ buildId: "bld_t1", runId: "run_1" });
  });
});
