// @vitest-environment happy-dom
import type { ChatMessage, Seed, Session } from "@4ge/contracts";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { RouterProvider, createMemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { routes } from "../../router.js";
import { FakeEventSource, fakeServer } from "../../shell/testing.js";

const AT = "2026-10-30T12:00:00.000Z";
const CONNECTED = {
  route: {
    provider: "nebius",
    baseUrl: "https://api.tokenfactory.nebius.com/v1",
    models: { lead: "nvidia/nemotron-3-super-120b-a12b", worker: "nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B", vision: "nvidia/Nemotron-3.5-Super-VL" },
    reason: "Your Nebius key is set.",
    checkedAt: AT,
  },
  models: [],
};

const SESSION: Session = {
  id: "ses_im1",
  parentId: null,
  rootId: "ses_im1",
  agent: "prometheus",
  kind: "imagistation",
  task: "A website for my tattoo shop",
  status: "waiting",
  createdAt: AT,
  updatedAt: AT,
};

const MESSAGES: ChatMessage[] = [
  { id: "msg_1", sessionId: "ses_im1", role: "user", content: "A website for my tattoo shop", createdAt: AT },
  { id: "msg_2", sessionId: "ses_im1", role: "assistant", agent: "prometheus", content: "Who books with you most, walk-ins or returning clients?", createdAt: "2026-10-30T12:00:05.000Z" },
];

const SEED: Seed = {
  id: "seed_t1",
  title: "Tattoo shop site",
  idea: "A website for my tattoo shop",
  problem: "Bookings live in DMs and get lost",
  audience: "Returning clients and walk-ins",
  successSignal: "Half of bookings come through the site",
  kind: "website",
  platforms: ["web"],
  features: ["Bookings", "Flash gallery"],
  constraints: [],
  references: [],
  answers: [],
  blueprintId: "blueprint:website",
  blueprintMatch: { blueprintId: "blueprint:website", score: 0.92, reason: "A marketing site with bookings is what the website Blueprint builds." },
  parameters: {},
  status: "ready",
  decision: "pending",
  imagistationSessionId: "ses_im1",
  createdAt: AT,
  updatedAt: AT,
};

/** The kit's TextArea wraps its textarea in the label; find it by the label text. */
function field(label: string): HTMLTextAreaElement {
  const element = screen.getByText(label).closest("label")?.querySelector("textarea");
  if (!element) throw new Error(`No field labelled ${label}`);
  return element;
}

function open(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  render(<RouterProvider router={router} />);
  return router;
}

beforeEach(() => FakeEventSource.install());
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("Imagistation", () => {
  it("will not start without an idea, then opens a Prometheus session", async () => {
    const seen = fakeServer([
      { method: "GET", path: "/models", handler: () => CONNECTED },
      { method: "GET", path: "/imagistation/sessions", handler: () => ({ sessions: [] }) },
      { method: "POST", path: "/imagistation/sessions", handler: () => ({ session: SESSION, messages: MESSAGES.slice(0, 1) }) },
      { method: "GET", path: "/imagistation/sessions/:id", handler: () => ({ session: SESSION, messages: MESSAGES }) },
      { method: "GET", path: "/seeds", handler: () => ({ seeds: [] }) },
      { method: "GET", path: "/atlas/blueprints", handler: () => ({ entries: [] }) },
    ]);
    const router = open("/imagistation");
    const start = (await screen.findByTestId("start-idea")) as HTMLButtonElement;
    expect(start.disabled).toBe(true);
    expect(screen.getByText("Say what you want to build first.")).toBeTruthy();
    fireEvent.change(field("What do you want to build?"), { target: { value: "A website for my tattoo shop" } });
    expect(start.disabled).toBe(false);
    fireEvent.click(start);
    await waitFor(() => expect(router.state.location.pathname).toBe("/imagistation/ses_im1"));
    expect(await screen.findByText("Who books with you most, walk-ins or returning clients?")).toBeTruthy();
    expect(seen.find((request) => request.method === "POST")?.body).toEqual({ idea: "A website for my tattoo shop" });
  });

  it("writes the Seed Scroll with the Blueprint pick and sends it to AInbox for later", async () => {
    const seen = fakeServer([
      { method: "GET", path: "/models", handler: () => CONNECTED },
      { method: "GET", path: "/imagistation/sessions", handler: () => ({ sessions: [SESSION] }) },
      { method: "GET", path: "/imagistation/sessions/:id", handler: () => ({ session: SESSION, messages: MESSAGES }) },
      { method: "GET", path: "/seeds", handler: () => ({ seeds: [] }) },
      { method: "GET", path: "/atlas/blueprints", handler: () => ({ entries: [] }) },
      { method: "POST", path: "/imagistation/sessions/:id/seed", handler: () => ({ seed: SEED }) },
      {
        method: "POST",
        path: "/seeds/:id/decision",
        handler: (request) => ({
          seed: {
            ...SEED,
            status: "queued",
            decision: "later",
            ainboxItem: "4ge-seed_t1.json",
            reminderNote: (request.body as { reminderNote?: string }).reminderNote,
          },
        }),
      },
    ]);
    open("/imagistation/ses_im1");
    await screen.findByText("Who books with you most, walk-ins or returning clients?");
    fireEvent.click(screen.getByTestId("write-seed"));
    expect(await screen.findByTestId("seed-scroll")).toBeTruthy();
    expect(screen.getByText("A marketing site with bookings is what the website Blueprint builds.")).toBeTruthy();
    expect(screen.getByText("92% fit")).toBeTruthy();
    fireEvent.click(screen.getByText("Later"));
    fireEvent.change(field("Reminder note for AInbox"), { target: { value: "Block Sunday morning" } });
    fireEvent.click(screen.getByText("Send to AInbox"));
    const later = await screen.findByTestId("seed-later");
    expect(later.textContent).toContain("Sent to AInbox");
    expect(later.textContent).toContain("4ge-seed_t1.json");
    expect(later.textContent).toContain("Reminder: Block Sunday morning");
    expect(seen.find((request) => request.path === "/seeds/seed_t1/decision")?.body).toEqual({
      when: "later",
      reminderNote: "Block Sunday morning",
    });
  });

  it("explains a model error from Prometheus and keeps the message", async () => {
    fakeServer([
      { method: "GET", path: "/models", handler: () => CONNECTED },
      { method: "GET", path: "/imagistation/sessions", handler: () => ({ sessions: [SESSION] }) },
      { method: "GET", path: "/imagistation/sessions/:id", handler: () => ({ session: SESSION, messages: MESSAGES }) },
      { method: "GET", path: "/seeds", handler: () => ({ seeds: [] }) },
      { method: "GET", path: "/atlas/blueprints", handler: () => ({ entries: [] }) },
      {
        method: "POST",
        path: "/imagistation/sessions/:id/messages",
        handler: () =>
          new Response(JSON.stringify({ error: { code: "model_error", message: "Nebius answered 500" } }), { status: 502 }),
      },
    ]);
    open("/imagistation/ses_im1");
    await screen.findByText("Who books with you most, walk-ins or returning clients?");
    const input = field("Message Prometheus") as HTMLTextAreaElement;
    fireEvent.change(input, { target: { value: "Mostly returning clients" } });
    fireEvent.click(screen.getByText("Send"));
    expect(await screen.findByText("The model answered with an error")).toBeTruthy();
    expect(screen.getByText("Nebius answered 500")).toBeTruthy();
    await waitFor(() => expect(input.value).toBe("Mostly returning clients"));
  });
});
