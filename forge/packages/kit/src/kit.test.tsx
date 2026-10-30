// @vitest-environment happy-dom
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import {
  ACTION_RISKS,
  ActionPill,
  Button,
  Eyebrow,
  FocalCard,
  Led,
  Panel,
  StageCanvas,
  fitStageView,
  token,
  zoomStageView,
} from "./index.js";

afterEach(cleanup);
const here = dirname(fileURLToPath(import.meta.url));

describe("@4ge/kit components keep their Phosphor classes", () => {
  it("Button maps variants to rp-btn classes and defaults to type=button", () => {
    render(<Button variant="primary">Build now</Button>);
    const button = screen.getByRole("button", { name: "Build now" });
    expect(button.className).toBe("rp-btn rp-btn--primary");
    expect(button.getAttribute("type")).toBe("button");
  });

  it("ActionPill carries risk, pressed state, the gate marker, and blocks planned clicks", () => {
    let clicks = 0;
    render(
      <ActionPill risk="high" gated pressed planned onClick={() => (clicks += 1)}>
        Deploy
      </ActionPill>,
    );
    const pill = screen.getByRole("button", { name: /Deploy/ });
    expect(pill.dataset.risk).toBe("high");
    expect(pill.getAttribute("aria-pressed")).toBe("true");
    expect(pill.textContent).toContain("gate");
    fireEvent.click(pill);
    expect(clicks).toBe(0);
    expect(ACTION_RISKS).toEqual(["low", "medium", "high", "critical"]);
  });

  it("Eyebrow, Led, Panel and FocalCard render their surfaces", () => {
    render(
      <Panel aria-label="region">
        <Eyebrow>4GE // TEST</Eyebrow>
        <Led state="live">Server live</Led>
        <FocalCard eyebrow="Gate pending // Smith" title="Approve the sprint" plain="The architecture sprint is waiting for you." />
      </Panel>,
    );
    expect(screen.getByText("4GE // TEST").className).toBe("rp-eyebrow");
    expect(screen.getByText("Server live").className).toBe("rp-led rp-led--live");
    expect(screen.getByRole("status")).toBeTruthy();
    expect(screen.getByText("Approve the sprint").closest(".rp-focal--gate")).toBeTruthy();
  });

  it("StageCanvas labels the board and fits and zooms about a point", () => {
    render(
      <StageCanvas contentWidth={1280} contentHeight={800} label="Landing page" readout="Live">
        <div>artboard</div>
      </StageCanvas>,
    );
    expect(screen.getByRole("region", { name: "Landing page stage" }).getAttribute("aria-roledescription")).toBe("pannable canvas");
    const fit = fitStageView({ width: 1376, height: 896 }, { width: 1280, height: 800 });
    expect(fit).toEqual({ zoom: 1, x: 48, y: 48 });
    const zoomed = zoomStageView({ x: 0, y: 0, zoom: 1 }, { x: 100, y: 100 }, 2);
    expect(zoomed).toEqual({ zoom: 2, x: -100, y: -100 });
  });
});

describe("@4ge/kit tokens", () => {
  it("mirror the CSS custom properties", () => {
    const colors = readFileSync(join(here, "tokens", "colors.css"), "utf8");
    expect(colors.toLowerCase()).toContain(token.color.ground.toLowerCase());
    expect(colors.toLowerCase()).toContain(token.color.patina.toLowerCase());
    expect(colors.toLowerCase()).toContain(token.color.bone.toLowerCase());
  });

  it("ship every stylesheet kit.css imports", () => {
    const kitCss = readFileSync(join(here, "kit.css"), "utf8");
    const imports = [...kitCss.matchAll(/@import "\.\/(.+?)";/g)].map((match) => match[1] ?? "");
    expect(imports.length).toBeGreaterThan(15);
    for (const file of imports) expect(() => readFileSync(join(here, file), "utf8"), file).not.toThrow();
  });
});
