// @vitest-environment happy-dom
import { PORTS, type Health } from "@4ge/contracts";
import { cleanup, render, screen } from "@testing-library/react";
import { RouterProvider, createMemoryRouter } from "react-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import { FEATURES } from "../features.js";
import { routes } from "../router.js";

const health: Health = {
  ok: true,
  name: "4ge",
  version: "0.1.0",
  startedAt: "2026-10-30T12:00:00.000Z",
  ports: { ...PORTS },
  layers: { core: true, atlas: false, orchestra: true, design: false, mods: false, sandbox: false, library: false },
  studio: { served: true },
};

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function stubServer(response: () => Promise<Response>) {
  vi.stubGlobal("fetch", vi.fn(response));
}

function open(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  render(<RouterProvider router={router} />);
  return router;
}

describe("studio shell", () => {
  it("lands on Imagistation, lists every feature, and shows the server live", async () => {
    stubServer(async () => new Response(JSON.stringify(health), { status: 200 }));
    const router = open("/");
    expect(await screen.findByRole("heading", { level: 1, name: "Imagistation" })).toBeTruthy();
    expect(router.state.location.pathname).toBe("/imagistation");
    for (const feature of FEATURES) expect(screen.getByTestId(`rail-${feature.id}`).textContent).toBe(feature.label);
    expect((await screen.findByTestId("server-status")).textContent).toBe("Server live");
    // The stub answers every route with health, so the model route is unknown, and the rail says so.
    expect((await screen.findByTestId("model-status")).textContent).toBe("Model unknown");
  });

  it("says the server is offline when it does not answer", async () => {
    stubServer(async () => {
      throw new TypeError("fetch failed");
    });
    open("/library");
    expect(await screen.findByRole("heading", { level: 1, name: "Library" })).toBeTruthy();
    expect((await screen.findByText("Server offline")).getAttribute("title")).toContain("Start the 4GE server");
  });

  it("opens a feature with its optional param, folds the rail in the IDE and explains unknown addresses", async () => {
    stubServer(async () => new Response(JSON.stringify(health), { status: 200 }));
    open("/ide/bld_123");
    // The stub's answer is not a build, so the IDE says it could not open it, in plain words.
    expect(await screen.findByRole("heading", { level: 1, name: "This build could not open" })).toBeTruthy();
    expect(document.querySelector(".studio--compact")).toBeTruthy();
    expect(screen.getByTestId("rail-ide").textContent).toBe("Build");
    cleanup();
    open("/nowhere");
    expect(await screen.findByText("There is no 4GE page at this address.")).toBeTruthy();
  });
});
