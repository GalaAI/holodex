import { expect, type Page } from "@playwright/test";
import { shotPath } from "./forge.js";

/**
 * Visual checks for a studio screen: a screenshot, then what the eye would
 * catch, measured from the DOM:
 *   - no sideways page scroll;
 *   - no text cut off by its box (overflow hidden with content wider or
 *     taller than the box), unless it ends in an ellipsis and says the whole
 *     text in a title;
 *   - no two controls or headings drawn over each other;
 *   - Phosphor tokens: the page ground, text and every visible color come
 *     from the @4ge/kit --color-* tokens (alpha variants allowed);
 *   - no em or en dash anywhere in the text.
 * Artboards inside iframes (the user's own build) are out of scope.
 */

/** En dash and em dash, built from code points so this file contains neither. */
export const DASHES = new RegExp(`[${String.fromCodePoint(0x2013)}${String.fromCodePoint(0x2014)}]`);

export interface VisualReport {
  sidewaysScroll: number;
  clipped: string[];
  overlaps: string[];
  /** Text another element is painted over (what the eye sees as one thing drawn on another). */
  covered: string[];
  offPalette: string[];
  ground: { body: string; token: string };
  dashes: boolean;
}

export async function measure(page: Page): Promise<VisualReport> {
  return page.evaluate(() => {
    const describe = (element: Element): string => {
      const html = element as HTMLElement;
      const id = html.dataset?.testid ? `[data-testid=${html.dataset.testid}]` : "";
      const cls = typeof html.className === "string" && html.className ? `.${html.className.trim().split(/\s+/).slice(0, 2).join(".")}` : "";
      const text = (html.innerText ?? html.textContent ?? "").trim().replace(/\s+/g, " ").slice(0, 50);
      return `${element.tagName.toLowerCase()}${id}${cls} "${text}"`;
    };
    const visible = (element: Element): boolean => {
      const rect = element.getBoundingClientRect();
      if (rect.width < 2 || rect.height < 2) return false; // screen-reader-only text is a 1px box by design
      // checkVisibility also hides what a closed <details> keeps laid out but never paints.
      if (!element.checkVisibility({ contentVisibilityAuto: true, opacityProperty: true, visibilityProperty: true })) return false;
      const style = getComputedStyle(element);
      if (style.visibility === "hidden" || style.display === "none" || Number(style.opacity) === 0) return false;
      return rect.bottom > 0 && rect.right > 0 && rect.top < innerHeight && rect.left < innerWidth;
    };
    // Inside the pannable canvas, artboards are drawn at any zoom and can sit under the toolbar by design.
    const onCanvas = (element: Element) => Boolean(element.closest('[data-testid="build-canvas"], .board__world, iframe'));

    // 1. Clipped text: overflow hidden or clip, content bigger than the box.
    const clipped: string[] = [];
    for (const element of Array.from(document.querySelectorAll("body *"))) {
      if (!visible(element) || onCanvas(element)) continue;
      const html = element as HTMLElement;
      if (!html.innerText || html.innerText.trim().length === 0) continue;
      if (["svg", "path", "img", "iframe", "textarea", "input", "select", "option", "canvas", "video"].includes(element.tagName.toLowerCase())) continue;
      const style = getComputedStyle(html);
      const hidesX = ["hidden", "clip"].includes(style.overflowX);
      const hidesY = ["hidden", "clip"].includes(style.overflowY);
      const wide = hidesX && html.scrollWidth > html.clientWidth + 2;
      const tall = hidesY && html.scrollHeight > html.clientHeight + 2 && style.webkitLineClamp === "none";
      if (!wide && !tall) continue;
      // Only boxes that hold text directly, not containers that scroll their children away.
      const ownText = Array.from(html.childNodes).some((node) => node.nodeType === Node.TEXT_NODE && (node.textContent ?? "").trim().length > 0);
      if (!ownText) continue;
      const ellipsis = style.textOverflow === "ellipsis" || style.webkitLineClamp !== "none";
      const explained = Boolean(html.title || html.closest("[title]") || html.getAttribute("aria-label"));
      if (ellipsis && explained) continue;
      clipped.push(`${describe(element)} ${wide ? `${html.scrollWidth}>${html.clientWidth}w` : `${html.scrollHeight}>${html.clientHeight}h`}${ellipsis ? " (ellipsis, no title)" : ""}`);
    }

    // 2. Overlaps between controls and headings that are not nested in each other, measured on what
    //    is actually painted: each box is cut to the scroll or clip boxes around it.
    const shown = (element: Element): DOMRect => {
      let rect = element.getBoundingClientRect();
      for (let parent = element.parentElement; parent && parent !== document.body; parent = parent.parentElement) {
        const style = getComputedStyle(parent);
        if (style.overflowX === "visible" && style.overflowY === "visible") continue;
        const clip = parent.getBoundingClientRect();
        const left = Math.max(rect.left, clip.left);
        const top = Math.max(rect.top, clip.top);
        const right = Math.min(rect.right, clip.right);
        const bottom = Math.min(rect.bottom, clip.bottom);
        rect = new DOMRect(left, top, Math.max(0, right - left), Math.max(0, bottom - top));
      }
      return rect;
    };
    const things = Array.from(
      document.querySelectorAll("button, a[href], input:not([type=hidden]), textarea, select, h1, h2, h3, [role=tab], label"),
    ).filter((element) => visible(element) && !onCanvas(element));
    const overlaps: string[] = [];
    for (let i = 0; i < things.length; i += 1) {
      for (let j = i + 1; j < things.length; j += 1) {
        const a = things[i] as Element;
        const b = things[j] as Element;
        if (a.contains(b) || b.contains(a)) continue;
        // A label and its own control, or a control inside a label, belong together.
        if (a.tagName === "LABEL" || b.tagName === "LABEL") {
          const label = (a.tagName === "LABEL" ? a : b) as HTMLLabelElement;
          const other = label === a ? b : a;
          if (label.control === other || label.htmlFor === (other as HTMLElement).id || label.parentElement?.contains(other)) continue;
        }
        const ra = shown(a);
        const rb = shown(b);
        const x = Math.min(ra.right, rb.right) - Math.max(ra.left, rb.left);
        const y = Math.min(ra.bottom, rb.bottom) - Math.max(ra.top, rb.top);
        if (!(x > 2 && y > 2 && x * y > 16)) continue;
        // A small control set inside a larger one's box (a corner link on a tile) is fine unless it covers that one's text.
        const inside = (inner: DOMRect, outer: DOMRect) => inner.left >= outer.left - 1 && inner.right <= outer.right + 1 && inner.top >= outer.top - 1 && inner.bottom <= outer.bottom + 1;
        const [small, big, smallRect] = inside(rb, ra) ? [b, a, rb] : inside(ra, rb) ? [a, b, ra] : [undefined, undefined, undefined];
        if (small && big && smallRect) {
          const walker = document.createTreeWalker(big, NodeFilter.SHOW_TEXT);
          let covers = false;
          for (let node = walker.nextNode(); node && !covers; node = walker.nextNode()) {
            if (!(node.textContent ?? "").trim() || small.contains(node)) continue;
            const range = document.createRange();
            range.selectNodeContents(node);
            covers = Array.from(range.getClientRects()).some(
              (box) => Math.min(box.right, smallRect.right) - Math.max(box.left, smallRect.left) > 1 && Math.min(box.bottom, smallRect.bottom) - Math.max(box.top, smallRect.top) > 1,
            );
          }
          if (!covers) continue;
        }
        overlaps.push(`${describe(a)} over ${describe(b)} (${Math.round(x)}x${Math.round(y)})`);
      }
    }

    // 2b. Text with something else painted on top of it: hit-test the middle of each line of text.
    const covered: string[] = [];
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const owner = node.parentElement;
      if (!owner || !(node.textContent ?? "").trim() || onCanvas(owner) || !visible(owner)) continue;
      const range = document.createRange();
      range.selectNodeContents(node);
      const lines = Array.from(range.getClientRects()).filter((box) => box.width > 4 && box.height > 4);
      for (const box of lines.slice(0, 3)) {
        const x = box.left + box.width / 2;
        const y = box.top + box.height / 2;
        if (x < 0 || y < 0 || x >= innerWidth || y >= innerHeight) continue;
        // Only text its own scroll box shows: text scrolled out of a panel is not "covered".
        const clip = shown(owner);
        if (x < clip.left || x > clip.right || y < clip.top || y > clip.bottom) continue;
        const hit = document.elementFromPoint(x, y);
        if (!hit || owner.contains(hit) || hit.contains(owner)) continue;
        covered.push(`${describe(owner)} under ${describe(hit)}`);
        break;
      }
      if (covered.length > 20) break;
    }

    // 3. Phosphor tokens.
    const root = getComputedStyle(document.documentElement);
    const probe = document.createElement("span");
    document.body.appendChild(probe);
    const resolve = (value: string) => {
      probe.style.color = "";
      probe.style.color = value;
      return getComputedStyle(probe).color;
    };
    const rgbOf = (color: string): string | undefined => {
      const match = /rgba?\(\s*(\d+)[,\s]+(\d+)[,\s]+(\d+)(?:[,\s/]+([\d.]+))?/.exec(color);
      if (!match) return undefined;
      if (match[4] !== undefined && Number(match[4]) === 0) return undefined; // transparent
      return `${match[1]},${match[2]},${match[3]}`;
    };
    const palette = new Set<string>();
    const tokenNames: string[] = [];
    for (const sheet of Array.from(document.styleSheets)) {
      let rules: CSSRuleList;
      try {
        rules = sheet.cssRules;
      } catch {
        continue;
      }
      for (const rule of Array.from(rules)) {
        const style = (rule as CSSStyleRule).style;
        if (!style) continue;
        for (let index = 0; index < style.length; index += 1) {
          const name = style.item(index);
          if (name.startsWith("--") && !tokenNames.includes(name)) tokenNames.push(name);
        }
      }
    }
    for (const name of tokenNames) {
      const value = root.getPropertyValue(name).trim();
      if (!value || /^(var|calc|\d|url)/.test(value)) continue;
      const rgb = rgbOf(resolve(value));
      if (rgb) palette.add(rgb);
    }
    palette.add("0,0,0");
    palette.add("255,255,255");
    const offPalette = new Map<string, string>();
    for (const element of Array.from(document.querySelectorAll("body *"))) {
      if (!visible(element) || onCanvas(element)) continue;
      const style = getComputedStyle(element);
      for (const [what, value] of [
        ["color", (element as HTMLElement).innerText?.trim() ? style.color : ""],
        ["background", style.backgroundColor],
        ["border", style.borderTopWidth !== "0px" && style.borderTopStyle !== "none" ? style.borderTopColor : ""],
      ] as const) {
        const rgb = value ? rgbOf(value) : undefined;
        if (!rgb || palette.has(rgb)) continue;
        // A color set inline is data (a build's own swatch or token), not studio chrome.
        if ((element as HTMLElement).style?.getPropertyValue(what === "background" ? "background-color" : what === "border" ? "border-color" : "color") ||
          (what === "background" && (element as HTMLElement).style?.background)) continue;
        // color-mix and alpha tints of a token land between two palette colors; accept anything within a small distance of a mix.
        const [r, g, b] = rgb.split(",").map(Number) as [number, number, number];
        const near = [...palette].some((entry) => {
          const [pr, pg, pb] = entry.split(",").map(Number) as [number, number, number];
          return Math.abs(pr - r) + Math.abs(pg - g) + Math.abs(pb - b) <= 24;
        });
        if (near) continue;
        const key = `${what} rgb(${rgb})`;
        if (!offPalette.has(key)) offPalette.set(key, describe(element));
      }
    }
    const tokenGround = rgbOf(resolve(root.getPropertyValue("--color-ground").trim() || "transparent")) ?? "";
    probe.remove();
    const body = rgbOf(getComputedStyle(document.body).backgroundColor) ?? rgbOf(getComputedStyle(document.documentElement).backgroundColor) ?? "";
    return {
      sidewaysScroll: Math.max(0, document.documentElement.scrollWidth - innerWidth),
      clipped,
      overlaps,
      covered,
      offPalette: [...offPalette].map(([key, where]) => `${key} on ${where}`),
      ground: { body, token: tokenGround },
      dashes: false,
    };
  }).then(async (report) => ({ ...report, dashes: DASHES.test(await page.locator("body").innerText()) }));
}

