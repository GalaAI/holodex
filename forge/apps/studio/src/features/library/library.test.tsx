// @vitest-environment happy-dom
import { groupLibrary, type LibraryItem } from "@4ge/contracts";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { RouterProvider, createMemoryRouter } from "react-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import Library from "./index.js";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const item = (id: string, origin: LibraryItem["origin"], title: string, subjectType: LibraryItem["open"]["subject"]["type"]): LibraryItem => ({
  id,
  origin,
  title,
  tone: "ok",
  status: "Ready",
  open: { agent: "smith", kind: origin === "mod" ? "mod" : "library", task: `Help me with ${title}`, subject: { type: subjectType, id, label: title } },
});

const library = groupLibrary([
  item("project:ryozan", "project", "Ryozan", "project"),
  item("mod:launch", "mod", "Launch Mod", "mod"),
  item("augment:vercel-mcp", "augment", "Vercel MCP", "augment"),
  item("adapter:vercel", "adapter", "Vercel", "adapter"),
]);

describe("Library", () => {
  it("shows the five origin groups in order, with the plus tile in Mods, and opens Smith on click", async () => {
    const calls: Array<{ url: string; method: string; body?: string }> = [];
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: string | URL | Request, init?: RequestInit) => {
        const url = String(input);
        calls.push({ url, method: init?.method ?? "GET", ...(typeof init?.body === "string" ? { body: init.body } : {}) });
        if (url.endsWith("/api/library")) return new Response(JSON.stringify(library), { status: 200 });
        if (url.endsWith("/api/sessions") && init?.method === "POST") {
          return new Response(JSON.stringify({ error: { code: "not_implemented", message: "The orchestra layer is not connected yet" } }), {
            status: 501,
          });
        }
        return new Response("{}", { status: 404 });
      }),
    );
    const router = createMemoryRouter([{ path: "/library", element: <Library /> }, { path: "/mods", element: <p>mods</p> }], {
      initialEntries: ["/library"],
    });
    render(<RouterProvider router={router} />);
    const headings = await screen.findAllByRole("heading", { level: 2 });
    expect(headings.map((heading) => heading.textContent?.replace(/\d+$/, ""))).toEqual(["My projects", "Repos", "Mods", "Augments", "Adapters"]);
    expect(screen.getByRole("button", { name: "Make a mod" })).toBeTruthy();

    fireEvent.click(screen.getByTitle("Open Launch Mod with Smith"));
    expect(await screen.findByText("This part of 4GE is not connected yet")).toBeTruthy();
    const post = calls.find((call) => call.method === "POST");
    expect(JSON.parse(post?.body ?? "{}")).toMatchObject({ agent: "smith", kind: "mod", subject: { type: "mod", id: "mod:launch" } });

    fireEvent.click(screen.getByRole("button", { name: "Make a mod" }));
    expect(router.state.location.search).toBe("?create=1");
  });
});
