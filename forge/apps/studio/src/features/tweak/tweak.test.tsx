// @vitest-environment happy-dom
import {
  TWEAK_KINDS,
  makeId,
  tweakOpSchema,
  type Annotation,
  type AppliedTweak,
  type ComponentInventoryItem,
} from "@4ge/contracts";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { RouterProvider, createMemoryRouter } from "react-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import { routes } from "../../router.js";
import { AnnotationLayer, InventoryPicker, TweakPanel, buildTweakOp, smithBrief, targetForElement } from "./index.js";

const buildId = makeId("build", "studio");
const smithSession = makeId("session", "smith");
const at = "2026-10-30T12:00:00.000Z";

interface Call {
  method: string;
  path: string;
  body: unknown;
}

/** A tiny in-memory 4GE server for the design routes; the real server is tested in apps/server. */
function stubServer() {
  const calls: Call[] = [];
  const tweaks: AppliedTweak[] = [];
  const annotations: Annotation[] = [];
  const inventory: ComponentInventoryItem[] = [];
  const json = (data: unknown, status = 200) => new Response(JSON.stringify(data), { status, headers: { "content-type": "application/json" } });
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), "http://4ge.test");
      const method = (init?.method ?? "GET").toUpperCase();
      const body = init?.body ? JSON.parse(String(init.body)) : undefined;
      calls.push({ method, path: url.pathname, body });
      const p = url.pathname;
      if (p === "/api/design/tweaks" && method === "GET") return json({ tweaks });
      if (p === "/api/design/tweaks" && method === "POST") {
        const tweak: AppliedTweak = {
          id: makeId("tweak", `t${tweaks.length + 1}`),
          op: tweakOpSchema.parse(body),
          status: "applied",
          by: "user",
          changedFiles: ["src/Hero.tsx"],
          createdAt: at,
          appliedAt: at,
        };
        tweaks.unshift(tweak);
        return json({ tweak });
      }
      const revert = /^\/api\/design\/tweaks\/([^/]+)\/revert$/.exec(p);
      if (revert) {
        const tweak = tweaks.find((item) => item.id === revert[1])!;
        tweak.status = "reverted";
        return json({ tweak });
      }
      if (p === "/api/design/annotations" && method === "GET") return json({ annotations });
      if (p === "/api/design/annotations" && method === "POST") {
        const annotation: Annotation = { ...body, id: makeId("annotation", `a${annotations.length + 1}`), author: "user", status: "open", replies: [], createdAt: at };
        annotations.push(annotation);
        return json({ annotation }, 201);
      }
      const patch = /^\/api\/design\/annotations\/([^/]+)$/.exec(p);
      if (patch && method === "PATCH") {
        const annotation = annotations.find((item) => item.id === patch[1])!;
        if (body.reply) annotation.replies.push({ author: body.author ?? "user", body: body.reply, at });
        if (body.status) annotation.status = body.status;
        return json({ annotation });
      }
      if (p === `/api/sessions/${smithSession}/messages`) {
        const message = { id: makeId("message", "m1"), sessionId: smithSession, role: "user", content: body.content, createdAt: at };
        const reply = { id: makeId("message", "m2"), sessionId: smithSession, role: "assistant", agent: "smith", content: "Darkened the hero.", createdAt: at };
        return json({ message, reply });
      }
      if (p === "/api/design/inventory" && method === "GET") return json({ items: inventory });
      if (p.startsWith("/api/design/inventory/") && method === "PUT") {
        inventory.push(body);
        return json({ item: body });
      }
      if (p === "/api/design/inventory/candidates") {
        return json({
          candidates: [
            { key: "kit:Button", name: "Button", category: "input", description: "Uppercase button.", source: "kit", sourceRef: "Button", via: "kit" },
            {
              key: "21st:olewandowski1/booking-2",
              name: "Appointment Booking Split",
              category: "booking",
              description: "Two-column appointment request form.",
              source: "21st",
              sourceRef: "olewandowski1/booking-2",
              sourceUrl: "https://21st.dev/@olewandowski1/components/booking-2",
              author: "olewandowski1",
              via: "21st-snapshot",
              fetchedAt: at,
            },
          ],
          registry: { live: false, message: "The 21st CLI is not installed, so 21st entries come from the registry catalog snapshot of 2026-10-30." },
        });
      }
      if (p === "/api/builds") {
        return json({ error: { code: "not_implemented", message: "Builds, newest first: the orchestra layer is not connected yet" } }, 501);
      }
      if (p === "/api/health") return json({ error: { code: "not_found", message: "no" } }, 404);
      return json({ error: { code: "not_found", message: `No stub for ${method} ${p}` } }, 404);
    }),
  );
  return { calls, tweaks, annotations, inventory };
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("buildTweakOp", () => {
  it("makes a valid TweakOp for every kind from the form defaults plus one value", () => {
    const target = { selector: '[data-4ge-id="hero"]', page: "/" };
    const extra: Record<string, Record<string, string>> = {
      color: { colorValue: "#112233" },
      placement: { x: "8" },
      layout: { gap: "12px" },
      text: { text: "Hello" },
      asset: { assetRef: "public/a.jpg" },
      component: { componentAction: "remove" },
    };
    for (const kind of TWEAK_KINDS) {
      const defaults = {
        colorProperty: "color", animationName: "fade-up", durationMs: "400", easing: "ease-out", delayMs: "0", iterations: "1",
        preset: "fade-up", intensity: "0.5", reducedMotion: "yes", transitionProperty: "all", transitionMs: "200", transitionEasing: "ease",
        componentAction: "insert",
      };
      const result = tweakOpSchema.safeParse(buildTweakOp(kind, buildId, target, { ...defaults, ...(extra[kind] ?? {}) }));
      expect(result.success, kind).toBe(true);
    }
  });
});