/** Screenshot the screen and measure it, without failing the test (for steps of a longer flow). */
export async function shootScreen(page: Page, name: string): Promise<VisualReport & { name: string }> {
  // The sandbox viewer streams over a websocket forever, so "network idle" and font loading are waited for a few seconds at most.
  await page.waitForLoadState("networkidle", { timeout: 5000 }).catch(() => undefined);
  await page.evaluate(() => Promise.race([document.fonts.ready.then(() => true), new Promise((done) => setTimeout(() => done(false), 3000))]));
  await page.screenshot({ path: shotPath(name), fullPage: false });
  return { name, ...(await measure(page)) };
}

/** Assert a measured screen is clean (soft, so every problem on every screen is listed). */
export function expectClean(report: VisualReport & { name: string }): void {
  const { name } = report;
  expect.soft(report.sidewaysScroll, `${name}: the page scrolls sideways`).toBe(0);
  expect.soft(report.clipped, `${name}: clipped text`).toEqual([]);
  expect.soft(report.overlaps, `${name}: overlapping controls`).toEqual([]);
  expect.soft(report.covered, `${name}: text with something painted over it`).toEqual([]);
  expect.soft(report.offPalette, `${name}: colors outside the Phosphor tokens`).toEqual([]);
  expect.soft(report.ground.body, `${name}: the page ground is the --color-ground token`).toBe(report.ground.token);
  expect.soft(report.dashes, `${name}: an em or en dash is shown`).toBe(false);
}

/** Screenshot the screen and assert it is clean. */
export async function checkScreen(page: Page, name: string): Promise<VisualReport> {
  const report = await shootScreen(page, name);
  expectClean(report);
  return report;
}
