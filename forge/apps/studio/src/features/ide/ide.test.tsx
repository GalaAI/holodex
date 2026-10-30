// @vitest-environment happy-dom
import type { Build, Checkpoint } from "@4ge/contracts";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { RouterProvider, createMemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { routes } from "../../router.js";
import { FakeEventSource, apiError, fakeServer, type FakeRoute } from "../../shell/testing.js";
import { AT, CHECKPOINT, SESSIONS, fixtureBuild } from "./fixtures.js";
import { buildBoard, effectiveTone, filterCounts, matchesFilter } from "./frames.js";
import { applyEvent, initialView } from "./state.js";

const NO_MODEL = {
  route: { provider: "none", models: { lead: "nvidia/nemotron-3-super-120b-a12b", worker: "nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B", vision: "nvidia/Nemotron-3.5-Super-VL" }, reason: "No NEBIUS_API_KEY or Hivemind is set for the 4GE server.", checkedAt: AT },
  models: [],
};

function serverFor(build: Build, extra: FakeRoute[] = []) {
  return fakeServer([
    ...extra,
    { method: "GET", path: "/models", handler: () => NO_MODEL },
    { method: "GET", path: "/builds/:id", handler: () => ({ build }) },
    { method: "GET", path: "/sessions", handler: () => ({ sessions: SESSIONS }) },
    { method: "GET", path: "/sessions/:id", handler: (request) => ({ session: SESSIONS.find((session) => request.path.endsWith(session.id)), messages: [] }) },
    { method: "GET", path: "/builds/:id/checkpoints", handler: () => ({ checkpoints: [CHECKPOINT] }) },
    { method: "GET", path: "/builds/:id/files", handler: () => new Response("table bookings (id, artist, slot)", { status: 200 }) },
  ]);
}

function open(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  render(<RouterProvider router={router} />);
  return router;
}

beforeEach(() => {
  FakeEventSource.install();
  window.localStorage.clear();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("IDE state", () => {
  it("folds stream events into the build", () => {
    let view = initialView(fixtureBuild());
    view = applyEvent(view, {
      id: "1",
      at: AT,
      type: "layer.updated",
      data: { layer: { ...fixtureBuild().layers[1]!, status: "failed", error: "The booking API did not answer" } },
    });
    expect(view.build.layers[1]?.status).toBe("failed");
    view = applyEvent(view, { id: "2", at: AT, type: "checkpoint.resolved", data: { checkpoint: { ...CHECKPOINT, status: "approved" } } });
    expect(view.build.openCheckpointId).toBeUndefined();
    view = applyEvent(view, { id: "3", at: AT, type: "build.status", data: { buildId: "bld_t1", status: "cancelled" } });
    expect(view.build.status).toBe("cancelled");
    expect(view.build.finishedAt).toBe(AT);
    view = applyEvent(view, { id: "4", at: AT, type: "build.status", data: { buildId: "bld_other", status: "passed" } });
    expect(view.build.status).toBe("cancelled");
  });

  it("lays out one row per stage with a stage frame and a frame per preview", () => {
    const board = buildBoard(fixtureBuild());
    expect(board.rows.map((row) => row.stage)).toEqual(["architecture", "orchestration", "interface", "deploy"]);
    expect(board.frames.map((frame) => frame.id)).toEqual([
      "stage:architecture",
      "preview:lyr_data:previews/schema.txt",
      "stage:orchestration",
      "stage:interface",
      "stage:deploy",
    ]);
    const preview = board.frames[1]!;
    expect(preview.tone).toBe("ok");
    expect(effectiveTone(preview, "broken")).toBe("err");
    expect(matchesFilter(preview, undefined, "unreviewed")).toBe(true);
    expect(filterCounts(board.frames, { [preview.id]: "work" })).toEqual({ all: 5, unreviewed: 4, work: 4, broken: 0 });
  });
});

describe("IDE", () => {
  it("shows the build, the stage strip, the agent tree and the open checkpoint", async () => {
    serverFor(fixtureBuild());
    open("/ide/bld_t1");
    expect(await screen.findByRole("heading", { level: 1, name: "Tattoo shop site" })).toBeTruthy();
    const strip = screen.getByTestId("stage-strip");
    expect(within(strip).getByText("Data model")).toBeTruthy();
    expect(within(strip).getByText("Booking flow")).toBeTruthy();
    expect(await screen.findByTestId("checkpoint")).toBeTruthy();
    expect(screen.getByText("Does the data model cover how your shop books?")).toBeTruthy();
    const tree = await screen.findByTestId("agent-tree");
    expect(within(tree).getByText("Smith")).toBeTruthy();
    expect(within(tree).getByText("Wiring the booking requests")).toBeTruthy();
    expect(screen.getByTestId("frame-stage:architecture")).toBeTruthy();
    expect(await screen.findByText("table bookings (id, artist, slot)")).toBeTruthy();
  });

  it("says plainly when no model is connected", async () => {
    serverFor(fixtureBuild());
    open("/ide/bld_t1");
    const notice = await screen.findByTestId("model-notice");
    expect(notice.textContent).toContain("Smith has no model to think with.");
    expect(notice.textContent).toContain("No NEBIUS_API_KEY or Hivemind is set for the 4GE server.");
  });

  it("approves a checkpoint through the server and closes it", async () => {
    const approved: Checkpoint = { ...CHECKPOINT, status: "approved", decision: { decision: "approve", decidedAt: AT } };
    const seen = serverFor(fixtureBuild(), [
      {
        method: "POST",
        path: "/builds/:id/checkpoints/:checkpointId",
        handler: () => {
          const { openCheckpointId: _open, ...rest } = fixtureBuild({ status: "running" });
          return { checkpoint: approved, build: rest };
        },
      },
    ]);
    open("/ide/bld_t1");
    fireEvent.click(await screen.findByTestId("checkpoint-approve"));
    await waitFor(() => expect(screen.queryByTestId("checkpoint")).toBeNull());
    const post = seen.find((request) => request.method === "POST");
    expect(post?.path).toBe("/builds/bld_t1/checkpoints/chk_1");
    expect(post?.body).toEqual({ decision: "approve" });
  });

  it("follows the build stream and explains a cancelled build", async () => {
    serverFor(fixtureBuild());
    open("/ide/bld_t1");
    await screen.findByRole("heading", { level: 1, name: "Tattoo shop site" });
    await waitFor(() => expect(FakeEventSource.instances.some((source) => source.url.includes("/builds/bld_t1/stream"))).toBe(true));
    FakeEventSource.emit("/builds/bld_t1/stream", { id: "9", at: AT, type: "build.status", data: { buildId: "bld_t1", status: "cancelled" } });
    expect(await screen.findByText(/This build was cancelled/)).toBeTruthy();
    expect(screen.queryByTestId("cancel-build")).toBeNull();
    // A cancelled build takes no more input, so its open checkpoint is withdrawn.
    expect(screen.queryByTestId("checkpoint")).toBeNull();
  });

  it("selects a frame, sets a verdict with a key and filters by it", async () => {
    serverFor(fixtureBuild());
    open("/ide/bld_t1");
    const frame = await screen.findByTestId("frame-preview:lyr_data:previews/schema.txt");
    fireEvent.click(frame.querySelector(".frame__body") as Element);
    expect(await screen.findByTestId("inspector")).toBeTruthy();
    fireEvent.keyDown(window, { key: "3" });
    await waitFor(() => expect(frame.className).toContain("is-err"));
    expect(screen.getByTestId("filter-broken").textContent).toContain("1");
    fireEvent.keyDown(window, { key: "Escape" });
    await waitFor(() => expect(screen.queryByTestId("inspector")).toBeNull());
  });

  it("shows a plain error when the build cannot load", async () => {
    fakeServer([
      { method: "GET", path: "/models", handler: () => NO_MODEL },
      { method: "GET", path: "/builds/:id", handler: () => apiError(404, "not_found", "There is no build bld_gone") },
    ]);
    open("/ide/bld_gone");
    expect(await screen.findByRole("heading", { level: 1, name: "This build could not open" })).toBeTruthy();
    expect(screen.getByText("There is no build bld_gone")).toBeTruthy();
  });
});