describe("TweakPanel", () => {
  it("applies a text tweak to the named element and undoes it", async () => {
    const server = stubServer();
    const onChange = vi.fn();
    render(<TweakPanel buildId={buildId} target={{ selector: '[data-4ge-id="hero-title"]', page: "/", label: "hero-title" }} onChange={onChange} />);
    fireEvent.click(screen.getByRole("tab", { name: "Text" }));
    fireEvent.change(screen.getByLabelText("New text"), { target: { value: "Ink & Steel" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply tweak" }));
    expect(await screen.findByText("Applied to src/Hero.tsx")).toBeTruthy();
    const apply = server.calls.find((call) => call.method === "POST" && call.path === "/api/design/tweaks");
    expect(apply?.body).toMatchObject({ kind: "text", text: "Ink & Steel", target: { selector: '[data-4ge-id="hero-title"]' } });
    expect(onChange).toHaveBeenCalledTimes(1);
    fireEvent.click(await screen.findByRole("button", { name: "Undo" }));
    await waitFor(() => expect(server.calls.some((call) => call.path.endsWith("/revert"))).toBe(true));
    expect(await screen.findByText("Undone")).toBeTruthy();
  });

  it("says why a source tweak cannot apply to a class selector", () => {
    stubServer();
    render(<TweakPanel buildId={buildId} target={{ selector: ".hero h1", page: "/" }} />);
    fireEvent.click(screen.getByRole("tab", { name: "Text" }));
    expect(screen.getByText(/the target must be \[data-4ge-id="\.\.\."\] or #id/)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Apply tweak" }) as HTMLButtonElement).disabled).toBe(true);
  });
});

describe("AnnotationLayer", () => {
  it("pins a comment, sends it to Smith, and records Smith's reply in the thread", async () => {
    const server = stubServer();
    const { container } = render(
      <div style={{ position: "relative", width: "400px", height: "300px" }}>
        <AnnotationLayer buildId={buildId} pinning smithSessionId={smithSession} />
      </div>,
    );
    const layer = within(container).getByTestId("annotation-layer");
    fireEvent.click(layer, { clientX: 100, clientY: 60 });
    fireEvent.change(await screen.findByLabelText("What should change?"), { target: { value: "Make the hero darker" } });
    fireEvent.click(screen.getByRole("button", { name: "Pin comment" }));
    expect(await screen.findByRole("dialog", { name: "Comment thread" })).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Send to Smith" }));
    expect(await screen.findByText("Darkened the hero.")).toBeTruthy();
    const sent = server.calls.find((call) => call.path === `/api/sessions/${smithSession}/messages`);
    expect((sent?.body as { content: string }).content).toContain("Make the hero darker");
    const reply = server.calls.find((call) => call.method === "PATCH");
    expect(reply?.body).toMatchObject({ author: "smith", reply: "Darkened the hero." });
  });

  it("explains when Smith has no session for the build", async () => {
    const server = stubServer();
    server.annotations.push({
      id: makeId("annotation", "x"),
      buildId,
      target: { selector: "body", page: "/", x: 0.5, y: 0.5 },
      body: "Too bright",
      author: "user",
      status: "open",
      replies: [],
      createdAt: at,
    });
    render(<AnnotationLayer buildId={buildId} />);
    fireEvent.click(await screen.findByRole("button", { name: "Comment 1: Too bright" }));
    expect(screen.getByText(/Smith has no session for this build yet/)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Send to Smith" }) as HTMLButtonElement).disabled).toBe(true);
    expect(smithBrief(server.annotations[0]!)).toContain("The person wrote: Too bright");
  });
});

describe("InventoryPicker", () => {
  it("shows each candidate's source and records it when added", async () => {
    const server = stubServer();
    render(<InventoryPicker buildId={buildId} />);
    expect(await screen.findByText("Appointment Booking Split")).toBeTruthy();
    expect(screen.getByText(/21st CLI is not installed/)).toBeTruthy();
    expect(screen.getByText("21st.dev/@olewandowski1/components/booking-2")).toBeTruthy();
    const card = screen.getByText("Appointment Booking Split").closest("li")!;
    fireEvent.click(within(card as HTMLElement).getByRole("button", { name: "Add" }));
    await waitFor(() => expect(server.inventory).toHaveLength(1));
    expect(server.inventory[0]).toMatchObject({ source: "21st", sourceRef: "olewandowski1/booking-2", status: "proposed", category: "booking" });
    expect(server.inventory[0]?.description).toContain("https://21st.dev/@olewandowski1/components/booking-2");
  });
});

describe("Tweak page", () => {
  it("opens a build by id when the builds list is not connected", async () => {
    stubServer();
    const router = createMemoryRouter(routes, { initialEntries: ["/tweak"] });
    render(<RouterProvider router={router} />);
    expect(await screen.findByRole("heading", { level: 1, name: "Tweak" })).toBeTruthy();
    expect(await screen.findByText(/the orchestra layer is not connected yet/)).toBeTruthy();
    fireEvent.change(screen.getByLabelText("Build id"), { target: { value: buildId } });
    fireEvent.click(screen.getByRole("button", { name: "Open" }));
    await waitFor(() => expect(router.state.location.pathname).toBe(`/tweak/${buildId}`));
  });
});

describe("targetForElement", () => {
  it("prefers the closest data-4ge-id, then id, then a short path", () => {
    document.body.innerHTML = '<section data-4ge-id="hero"><h1 class="t big">x</h1></section><div id="cta"><span>y</span></div><p class="lead">z</p>';
    expect(targetForElement(document.querySelector("h1")!)).toMatchObject({ selector: '[data-4ge-id="hero"]', label: "hero" });
    expect(targetForElement(document.querySelector("span")!)).toMatchObject({ selector: "#cta" });
    expect(targetForElement(document.querySelector("p")!).selector).toBe("p.lead");
  });
});
