const INV = __INV__;
// Projects the lab lists, each on the row of its Atlas blueprint deliverable (blueprintDeliverableSchema in
// packages/lexicon/src/veritas-canon.ts). Only Holodex publishes renders today.
const BLUEPRINT_ROWS = [["desktop-app", "Desktop App"], ["web-app", "Web App"], ["api-service", "API Service"], ["custom", "Custom"]];
const PROJECTS = [
  { id: "holodex", name: "Holodex", row: "desktop-app", type: "Desktop App", repo: "GalaAI/holodex", shell: "phosphor", renders: true, known: "the Hivemind mission repos (GalaAI/holodex)" },
  { id: "bttco", name: "BTTCO", row: "web-app", type: "Web App", repo: "GalaAI/BTTCO", shell: "phosphor", shellNote: "Vagabond, kept in BTTCO's own repo", known: "the Hivemind mission repos and the Railway project BTTCO" },
  { id: "ryozan", name: "Ryozan", row: "web-app", type: "Web App", repo: "GalaAI/ryozan", shell: "bandit", known: "the Railway projects ryozan and ryozan-test-studio" },
  { id: "console", name: "ArkStation Console", row: "web-app", type: "Web App", repo: "GalaAI/ark", shell: "phosphor", known: "apps/web-console in GalaAI/ark and the Railway project ark" },
  { id: "portal", name: "Hivemind Portal", row: "web-app", type: "Web App", repo: "GalaAI/ark", shell: "phosphor", known: "apps/hivemind-portal in GalaAI/ark and the Railway project hivemind-public" },
  { id: "hivemind", name: "Hivemind", row: "api-service", type: "API Service", repo: "GalaAI/hivemind", shell: "phosphor", known: "the Hivemind mission repos (GalaAI/hivemind) and the Railway project Hivemind" },
  { id: "galax", name: "GalaX", row: "custom", type: "Custom", repo: null, shell: "phosphor", known: "the Railway project GalaX; its blueprint type is not recorded" },
];
const proj = () => PROJECTS.find(p => p.id === S.project) || PROJECTS[0];
const hasRenders = () => !!proj().renders;
const repoName = () => proj().repo || "the project's own repository";
const reduced = () => matchMedia("(prefers-reduced-motion: reduce)").matches;
const MAPS = new Set(__MAPS__);
const RUN = "43071856814";
const HX = "host:holodex";
const AGENT = { claude: "Smith", metis: "Metis", iris: "Iris" };
const agentLabel = id => AGENT[id] || id || "Agent";

const TOK = Object.fromEntries(TOKENS.map(t => [t.name, t]));
for (const n of TUNABLE) if (!TOK[n]) TOK[n] = { name: n, value: "", file: "PhosphorTheme (Holodex-local)", note: "Holodex-local metric" };
const tunable = new Set(TUNABLE);
const HEXRE = /^#[0-9a-f]{6}$/i, NUMBER = /^(-?\d*\.?\d+)(px|ms|em|rem|s)?$/;
const isColor = v => HEXRE.test(v || "");
const kindOf = n => n.startsWith("color-") || n.startsWith("ark-emblem") ? "color" : /^(dur-|disclosure-)/.test(n) || /tip-dwell|stagger|reduced-motion/.test(n) ? "motion" : n.startsWith("text-") || n.startsWith("eyebrow") ? "type" : "metric";
const TOKEN_KINDS = [["color", "Colour"], ["motion", "Motion"], ["metric", "Metrics"], ["type", "Type"]];

// Browser storage keeps conveniences only: pending tweaks, the focused surface, and which Hivemind session this thread is.
const store = { get(k, d) { try { const v = localStorage.getItem("uilab:" + k); return v == null ? d : JSON.parse(v); } catch { return d; } }, set(k, v) { try { localStorage.setItem("uilab:" + k, JSON.stringify(v)); } catch {} } };
const S = { view: "inventory", project: null, side: "thread", layer: null, focus: store.get("focus", "holodex"), tweaks: store.get("tweaks", {}), elTweaks: store.get("elTweaks", {}), pick: null, q: "", open: { surfaces: true }, missions: store.get("missions", []), mission: null, sessionId: store.get("sessionId", null), thread: store.get("thread", null), source: "published" };
if (!SIZES[S.focus]) S.focus = ALL[0];
const save = () => { store.set("tweaks", S.tweaks); store.set("elTweaks", S.elTweaks); store.set("focus", S.focus); store.set("missions", S.missions); store.set("sessionId", S.sessionId); store.set("thread", S.thread); };
const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const $ = id => document.getElementById(id);
const uuid = () => crypto.randomUUID ? crypto.randomUUID() : "10000000-1000-4000-8000-100000000000".replace(/[018]/g, c => (c ^ crypto.getRandomValues(new Uint8Array(1))[0] & 15 >> c / 4).toString(16));
const tokVal = n => TOK[n]?.value ?? "";
const narrow = () => matchMedia("(max-width: 55rem)").matches;

/* ---------- Shell parts as static markup (same structure and class names as @ark/ui) ---------- */
const badge = (text, state) => `<span class="rp-badge${state ? " rp-badge--" + state : ""}">${esc(text)}</span>`;
const life = (state, text) => `<span class="rp-life rp-life--${state}"><i aria-hidden="true"></i>${esc(text || state)}</span>`;
const led = (state, text) => `<span class="rp-led rp-led--${state}"><i aria-hidden="true"></i>${esc(text)}</span>`;
const btn = (label, attrs = "", variant = "") => `<button type="button" class="rp-btn${variant ? " rp-btn--" + variant : ""}" ${attrs}>${esc(label)}</button>`;
const switcher = (label, items, value, attr) => `<div class="rp-context-switcher" role="group" aria-label="${esc(label)}">${items.map(([id, text, off]) => `<button type="button" class="rp-context-switcher__item" ${id === value ? 'data-active="true"' : ""} aria-pressed="${id === value}" ${off ? "disabled" : ""} data-${attr}="${id}"><span>${esc(text)}</span></button>`).join("")}</div>`;
const row = ({ title, desc, leading, trailing, selected, attrs }) => `<button type="button" class="rp-inspectable-row rp-aura--behind" ${selected ? 'data-selected="true" data-aura="lit" aria-current="true"' : ""} ${attrs || ""}><span class="rp-inspectable-row__leading" ${leading ? "" : 'aria-hidden="true"'}>${leading || ""}</span><span class="rp-inspectable-row__body"><strong>${esc(title)}</strong>${desc ? `<small>${esc(desc)}</small>` : ""}</span>${trailing ? `<span class="rp-inspectable-row__trailing">${trailing}</span>` : ""}</button>`;
const accordion = (id, title, summary, content) => { const open = !!S.open[id]; return `<section class="rp-accordion" data-open="${open}" data-compact="true"><h3 class="rp-accordion__heading"><button type="button" id="acc-${id}-trigger" class="rp-accordion__trigger rp-aura--behind" aria-expanded="${open}" aria-controls="acc-${id}-body" data-acc="${id}"><span class="rp-accordion__indicator" aria-hidden="true">${open ? "−" : "+"}</span><span class="rp-accordion__identity"><span class="rp-accordion__title">${esc(title)}</span></span>${summary != null ? `<span class="rp-accordion__summary">${esc(summary)}</span>` : ""}</button></h3><section id="acc-${id}-body" class="rp-accordion__body" aria-labelledby="acc-${id}-trigger" aria-hidden="${!open}" ${open ? "" : "inert"}><div class="rp-accordion__content">${content}</div></section></section>`; };
const textField = (label, value, attrs, hint) => `<label class="rp-field"><span class="rp-field__label">${esc(label)}</span><span class="rp-field__box"><input class="rp-field__input rp-field__input--mono" type="text" value="${esc(value)}" ${attrs}></span>${hint ? `<span class="rp-field__hint">${esc(hint)}</span>` : ""}</label>`;
const link = url => `<a class="rp-link" href="${esc(url)}" target="_blank" rel="noopener">${esc(url.replace("https://github.com/", ""))}</a>`;
const note = t => `<p class="lab-note">${esc(t)}</p>`;

/* ---------- layers ---------- */
function layerTokens(l) { return l ? l.kind === "token" ? [l.id] : (l.kind === "component" ? COMPONENTS : EFFECTS).find(x => x.id === l.id)?.tokens || [] : []; }
function layerSurfaces(l) { if (!l || l.kind === "token") return null; const x = (l.kind === "component" ? COMPONENTS : EFFECTS).find(y => y.id === l.id); return x ? new Set(ALL.filter(x.on)) : null; }
const layerName = l => l.kind === "token" ? l.id : (l.kind === "component" ? COMPONENTS : EFFECTS).find(x => x.id === l.id)?.name || l.id;
function renderLayers() {
  if (!hasRenders()) { $("side-layers").innerHTML = note(`${proj().name} has no renders yet, so there are no surfaces, components or tokens to list.`); return; }
  const words = S.q.toLowerCase().split(/\s+/).filter(Boolean);
  const shown = t => words.every(w => t.toLowerCase().includes(w));
  const sel = (kind, id) => S.layer?.kind === kind && S.layer.id === id;
  const layerRow = (kind, id, title, count, leading) => row({ title, leading, trailing: kind === "token" && id in S.tweaks ? badge("changed", "queued") : count != null ? `<span class="lab-count">${count}</span>` : "", selected: sel(kind, id), attrs: `data-layer="${kind}:${esc(id)}"` });
  const surf = ALL.filter(shown), comps = COMPONENTS.filter(c => shown(c.name + " " + c.cls)), effs = EFFECTS.filter(e => shown(e.name));
  let html = accordion("surfaces", "Surfaces", surf.length, surf.map(k => row({ title: k, desc: `${SIZES[k][0]} x ${SIZES[k][1]}`, trailing: MAPS.has(k) ? "" : `<span class="lab-count">no map</span>`, selected: S.view === "focus" && S.focus === k, attrs: `data-focus="${k}"` })).join("") || note("No surface matches."));
  html += accordion("components", "Components", comps.length, comps.map(c => layerRow("component", c.id, c.name, ALL.filter(c.on).length)).join("") || note("No component matches."));
  html += accordion("effects", "Effects", effs.length, effs.map(e => layerRow("effect", e.id, e.name, ALL.filter(e.on).length)).join("") || note("No effect matches."));
  for (const [kind, label] of TOKEN_KINDS) {
    const items = Object.keys(TOK).filter(n => kindOf(n) === kind && shown(n));
    if (items.length) html += accordion("tokens-" + kind, "Tokens: " + label, items.length, items.map(n => layerRow("token", n, n, tunable.has(n) ? "live" : null, isColor(tokVal(n)) ? `<span class="lab-swatch" style="background:var(--${esc(n)})"></span>` : "")).join(""));
  }
  const keep = document.activeElement?.id === "q";
  $("side-layers").innerHTML = `<div class="rp-search"><div class="rp-search__entry"><label class="rp-field"><span class="rp-field__label">Find a surface, component, effect or token</span><span class="rp-field__box"><input class="rp-field__input" type="search" id="q" value="${esc(S.q)}" autocomplete="off" spellcheck="false"></span></label></div></div>` + html;
  if (keep) { const q = $("q"); q.focus(); q.setSelectionRange(q.value.length, q.value.length); }
}

/* ---------- live app (host:holodex) ---------- */
// The local MCP server "holodex" answers only inside the Claude desktop app, for the artifact's owner, while Holodex runs with its developer pipe.
const live = { state: "checking", msg: "", applied: {}, pending: {}, timer: null, busy: false, elProps: {}, note: "" };
function holodexErr(e) {
  const c = e?.code;
  if (c === "server_not_connected") return "The holodex server is not connected in this Claude app.";
  if (c === "not_in_manifest") return "Holodex is turned off for this page. Allow it from the artifact's Permissions menu.";
  if (c === "approval_required") return "The Claude app needs your approval for the holodex server.";
  if (c === "cancelled") return "You declined that change in the Claude app, so nothing was applied.";
  if (c === "blocked_by_policy") return "Your organization's policy blocks the holodex server.";
  if (c === "server_unavailable") return "Holodex did not answer in time.";
  if (c === "tool_error") return String(e.message || "Holodex refused the call.").slice(0, 240);
  return `The holodex call failed (${c || "error"}).`;
}
function renderSource() {
  if (!hasRenders()) { $("source").innerHTML = led("idle", "No render source") + note(`${proj().name} is known from ${proj().known}.`); return; }
  const on = live.state === "on";
  const text = on ? "Live: Holodex on this PC" : live.state === "checking" ? "Checking for Holodex" : live.state === "connecting" ? "Connecting to Holodex" : "Published renders";
  const detail = on ? live.note : live.state === "error" ? live.msg : live.state === "off" ? `The live app answers only in the Claude desktop app with the holodex server connected. These are the renders of UI renders run ${RUN}.` : "";
  $("source").innerHTML = led(on ? "live" : live.state === "error" ? "warn" : "idle", text) + (detail ? note(detail) : "") +
    (on ? switcher("Render source", [["live", "Live"], ["published", "Published"]], S.source, "src") + (S.view === "focus" && S.source === "live" ? btn("Render again", 'id="rerender"') : "") : live.state === "error" ? btn("Connect", 'id="live-go"') : "");
}
async function liveConnect() {
  if (!mcp) return;
  live.state = "connecting"; renderSource();
  try {
    await mcp.callTool(HX, "list_surfaces", {}, { cache: false });
    live.state = "on"; S.source = "live";
    try { applyTuning((await mcp.callTool(HX, "tune_get", {}, { cache: false })).payload); } catch {}
  } catch (e) { live.state = "error"; live.msg = holodexErr(e); S.source = "published"; }
  renderSource(); showView(); if (S.view === "focus") renderStage(); renderInspect();
}
function applyTuning(t) {
  if (!t || typeof t !== "object") return;
  for (const [n, v] of Object.entries(t)) {
    if (!v || typeof v !== "object") continue;
    tunable.add(n);
    if (!TOK[n]) TOK[n] = { name: n, value: String(v.value ?? ""), file: "PhosphorTheme (Holodex-local)", note: "Holodex-local metric" };
    else if (!TOK[n].value && !v.tuned) TOK[n].value = String(v.value ?? "");
    if (v.tuned) live.applied[n] = String(v.value ?? "");
  }
}
function queueLive(n, v) { if (live.state !== "on" || !tunable.has(n)) return; live.pending[n] = v; clearTimeout(live.timer); live.timer = setTimeout(flushLive, 450); }
async function flushLive() {
  if (live.busy) { live.timer = setTimeout(flushLive, 300); return; }
  const values = live.pending; live.pending = {};
  if (!Object.keys(values).length) return;
  live.busy = true;
  try {
    const p = (await mcp.callTool(HX, "tune_set", { values })).payload || {};
    const refused = Array.isArray(p.refused) ? p.refused.filter(Boolean) : [];
    for (const [n, v] of Object.entries(values)) { if (refused.includes(n)) continue; if (v === null) delete live.applied[n]; else live.applied[n] = String(v); }
    if (p.tuning) applyTuning(p.tuning);
    live.note = refused.length ? `Holodex refused ${refused.join(", ")}. Those stay code changes.` : "Applied to the running app.";
    if (S.view === "focus" && S.source === "live") await loadFocus(true);
  } catch (e) { live.note = holodexErr(e); }
  live.busy = false; renderSource(); renderPending();
}
async function liveReset() {
  try { const r = await mcp.callTool(HX, "tune_reset", {}); live.applied = {}; applyTuning(r.payload); live.note = "Live tuning cleared. Every token is back at its shipped value in the app."; if (S.view === "focus" && S.source === "live") await loadFocus(true); }
  catch (e) { live.note = holodexErr(e); }
  renderSource(); renderPending();
}

/* ---------- StageCanvas (CP-24) with inspect mode and TraceBeam (FX-10) ---------- */
// Same behaviour as @ark/ui StageCanvas: wheel pans, Shift+wheel zooms at the cursor, drag pans, double-click fits,
// arrows, +, - and 0 from focus, and in inspect mode [ ] step through the elements and Enter picks.
function makeStage(host, { width, height, label, readout, elements, pickedId, onPick, fill, inspect = true }) {
  host.innerHTML = `<section class="rp-stage" aria-label="${esc(label)} stage" aria-roledescription="pannable canvas" data-inspect="" tabindex="0"><div class="rp-stage__board" style="width:${width}px;height:${height}px"></div><div class="rp-marks" aria-hidden="true"><i class="rp-marks__mark" data-at="tl"></i><i class="rp-marks__mark" data-at="tr"></i><i class="rp-marks__mark" data-at="bl"></i><i class="rp-marks__mark" data-at="br"></i></div><span class="rp-stage__label">${esc(label)}</span><span class="rp-stage__readout" aria-live="polite"><span class="rp-stage__text"></span><span class="rp-stage__target">Inspect</span><b></b></span><button type="button" class="rp-btn lab-hud" id="hud-back" aria-label="Return to inventory, the previous page">Return to inventory</button></section>`;
  const el = host.firstElementChild, board = el.querySelector(".rp-stage__board"), marks = el.querySelector(".rp-marks");
  const st = { el, board, elements: elements || [], picked: pickedId || null, hover: null, view: { x: 0, y: 0, zoom: 1 }, readout: readout || "" };
  fill(board);
  const beam = (e, state) => { const v = st.view, s = document.createElement("span"); s.className = "rp-trace-beam"; s.setAttribute("aria-hidden", "true"); s.dataset.state = state; s.style.cssText = `left:${v.x + e.x * v.zoom}px;top:${v.y + e.y * v.zoom}px;width:${Math.max(0, e.w * v.zoom)}px;height:${Math.max(0, e.h * v.zoom)}px`; s.innerHTML = `<span class="rp-trace-beam__ring"></span>${state === "hover" ? `<span class="rp-trace-beam__tag">${esc(e.name)}</span>` : ""}`; return s; };
  const draw = () => {
    const v = st.view;
    // The dot grid belongs to the board, so it stops at the board's edges. Its pitch is kept readable on screen at any zoom.
    let pitch = 22 * v.zoom; while (pitch < 16) pitch *= 2; while (pitch > 44) pitch /= 2; board.style.setProperty("--grid-pitch", pitch / v.zoom + "px"); board.style.setProperty("--grid-dot", 1 / v.zoom + "px");
    board.style.transform = `translate(${v.x}px, ${v.y}px) scale(${v.zoom})`;
    el.querySelectorAll(".rp-trace-beam").forEach(b => b.remove());
    const pk = st.elements.find(e => e.id === st.picked), hv = st.elements.find(e => e.id === st.hover);
    if (pk) el.insertBefore(beam(pk, "picked"), marks);
    if (hv && hv.id !== pk?.id) el.insertBefore(beam(hv, "hover"), marks);
    el.querySelector(".rp-stage__text").textContent = st.readout;
    el.querySelector(".rp-stage__target").textContent = (hv || pk)?.name || "Inspect";
    el.querySelector("b").textContent = `${Math.round(v.zoom * 100)}%`;
  };
  // Camera bounds: zoom-out stops at fit-to-board, and no pan, wheel or key can carry the view past the board's edges.
  const box = () => { const b = el.getBoundingClientRect(); return { w: b.width, h: b.height, m: Math.min(48, b.width / 12) }; };
  const fitZoom = () => { const b = box(); return Math.max(0.01, Math.min((b.w - b.m * 2) / width, (b.h - b.m * 2) / height, 1)); };
  const clampView = v => { const b = box(); if (!b.w) return v; const zoom = Math.min(4, Math.max(fitZoom(), v.zoom)); const axis = (pos, size, vp) => size + b.m * 2 <= vp ? (vp - size) / 2 : Math.min(b.m, Math.max(vp - size - b.m, pos)); return { zoom, x: axis(v.x, width * zoom, b.w), y: axis(v.y, height * zoom, b.h) }; };
  const setView = v => { st.view = clampView(v); draw(); };
  const fit = () => { const b = box(); if (!b.w) return; const zoom = fitZoom(); setView({ zoom, x: (b.w - width * zoom) / 2, y: (b.h - height * zoom) / 2 }); };
  const zoomAt = (pt, next) => { const z = Math.min(4, Math.max(fitZoom(), next)), r = z / st.view.zoom; setView({ zoom: z, x: pt.x - (pt.x - st.view.x) * r, y: pt.y - (pt.y - st.view.y) * r }); };
  const under = ev => { const b = el.getBoundingClientRect(), x = (ev.clientX - b.left - st.view.x) / st.view.zoom, y = (ev.clientY - b.top - st.view.y) / st.view.zoom; let best = null; for (const e of st.elements) { if (x < e.x || y < e.y || x > e.x + e.w || y > e.y + e.h) continue; const d = (e.depth ?? 0) - (best?.depth ?? 0); if (!best || d > 0 || d === 0 && e.w * e.h < best.w * best.h) best = e; } return best; };
  const under0 = under; const setHover = e => { if (!inspect) return; const id = e?.id ?? null; if (id !== st.hover) { st.hover = id; draw(); } };
  let drag = null;
  el.addEventListener("wheel", ev => { ev.preventDefault(); const b = el.getBoundingClientRect(); if (ev.shiftKey || ev.ctrlKey) zoomAt({ x: ev.clientX - b.left, y: ev.clientY - b.top }, st.view.zoom * Math.exp(-(ev.deltaY || ev.deltaX) * 0.0015)); else setView({ ...st.view, x: st.view.x - ev.deltaX, y: st.view.y - ev.deltaY }); }, { passive: false });
  el.addEventListener("pointerdown", ev => { if (ev.target.closest(".lab-hud, [data-stage-interactive]")) return; drag = { id: ev.pointerId, x: ev.clientX, y: ev.clientY, travel: 0 }; try { el.setPointerCapture(ev.pointerId); } catch {} });
  el.addEventListener("pointermove", ev => { if (!drag || drag.id !== ev.pointerId) { setHover(under(ev)); return; } const dx = ev.clientX - drag.x, dy = ev.clientY - drag.y; drag = { ...drag, x: ev.clientX, y: ev.clientY, travel: drag.travel + Math.abs(dx) + Math.abs(dy) }; setView({ ...st.view, x: st.view.x + dx, y: st.view.y + dy }); });
  const end = ev => { const a = drag; if (a?.id !== ev.pointerId) return; drag = null; if (inspect && ev.type === "pointerup" && ev.button === 0 && a.travel <= 4) { const t = under(ev); if (t) onPick(t); } };
  el.addEventListener("pointerup", end); el.addEventListener("pointercancel", end);
  el.addEventListener("pointerleave", () => { if (!drag) setHover(null); });
  el.addEventListener("contextmenu", ev => ev.preventDefault());
  el.addEventListener("dblclick", () => { if (!st.hover) fit(); });
  el.addEventListener("keydown", ev => {
    if (ev.target !== el) return;
    const b = el.getBoundingClientRect(), c = { x: b.width / 2, y: b.height / 2 }, k = ev.key, n = st.elements.length;
    const pan = { ArrowLeft: [48, 0], ArrowRight: [-48, 0], ArrowUp: [0, 48], ArrowDown: [0, -48] }[k];
    if (n && (k === "[" || k === "]")) { const at = st.elements.findIndex(e => e.id === st.hover), d = k === "]" ? 1 : -1; st.hover = st.elements[at < 0 ? (d > 0 ? 0 : n - 1) : (at + d + n) % n].id; draw(); }
    else if (k === "Enter" && st.hover) { const h = st.elements.find(e => e.id === st.hover); if (h) onPick(h); }
    else if (k === "0") fit();
    else if (pan) setView({ ...st.view, x: st.view.x + pan[0], y: st.view.y + pan[1] });
    else if (k === "+" || k === "=") zoomAt(c, st.view.zoom * 1.25);
    else if (k === "-") zoomAt(c, st.view.zoom / 1.25);
    else return;
    ev.preventDefault();
  });
  st.fit = fit; st.draw = draw;
  st.set = patch => { Object.assign(st, patch); if (patch.view) st.view = clampView(patch.view); draw(); };
  fit();
  return st;
}

/* ---------- stage: board and focus ---------- */
let stage = null;
// The base bitmap for the focused surface, its element map, and where both came from.
const base = { key: null, src: null, img: null, w: 0, h: 0, map: null, mapState: "none", at: null, err: "", px: null };
const mapCache = {};
const curEls = () => base.key === S.focus && base.map ? base.map.elements || [] : [];
const elById = id => curEls().find(e => e.id === id) || null;
const elName = e => e.name || String(e.id).split("/").pop() || e.type;
function boardLayout(rowW = 3200) {
  const gap = 64, tiles = []; let x = gap, y = gap, rowH = 0, width = 0;
  for (const k of ALL) { const [w, h] = SIZES[k]; if (x > gap && x + w + gap > rowW) { x = gap; y += rowH + gap; rowH = 0; } tiles.push({ id: k, name: k, x, y, w, h }); x += w + gap; rowH = Math.max(rowH, h); width = Math.max(width, x); }
  return { width, height: y + rowH + gap, tiles };
}
// The board opens at a readable size: rows are as wide as the stage shows at 22%, and the rest pans. 0 or a double-click fits it all.
const BOARD_ZOOM = 0.22;
function boardFor(host) { const w = host.getBoundingClientRect().width || 1000; return boardLayout(Math.max(1200, Math.round(w / BOARD_ZOOM))); }
function renderStage() {
  renderActions();
  if (S.view === "inventory") return;
  if (!hasRenders()) return mountEmptyBoard();
  if (S.view === "board") {
    const b = boardFor($("stagehost")), hits = layerSurfaces(S.layer);
    stage = makeStage($("stagehost"), { width: b.width, height: b.height, label: "Board", readout: hits ? `${hits.size} of ${ALL.length} use it` : `${INV.pages} pages // ${INV.elements} elements // ${COMPONENTS.length} components`, elements: b.tiles, onPick: t => goFocus(t.id),
      fill: board => { board.innerHTML = b.tiles.map(t => `<div class="lab-tile" ${hits && !hits.has(t.id) ? "data-dim" : ""} style="left:${t.x}px;top:${t.y}px;width:${t.w}px;height:${t.h}px"><img class="lab-render" src="r/${t.id}.png" alt="${esc(t.id)} render" width="${t.w}" height="${t.h}" draggable="false" loading="lazy"></div>`).join(""); } });
    const sw = stage.el.getBoundingClientRect().width, z = Math.min(1, sw / b.width);
    stage.set({ view: { zoom: z, x: (sw - b.width * z) / 2, y: 0 } });
    renderSource(); return;
  }
  const want = live.state === "on" && S.source === "live" ? "live" : "published";
  if (base.key !== S.focus || base.src !== want || !base.img) { mountFocus(); loadFocus(); } else mountFocus();
  renderSource();
}
// A project with no renders gets an honest empty board, the smallest its empty inventory implies.
function mountEmptyBoard() {
  const P = proj(), r = $("stagehost").getBoundingClientRect(), m = Math.min(48, r.width / 12);
  const w = Math.max(240, Math.round(r.width - m * 2)), h = Math.max(200, Math.round(r.height - m * 2));
  stage = makeStage($("stagehost"), { width: w, height: h, label: P.name, readout: "0 pages // 0 elements // 0 components", elements: [], onPick() {}, inspect: false,
    fill: board => { board.innerHTML = `<div class="lab-empty" data-stage-interactive>${led("idle", "No renders yet")}<h2 class="lab-heading">${esc(P.name)} has no board</h2>${note(`The UI Lab knows ${P.name} from ${P.known}, but nothing renders its pages here.`)}${note("To add a source, give the project a render job that writes one <page>.png and one <page>.json element map per page, the way Holodex's UI renders workflow does, then publish those files beside this lab.")}<div class="lab-actions">${btn("Ask Smith for a source", 'id="ask-source"')}</div></div>`; } });
  stage.el.removeAttribute("data-inspect"); stage.el.querySelector(".rp-stage__target").textContent = "";
  renderSource();
}
// A focused page sits on its own board with a gutter, so the board's dot grid shows around it.
const FOCUS_PAD = 64;
function mountFocus() {
  const w = base.key === S.focus && base.w ? base.w : SIZES[S.focus][0], h = base.key === S.focus && base.h ? base.h : SIZES[S.focus][1], P = FOCUS_PAD;
  stage = makeStage($("stagehost"), { width: w + P * 2, height: h + P * 2, label: S.focus, readout: focusReadout(), elements: curEls().map(e => ({ id: e.id, name: elName(e), x: e.x + P, y: e.y + P, w: e.w, h: e.h, depth: e.depth })), pickedId: S.pick, onPick: e => setPick(e.id),
    fill: board => { board.innerHTML = `<canvas id="cv" class="lab-render lab-page" style="left:${P}px;top:${P}px;width:${w}px;height:${h}px" width="${w}" height="${h}" aria-label="${esc(S.focus)} render"></canvas>`; } });
  paint();
}
function focusReadout() {
  if (base.key !== S.focus || !base.img) return "Loading";
  return `${base.src === "live" ? "Live" : "Published"}${base.mapState === "ok" ? "" : " // No map"}`;
}
let loadSeq = 0;
async function loadFocus(force) {
  const k = S.focus, seq = ++loadSeq;
  const want = live.state === "on" && S.source === "live" ? "live" : "published";
  if (!force) { base.img = null; base.map = null; }
  base.key = k; base.src = want; base.err = "";
  let src, map = null, mapState = "none", w = SIZES[k]?.[0], h = SIZES[k]?.[1], err = "";
  if (want === "live") {
    try {
      const r = await mcp.callTool(HX, "render_surface", { name: k }, { cache: false });
      const im = (r.content || []).find(b => b.type === "image" && typeof b.data === "string");
      if (!im) throw { code: "tool_error", message: "Holodex returned no image for this surface." };
      src = `data:${/^image\/[a-z+.-]+$/.test(im.mimeType || "") ? im.mimeType : "image/png"};base64,${im.data}`;
      const p = r.payload && typeof r.payload === "object" ? r.payload : {};
      if (p.width > 0 && p.height > 0) { w = p.width; h = p.height; }
      if (p.map && Array.isArray(p.map.elements)) { map = p.map; mapState = "ok"; } else mapState = "painted";
    } catch (e) { err = holodexErr(e) + " Showing the published render."; }
  }
  if (!src) {
    src = `r/${k}.png`; w = SIZES[k]?.[0]; h = SIZES[k]?.[1];
    if (MAPS.has(k)) { try { map = mapCache[k] || (mapCache[k] = await (await fetch(`r/${k}.json`)).json()); mapState = Array.isArray(map?.elements) ? "ok" : "none"; if (mapState !== "ok") map = null; } catch { map = null; mapState = "none"; } }
  }
  const img = new Image();
  await new Promise(res => { img.onload = res; img.onerror = res; img.src = src; });
  if (seq !== loadSeq || !img.naturalWidth) return;
  Object.assign(base, { img, w: w || img.naturalWidth, h: h || img.naturalHeight, map, mapState, at: new Date(), err, px: null });
  if (err) { base.src = "published"; live.note = err; }
  if (S.pick && !elById(S.pick)) S.pick = null;
  if (S.view === "focus" && S.focus === k) mountFocus();
  renderSource(); renderInspect();
  if (S.pick && live.state === "on" && base.src === "live") readElement(S.pick);
}
const hex = h => [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16));
const toHex = a => "#" + a.map(v => Math.max(0, Math.min(255, Math.round(v))).toString(16).padStart(2, "0")).join("");
function recolour(px, w, maps, rect) {
  const TOL = 46, rows = px.length / 4 / w, x0 = rect ? Math.max(0, rect.x) : 0, y0 = rect ? Math.max(0, rect.y) : 0, x1 = rect ? Math.min(w, rect.x + rect.w) : w, y1 = rect ? Math.min(rows, rect.y + rect.h) : rows;
  for (let y = y0; y < y1; y++) for (let x = x0; x < x1; x++) {
    const i = (y * w + x) * 4, r = px[i], g = px[i + 1], b = px[i + 2];
    let best = null, bd = TOL;
    for (const m of maps) { const d = Math.abs(r - m.a[0]) + Math.abs(g - m.a[1]) + Math.abs(b - m.a[2]); if (d < bd) { bd = d; best = m; } }
    if (!best) continue;
    const k = 1 - bd / TOL; px[i] = r + best.d[0] * k; px[i + 1] = g + best.d[1] * k; px[i + 2] = b + best.d[2] * k;
  }
}
const cmap = (a, b) => { a = hex(a); b = hex(b); return { a, d: [b[0] - a[0], b[1] - a[1], b[2] - a[2]] }; };
// Colour preview: pixels near a tweaked colour move by that change, fading with distance. Tokens already applied in the
// running app are skipped on a live render, because the app painted them itself.
function paint() {
  const cv = $("cv"); if (!cv || !base.img || base.key !== S.focus) return;
  const { w, h } = base, g = cv.getContext("2d", { willReadFrequently: true });
  g.clearRect(0, 0, w, h); g.drawImage(base.img, 0, 0, w, h);
  if (!base.px) { try { base.px = g.getImageData(0, 0, w, h); } catch { base.px = null; } }
  if (!base.px) return;
  const maps = Object.keys(S.tweaks).filter(n => isColor(tokVal(n)) && isColor(S.tweaks[n]) && S.tweaks[n].toLowerCase() !== tokVal(n).toLowerCase() && !(base.src === "live" && live.applied[n] === S.tweaks[n])).map(n => cmap(tokVal(n), S.tweaks[n]));
  const elc = Object.entries(S.elTweaks).filter(([key]) => key.startsWith(S.focus + "|")).map(([key, t]) => [elById(key.slice(S.focus.length + 1)), t]).filter(([e]) => e);
  if (!maps.length && !elc.some(([, t]) => t.fill || t.ink)) return;
  const img = new ImageData(new Uint8ClampedArray(base.px.data), w, h);
  if (maps.length) recolour(img.data, w, maps);
  for (const [e, t] of elc) { const m = ["fill", "ink"].filter(p => t[p] && isColor(t[p].old) && isColor(t[p].new)).map(p => cmap(t[p].old, t[p].new)); if (m.length) recolour(img.data, w, m, e); }
  g.putImageData(img, 0, 0);
}
function setPick(id) {
  S.pick = id && elById(id) ? id : null;
  if (S.pick) { S.layer = null; setSide("inspect"); }
  stage?.set({ picked: S.pick }); renderInspect(); renderLayers();
  if (S.pick && live.state === "on" && base.src === "live") readElement(S.pick);
}
async function readElement(id) {
  const key = `${S.focus}|${id}`;
  try { const r = await mcp.callTool(HX, "element", { surface: S.focus, id }, { cache: false }); if (r.payload && typeof r.payload === "object") { live.elProps[key] = r.payload; if (S.pick === id) renderInspect(); } } catch {}
}
// Without the live app, a control's colours are read off the render: the commonest colour in its box is the fill, the commonest one far from it is the ink.
function sampleColors(e) {
  if (!base.px) return { fill: "", ink: "" };
  const d = base.px.data, w = base.w, bins = new Map();
  const x0 = Math.max(0, e.x), y0 = Math.max(0, e.y), x1 = Math.min(base.w, e.x + e.w), y1 = Math.min(base.h, e.y + e.h);
  const step = Math.max(1, Math.floor(Math.sqrt((x1 - x0) * (y1 - y0) / 6000)));
  for (let y = y0; y < y1; y += step) for (let x = x0; x < x1; x += step) { const i = (y * w + x) * 4, key = (d[i] >> 3) << 10 | (d[i + 1] >> 3) << 5 | d[i + 2] >> 3; const b = bins.get(key) || [0, 0, 0, 0]; b[0]++; b[1] += d[i]; b[2] += d[i + 1]; b[3] += d[i + 2]; bins.set(key, b); }
  const list = [...bins.values()].sort((a, b) => b[0] - a[0]).map(b => ({ n: b[0], c: [b[1] / b[0], b[2] / b[0], b[3] / b[0]] }));
  if (!list.length) return { fill: "", ink: "" };
  const bg = list[0]; let fg = null, score = 0;
  for (const b of list.slice(1)) { const dist = Math.abs(b.c[0] - bg.c[0]) + Math.abs(b.c[1] - bg.c[1]) + Math.abs(b.c[2] - bg.c[2]); if (dist < 90) continue; if (b.n * dist > score) { score = b.n * dist; fg = b; } }
  return { fill: toHex(bg.c), ink: fg ? toHex(fg.c) : "" };
}

/* ---------- inspect: one UniversalWidget (CP-25) built from the picked thing's spec ---------- */
function tokenControl(name) {
  const cur = tokVal(name).trim();
  if (HEXRE.test(cur)) return { id: name, label: name, type: "color", value: cur };
  const m = NUMBER.exec(cur);
  if (m) { const b = Number(m[1]), unit = m[2] || "", min = unit === "ms" ? Math.min(120, b) : 0, reach = unit === "ms" ? 1000 : unit === "em" || unit === "rem" ? 1 : 64; if (b >= min) return { id: name, label: name, type: "slider", min, max: Math.max(b * 3, reach), step: unit === "ms" ? 10 : Number.isInteger(b) ? 1 : 0.01, value: b, unit }; }
  return { id: name, label: name, type: "text", value: cur };
}
let spec = null;
function buildSpec() {
  const e = S.view === "focus" && S.pick ? elById(S.pick) : null;
  if (e) {
    const lp = live.elProps[`${S.focus}|${e.id}`] || null, smp = sampleColors(e);
    const fill = isColor(lp?.backColor) ? lp.backColor.toLowerCase() : smp.fill, ink = isColor(lp?.foreColor) ? lp.foreColor.toLowerCase() : smp.ink;
    const own = [];
    if (e.text) own.push({ id: "el-text", label: "Text", type: "text", value: String(e.text) });
    own.push({ id: "el-w", label: "Width", type: "slider", min: 0, max: Math.max(base.w, e.w), step: 1, value: e.w, unit: "px" }, { id: "el-h", label: "Height", type: "slider", min: 0, max: Math.max(base.h, e.h), step: 1, value: e.h, unit: "px" });
    if (fill) own.push({ id: "el-fill", label: "Fill", type: "color", value: fill });
    if (ink) own.push({ id: "el-ink", label: "Ink", type: "color", value: ink });
    return { title: elName(e).slice(0, 32), component: e.component, element: e, controls: [...own, ...(e.tokens || []).filter(n => TOK[n]).map(tokenControl)] };
  }
  if (S.layer) { const toks = layerTokens(S.layer).filter(n => TOK[n]); if (toks.length) return { title: layerName(S.layer).slice(0, 32), component: { token: "Token", component: "Component", effect: "Effect" }[S.layer.kind], controls: toks.map(tokenControl) }; }
  return null;
}
const elKey = () => `${S.focus}|${S.pick}`;
function widgetValue(c) {
  if (c.id.startsWith("el-")) { const t = S.elTweaks[elKey()]?.[c.id.slice(3)]; return t ? (c.type === "slider" ? Number(t.new) : t.new) : c.value; }
  const t = S.tweaks[c.id]; if (t === undefined) return c.value;
  if (c.type === "slider") { const m = NUMBER.exec(t); return m ? Number(m[1]) : c.value; }
  return t;
}
const valueText = (c, v) => c.type === "slider" ? `${v}${c.unit ?? ""}` : String(v);
function renderInspect() {
  const e = S.view === "focus" && S.pick ? elById(S.pick) : null;
  spec = hasRenders() ? buildSpec() : null;
  let html = "";
  if (!hasRenders()) { $("side-inspect").innerHTML = note(`${proj().name} has no renders yet, so there is nothing to pick or tune.`); return; }
  if (e) {
    const lp = live.elProps[elKey()] || null;
    const txt = v => typeof v === "string" ? v : JSON.stringify(v);
    html += `<div class="lab-facts"><h3 class="lab-heading">${esc(elName(e))}</h3><dl class="lab-list"><dt>Type</dt><dd>${esc(e.type)}</dd><dt>Component</dt><dd>${esc(e.component)}</dd><dt>Box</dt><dd>${e.w} x ${e.h} at ${e.x}, ${e.y}</dd>${lp?.font ? `<dt>Font</dt><dd>${esc(txt(lp.font))}</dd>` : ""}${lp?.padding ? `<dt>Padding</dt><dd>${esc(txt(lp.padding))}</dd>` : ""}<dt>Source</dt><dd>${lp ? "the running app" : "element map, colours sampled"}</dd></dl></div>`;
  } else if (S.layer) {
    const hits = layerSurfaces(S.layer);
    html += `<div class="lab-facts"><h3 class="lab-heading">${esc(layerName(S.layer))}</h3>${note(S.layer.kind === "token" ? (TOK[S.layer.id]?.note || TOK[S.layer.id]?.file || "Holodex token.") : `${S.layer.kind === "component" ? "Component" : "Effect"} on ${hits ? hits.size : 0} of ${ALL.length} surfaces. The board dims the rest.`)}</div>`;
  } else html += note(S.view === "focus" ? (base.mapState === "ok" ? "Pick a control on the surface, or a component, effect or token under Layers, to get its controls." : (base.mapState === "painted" ? "This surface is painted, not built from controls, so it has no element map." : "No element map for this render yet.") + " Pick a component, effect or token under Layers to get its controls.") : "Focus a surface and pick a control on it, or pick a component, effect or token under Layers, to get its controls.");
  if (spec) {
    const st = live.state === "on" && spec.controls.some(c => tunable.has(c.id)) ? "live" : "pending";
    html += `<div class="rp-panel rp-widget" data-state="${st}" role="group" aria-labelledby="w-title"><header class="rp-widget__head"><span class="rp-widget__title" id="w-title">${esc(spec.title)}</span>${spec.component ? `<span class="rp-widget__meta">${esc(spec.component)}</span>` : ""}<span class="rp-widget__state">${st === "live" ? "Live" : "Pending"}</span></header><div class="rp-widget__rows">${spec.controls.map(c => {
      const v = widgetValue(c), ch = v !== c.value, id = "w-" + c.id;
      if (c.type === "text") return `<div class="rp-widget__row" data-row="${esc(c.id)}" ${ch ? 'data-changed=""' : ""}>${textField(c.label, v, `data-w="${esc(c.id)}"`)}</div>`;
      return `<div class="rp-widget__row" data-row="${esc(c.id)}" ${ch ? 'data-changed=""' : ""}><label class="rp-widget__label" for="${esc(id)}">${esc(c.label)}</label><output class="rp-widget__value" for="${esc(id)}">${esc(valueText(c, v))}</output>${c.type === "slider" ? `<input class="rp-widget__range" id="${esc(id)}" type="range" min="${c.min}" max="${c.max}" step="${c.step ?? 1}" value="${Number(v)}" data-w="${esc(c.id)}">` : `<input class="rp-widget__swatch" id="${esc(id)}" type="color" value="${esc(HEXRE.test(String(v)) ? v : c.value)}" data-w="${esc(c.id)}">`}</div>`;
    }).join("")}</div><footer class="rp-widget__foot">${btn("Reset", 'id="w-reset"')}${btn("Promote to a PR", 'id="promote"')}</footer></div>`;
  } else if (e) html += note("The element map lists no tokens for this control.");
  const f = S.view === "focus" ? FINDINGS[S.focus] || [] : [];
  if (f.length) html += accordion("findings", "Audit findings", f.length, `<dl class="lab-list">${f.map(([id, , t]) => `<dt>${esc(id)}</dt><dd>${esc(t)}</dd>`).join("")}</dl>`);
  $("side-inspect").innerHTML = html + `<div class="lab-facts" id="pending"></div>`;
  renderPending();
}
function renderPending() {
  const host = $("pending"); if (!host) return;
  const toks = Object.entries(S.tweaks), els = Object.entries(S.elTweaks).flatMap(([key, t]) => Object.entries(t).map(([p, v]) => [key, p, v]));
  const total = toks.length + els.length;
  host.innerHTML = `<h3 class="lab-heading">Pending tweaks: ${total}</h3>` + (total ? `<ul class="lab-tweaks">${toks.map(([n, v]) => `<li><span>${esc(n)}: ${esc(tokVal(n) || "(unset)")} to <b>${esc(v)}</b>${live.applied[n] === v ? " (live)" : ""}</span>${btn("Undo", `data-undo="${esc(n)}"`)}</li>`).join("")}${els.map(([key, p, v]) => `<li><span>${esc(key.split("|")[1].split("/").pop())} ${esc(p)}: ${esc(v.old === "" ? "(none)" : v.old)} to <b>${esc(v.new)}</b></span>${btn("Undo", `data-elundo="${esc(key)}" data-p="${esc(p)}"`)}</li>`).join("")}</ul>` : note(live.state === "on" ? "None. Live tokens change the running app as you edit; everything else is held here until Smith ships it." : "None. A tweak is held here and written nowhere until Smith ships it.")) +
    `<div class="lab-actions">${btn("Clear all", `id="clear" ${total ? "" : "disabled"}`)}${live.state === "on" ? btn("Reset live tuning", 'id="livereset"') : ""}</div>`;
  const reset = $("w-reset"); if (reset && spec) reset.disabled = !spec.controls.some(c => widgetValue(c) !== c.value);
}
function widgetInput(id, raw) {
  const c = spec?.controls.find(x => x.id === id); if (!c) return;
  const v = c.type === "slider" ? Number(raw) : String(raw);
  if (id.startsWith("el-")) {
    const p = id.slice(3), key = elKey(), t = S.elTweaks[key] || {};
    if (v === c.value) delete t[p]; else t[p] = { old: String(c.value), new: String(v) };
    if (Object.keys(t).length) S.elTweaks[key] = t; else delete S.elTweaks[key];
  } else {
    const text = valueText(c, v).trim();
    if (v === c.value || !text) { delete S.tweaks[id]; queueLive(id, null); } else { S.tweaks[id] = text; queueLive(id, text); }
  }
  save();
  const r = document.querySelector(`[data-row="${CSS.escape(id)}"]`);
  if (r) { r.toggleAttribute("data-changed", widgetValue(c) !== c.value); const o = r.querySelector("output"); if (o) o.textContent = valueText(c, widgetValue(c)); }
  paint(); renderPending();
}

/* ---------- Smith thread (GalaThread, panel variant) ---------- */
const preamble = () => `You are Smith (registry id claude), conducting this ${proj().name} UI Lab thread for Justin.
Delegate by layer. UI/UX and frontend work goes to Metis (4GE Interface stage). Live-app bridge and gateway work (tools/holodex-mcp, the control pipe, the host MCP server) goes to Iris (middleware). Metis and Iris are draft-only: they propose drafts, you build and ship.
Build in ${repoName()} (Hivemind missions accept any repo Justin can push to, ark PR #1818). Ship as a draft PR labelled solatree:graft with SolaTree.
In each reply say who did what (Smith, Metis, Iris) and give the PR link once there is one. Use no em or en dashes.`;
const flowOn = id => store.get("flow-" + id, FLOWS.find(f => f[0] === id)[2]);
function brief(ask, first, limit = 7800) {
  const e = S.pick ? elById(S.pick) : null;
  const tw = Object.entries(S.tweaks).map(([n, v]) => `- ${n}: ${tokVal(n) || "(unset)"} -> ${v} (${tunable.has(n) ? (live.applied[n] === v ? "live, applied in the running app" : "live, tunable") : "code"}; ${TOK[n]?.file || "PhosphorTheme"})`);
  const et = Object.entries(S.elTweaks).flatMap(([key, t]) => Object.entries(t).map(([p, v]) => `- ${key.replace("|", " // ")} ${p}: ${v.old === "" ? "(none)" : v.old} -> ${v.new} (code)`));
  const f = (FINDINGS[S.focus] || []).map(x => `- ${x[0]} ${x[2]}`);
  const steps = [
    flowOn("edit") && (hasRenders() ? `Edit: work in ${repoName()} on a new branch from master. Holodex is .NET 10 WinForms; tokens live in PhosphorTokens.cs and PhosphorTheme.cs, components in PhosphorKit.cs, PhosphorComposer.cs and the *Form.cs files.` : `Edit: work in ${repoName()} on a new branch from its default branch.`),
    flowOn("build") && hasRenders() && "Build: dotnet build Holodex.csproj -c Release (on Linux add -p:EnableWindowsTargeting=true). 0 errors, 0 new warnings.",
    flowOn("tests") && hasRenders() && "Tests: dotnet test tests/Holodex.Tests with the CI filter (Windows CI runs them).",
    flowOn("renders") && hasRenders() && "Renders: the UI renders workflow runs on the PR; compare the ui-renders artifact for this surface before and after and say what changed.",
    flowOn("audit") && hasRenders() && "Audit doc: update docs/UI_AUDIT.md for any finding this closes.",
    flowOn("graft") && "Ship with SolaTree: open a draft PR labelled solatree:graft. No em or en dashes in copy, commits or the PR.",
  ].filter(Boolean).map((s, i) => `${i + 1}. ${s}`);
  const P = proj(); if (!hasRenders()) { const txt = [first ? preamble() + "\n" : "", `Request: ${ask}`, `Project: ${P.name} (${P.type}), repo ${repoName()}. It has no renders in the UI Lab yet.`, steps.length ? `Dev flows ticked:\n${steps.join("\n")}` : "", "Report the branch and the PR link."].filter(Boolean).join("\n"); return txt.slice(0, limit); }
  const size = base.key === S.focus && base.w ? `${base.w} x ${base.h} px` : `${SIZES[S.focus][0]} x ${SIZES[S.focus][1]} px`;
  const parts = [
    first ? preamble() + "\n" : "",
    `Request: ${ask}`,
    `Surface: ${S.focus} (${surfaceGroup(S.focus)}), ${size}, ${base.src === "live" && base.key === S.focus ? "seen live from the running app" : `seen as the published render of UI renders run ${RUN}`}.`,
    e ? `Picked element: ${e.id} // type ${e.type} // component ${e.component} // bounds x ${e.x}, y ${e.y}, w ${e.w}, h ${e.h}${e.text ? ` // text "${String(e.text).slice(0, 120)}"` : ""} // tokens ${(e.tokens || []).join(", ") || "none"}` : S.layer ? `Layer: ${S.layer.kind} ${layerName(S.layer)} (tokens: ${layerTokens(S.layer).join(", ")})` : "",
    tw.length ? `Pending token tweaks:\n${tw.join("\n")}` : "Pending token tweaks: none",
    et.length ? `Pending element tweaks:\n${et.join("\n")}` : "",
    f.length ? `Audit findings on this surface:\n${f.join("\n")}` : "Audit findings on this surface: none",
    steps.length ? `Dev flows ticked:\n${steps.join("\n")}` : "Dev flows ticked: none (talk it through only)",
    "Report the branch, the PR link, the build and test result, and the render comparison.",
  ].filter(Boolean).join("\n");
  return parts.length > limit ? parts.slice(0, limit - 60) + "\n[brief cut to fit the message limit]" : parts;
}
let mcp = null, hm = "checking", polling = null, pollLeft = 0, sending = false, fb = { status: "", error: "" };
function errText(e) {
  const c = e?.code, m = String(e?.message || "");
  if (c === "server_not_connected") return "Hivemind is not connected for you. Add it in claude.ai Settings, Connectors.";
  if (c === "needs_reauth") return "Hivemind needs signing in again. Reconnect it in claude.ai Settings, Connectors.";
  if (c === "not_in_manifest" || c === "consent_required") return "Hivemind is turned off for this page. Allow it from the artifact's Permissions menu, then send again.";
  if (c === "approval_required") return "This needs your approval. Send again and allow the Hivemind call.";
  if (c === "blocked_by_policy") return "Your organization's policy blocks this Hivemind tool.";
  if (c === "server_unavailable" || c === "upstream_error") return "Hivemind did not answer in time. It may still have gone through: refresh the session before sending again.";
  if (c === "tool_error") return /workspaceRepository|enum/i.test(m) ? `Hivemind refused ${repoName()} as the mission repo. This Hivemind may not have the any-repo change yet; send it as a thread message for now. ${m.slice(0, 200)}` : `Hivemind refused it: ${m.slice(0, 300)}`;
  return `The call failed (${c || "error"}): ${m.slice(0, 200)}`;
}
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
// Hivemind answers {ok, data, audit}. The session id is looked up by name, never guessed from the first id in the reply.
function findSessionId(p) { const d = p?.data ?? p ?? {}; for (const c of [d.sessionId, d.session?.id, d.session?.sessionId, p?.sessionId, d.message?.sessionId, d.ask?.sessionId]) if (typeof c === "string" && UUID.test(c)) return c; return null; }
const sessionOf = p => p?.data?.session || p?.session || null;
const prLinks = t => [...new Set(String(t || "").match(/https:\/\/github\.com\/[\w.-]+\/[\w.-]+\/pull\/\d+/g) || [])];
const lifeState = s => /complete|succe|deliver|done/i.test(s) ? "done" : /fail|error|cancel|refus|timeout/i.test(s) ? "failed" : /run|start|progress|work/i.test(s) ? "running" : /queue|pend|sent|accept/i.test(s) ? "queued" : "idle";
const missionState = m => String(m.status?.data?.execution?.status || m.status?.data?.status || m.status?.status || m.receipt?.data?.execution?.status || m.receipt?.data?.status || m.receipt?.status || "sent");
function setFeedback(status, error) { fb = { status: status || "", error: error || "" }; $("feedback").innerHTML = (fb.status ? `<p role="status">${esc(fb.status)}</p>` : "") + (fb.error ? `<p role="alert">${esc(fb.error)}</p>` : ""); }
function renderThread() {
  const s = S.thread;
  $("t-title").textContent = s?.title || "Smith thread";
  if (s?.messages?.length) {
    const rec = Object.fromEntries((s.receipts || []).map(r => [r.executionId, r]));
    $("msgs").innerHTML = s.messages.map(m => {
      const user = m.role === "user", agent = m.role === "agent", text = String(m.content || "");
      const shown = user && text.includes("\nSurface: ") ? (text.match(/Request: ([\s\S]*?)\nSurface: /)?.[1] || text) : agent ? `${agentLabel(m.agentId)}: ${text}` : text;
      const r = m.executionId ? rec[m.executionId] : null, prs = prLinks(text + " " + (r?.result || ""));
      const cards = (r ? life(lifeState(r.status || ""), r.status || "receipt") : "") + prs.map(link).join("");
      return `<article data-role="${user ? "user" : agent ? "assistant" : "status"}"><strong>${user ? "You" : agent ? "Smith" : "Mission status"}</strong><p>${esc(shown)}</p>${cards ? `<div class="lab-cards">${cards}</div>` : ""}</article>`;
    }).join("");
    $("msgs").scrollTop = $("msgs").scrollHeight;
  } else $("msgs").innerHTML = `<p>Nothing sent yet. Smith hands UI work to Metis and bridge work to Iris, then builds what they draft and ships it with SolaTree.</p>`;
  const waiting = !!polling || sending;
  $("ask").disabled = sending; $("send").disabled = sending; $("send").textContent = sending ? "Working" : "Send"; $("send").setAttribute("aria-label", sending ? "Working" : "Send");
  $("mission").disabled = sending;
  if (waiting && !fb.error && !sending) setFeedback("Smith is working", "");
  renderThreadMore();
}
function renderThreadMore() {
  const s = S.thread, sel = S.missions[S.mission];
  const flows = FLOWS.filter(f => flowOn(f[0])).length;
  let html = accordion("brief", "Brief and dev flows", `${flows} of ${FLOWS.length} flows`, FLOWS.map(([id, label]) => `<label class="rp-field rp-field--checkbox"><input class="rp-field__checkbox" type="checkbox" id="flow-${id}" ${flowOn(id) ? "checked" : ""}><span class="rp-field__label">${esc(label)}</span></label>`).join("") + `<p class="lab-brief" id="briefview">${esc(S.open.brief ? brief($("ask").value.trim() || "(your message)", !S.sessionId) : "")}</p>`);
  html += accordion("session", "Session", hm === "ok" ? (s ? s.status || "open" : "none yet") : hm === "checking" ? "checking" : "Hivemind unavailable", `<dl class="lab-list"><dt>Hivemind</dt><dd>${hm === "ok" ? "connected, asks on first send" : hm === "checking" ? "checking" : hm === "none" ? "not available in this view" : "not connected for you"}</dd><dt>Session</dt><dd>${esc(S.sessionId || "opens with your first message")}</dd></dl><div class="lab-actions">${btn("Refresh", `id="t-refresh" ${mcp && S.sessionId ? "" : "disabled"}`)}${btn("New thread", `id="t-new" ${S.sessionId || s ? "" : "disabled"}`)}${btn("Add note", `id="postnote" title="Adds your draft to the session without giving Smith a turn" ${mcp && S.sessionId ? "" : "disabled"}`)}</div>`);
  if (S.missions.length) html += accordion("missions", "Missions", S.missions.length, S.missions.map((m, i) => row({ title: m.ask.slice(0, 60), desc: `${m.surface} // ${String(m.at).slice(0, 16).replace("T", " ")}`, trailing: life(lifeState(missionState(m)), missionState(m).slice(0, 16)), selected: S.mission === i, attrs: `data-mission="${i}"` })).join("") +
    (sel ? `<div class="lab-facts"><dl class="lab-list"><dt>Execution</dt><dd>${esc(sel.id || "none came back")}</dd>${prLinks(JSON.stringify([sel.status, sel.deliver])).map(l => `<dt>PR</dt><dd>${link(l)}</dd>`).join("")}${sel.approvalUrl ? `<dt>Approval</dt><dd><a class="rp-link" href="${esc(sel.approvalUrl)}" target="_blank" rel="noopener">Open the approval</a></dd>` : ""}</dl>${sel.msg ? note(sel.msg) : ""}${textField("Approval id", sel.approvalId || "", `id="mapprove" data-mapprove="${S.mission}"`, "From the Hivemind portal, after you approve the delivery.")}<div class="lab-actions">${btn("Read status", `data-mcall="status" ${sel.id ? "" : "disabled"}`)}${btn("Deliver PR", `data-mcall="deliver" ${sel.id ? "" : "disabled"}`)}${btn("Cancel", `data-mcall="cancel" ${sel.id ? "" : "disabled"}`)}</div></div>` : ""));
  $("thread-more").innerHTML = html;
}
async function refreshThread(quiet) {
  if (!mcp || !S.sessionId) return;
  try {
    const s = sessionOf((await mcp.callTool(HM, "read_session", { sessionId: S.sessionId }, { cache: false })).payload);
    if (s) { S.thread = { id: s.id, title: s.title, status: s.status, messages: (s.messages || []).slice(-40).map(m => ({ id: m.id, role: m.role, agentId: m.agentId, content: String(m.content || "").slice(0, 9000), createdAt: m.createdAt, executionId: m.executionId })), receipts: (s.receipts || []).slice(-40).map(x => ({ executionId: x.executionId, status: x.status, result: String(x.result || "").slice(0, 2000) })) }; save(); }
    if (!quiet) setFeedback("", "");
  } catch (e) { stopPoll(); if (!quiet) setFeedback("", errText(e)); }
  renderThread();
}
function stopPoll() { clearInterval(polling); polling = null; }
function startPoll() {
  stopPoll(); pollLeft = 36;
  polling = setInterval(async () => {
    if (document.hidden) return;
    const s = S.thread, last = s?.messages?.[s.messages.length - 1];
    if (--pollLeft <= 0 || (last && last.role === "agent" && s.status !== "running") || /fail/i.test(s?.status || "")) { stopPoll(); setFeedback(/fail/i.test(s?.status || "") ? "" : s?.status === "paused" ? "Paused for you: reply to continue" : "", /fail/i.test(s?.status || "") ? "Hivemind marks this session as failed. Start a new thread or run it as a mission." : ""); renderThread(); return; }
    await refreshThread(true);
  }, 10000);
}
// GalaThread contract: a draft needs at least 3 non-space characters, a failed send keeps the draft, and nothing is marked sent that was not.
function draft() { const t = $("ask").value.trim(); if (t.length < 3) { setFeedback("", "Write the message first."); $("ask").focus(); return ""; } return t; }
async function send() {
  const ask = draft(); if (!ask || sending) return;
  const first = !S.sessionId;
  if (!mcp) { S.open.brief = true; renderThreadMore(); return setFeedback("", "Hivemind is not reachable from this view, so nothing was sent. The brief is under Brief and dev flows."); }
  const input = { agentId: "claude", content: brief(ask, first), submissionId: uuid() };
  const title = `${proj().name} UI Lab // ${hasRenders() ? S.focus : "project"} // ${input.submissionId.slice(0, 8)}`;
  if (first) input.title = title; else input.sessionId = S.sessionId;
  sending = true; setFeedback("Sending to Smith", ""); renderThread();
  try {
    const r = await mcp.callTool(HM, "ask_agent", input);
    let id = S.sessionId || findSessionId(r.payload);
    if (!id) { try { id = ((await mcp.callTool(HM, "list_sessions", {}, { cache: false })).payload?.data?.sessions || []).find(x => x.title === title)?.id || null; } catch {} }
    if ($("ask").value.trim() === ask) $("ask").value = "";
    sending = false;
    if (id) { S.sessionId = id; save(); setFeedback("", ""); await refreshThread(true); const last = S.thread?.messages?.[S.thread.messages.length - 1]; if (!last || last.role !== "agent") startPoll(); }
    else setFeedback("", "Hivemind took the message but did not say which session it opened. Read the reply in the Hivemind portal.");
  } catch (e) { sending = false; setFeedback("", errText(e)); }
  renderThread();
}
async function postNote() {
  const ask = draft(); if (!ask || !mcp || !S.sessionId) return;
  try { await mcp.callTool(HM, "post_session_message", { sessionId: S.sessionId, submissionId: uuid(), content: brief(ask, false) }); $("ask").value = ""; setFeedback("Note added. Smith reads it on the next turn.", ""); await refreshThread(true); }
  catch (e) { setFeedback("", errText(e)); }
}
async function runMission() {
  const ask = draft(); if (!ask) return;
  if (!mcp) return setFeedback("", "Hivemind is not reachable from this view, so no mission was sent.");
  const input = { objective: brief(ask, true, 19500), targetAgentId: "claude", executionMode: "execute", submissionId: uuid(), taskScope: `${proj().id}-ui-lab-${hasRenders() ? S.focus : "project"}`.slice(0, 160), timeoutSeconds: 1800 };
  if (proj().repo) input.workspaceRepository = proj().repo;
  sending = true; setFeedback("Sending the mission", ""); renderThread();
  try {
    const p = (await mcp.callTool(HM, "dispatch_agent_mission", input)).payload || {}, d = p.data || p;
    const id = [d.executionId, d.execution?.executionId, d.execution?.id, d.receipt?.executionId, d.mission?.executionId].find(x => typeof x === "string" && UUID.test(x)) || null;
    S.missions.push({ id, ask, surface: S.focus, mode: "execute", at: new Date().toISOString(), receipt: p }); S.mission = S.missions.length - 1; S.open.missions = true;
    save(); $("ask").value = ""; setFeedback(id ? "Mission sent to Smith. Its receipt is under Missions." : "", id ? "" : "Mission sent, but no execution id came back.");
  } catch (e) { setFeedback("", errText(e)); }
  sending = false; renderThread();
}
async function missionCall(tool) {
  const m = S.missions[S.mission]; if (!mcp || !m?.id) return;
  try {
    if (tool === "status") { m.status = (await mcp.callTool(HM, "agent_mission_status", { executionId: m.id }, { cache: false })).payload; m.msg = "Status read."; }
    if (tool === "deliver") { const input = { executionId: m.id, graft: true }; if (m.approvalId) input.approvalId = m.approvalId; const p = (await mcp.callTool(HM, "deliver_mission_branch", input)).payload; m.deliver = p; const l = JSON.stringify(p).match(/https:\/\/[^"\s\\]+/); m.approvalUrl = !m.approvalId && l ? l[0] : ""; m.msg = m.approvalId ? "Delivery started. Read status for the PR link." : "Approve the delivery in the Hivemind portal, paste the approval id, then deliver again."; }
    if (tool === "cancel") { m.status = (await mcp.callTool(HM, "cancel_agent_mission", { executionId: m.id })).payload; m.msg = "Cancel sent."; }
  } catch (e) { m.msg = errText(e); }
  save(); renderThreadMore();
}

/* ---------- inventory: FolderPocket (CP-36, Concept) per project, on blueprint rows ---------- */
const FP = { back: "M0 56A22 22 0 0 1 22 34L146 34C170 34 172 0 198 0L318 0A22 22 0 0 1 340 22L340 250A22 22 0 0 1 318 272L22 272A22 22 0 0 1 0 250Z", front: "M0 22A22 22 0 0 1 22 0L118 0C144 0 146 32 172 32L318 32A22 22 0 0 1 340 54L340 176A22 22 0 0 1 318 198L22 198A22 22 0 0 1 0 176Z" };
const tokRgba = (name, a) => { const v = getComputedStyle(document.documentElement).getPropertyValue("--" + name).trim(); const m = /^#([0-9a-f]{6})$/i.exec(v); if (!m) return "transparent"; const n = parseInt(m[1], 16); return `rgba(${n >> 16},${n >> 8 & 255},${n & 255},${a})`; };
function fpRng(seed) { let a = seed >>> 0; return () => { a = (a + 0x6d2b79f5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
// Back panel art: the token contour field of the spec (patina and bone lines, a faint ember corner).
function fpArt(c, seed) {
  const g = c.getContext("2d"), w = c.width, h = c.height, r = fpRng(seed), ph = [];
  for (let i = 0; i < 6; i++) ph.push([r() * 6.28, 0.6 + r() * 1.8, 0.4 + r() * 1.4, r() * 6.28]);
  const f = (x, y) => { let v = 0; for (let i = 0; i < ph.length; i++) { const p = ph[i]; v += Math.sin(x / w * p[1] * 6.28 + p[0] + Math.cos(y / h * p[2] * 6.28 + p[3]) * 1.4) / (i + 1); } return v; };
  g.fillStyle = tokRgba("color-surface", 1); g.fillRect(0, 0, w, h);
  const wash = (cx, cy, rad, col) => { const gr = g.createRadialGradient(cx, cy, 0, cx, cy, rad); gr.addColorStop(0, col); gr.addColorStop(1, "transparent"); g.fillStyle = gr; g.fillRect(0, 0, w, h); };
  wash(w * 0.78, h * 0.12, w * 0.7, tokRgba("color-patina", 0.18)); wash(w * 0.15, h * 0.95, w * 0.6, tokRgba("color-ember", 0.12));
  for (let lv = 0; lv < 18; lv++) { const k = -1.6 + lv * (3.2 / 18); g.beginPath(); for (let x = 0; x <= w; x += 4) for (let y = 0; y <= h; y += 4) { const a = f(x, y) - k, b = f(x + 4, y) - k; if ((a < 0) !== (b < 0)) { g.moveTo(x, y); g.lineTo(x + 4, y + 1); } } g.strokeStyle = lv % 4 === 0 ? tokRgba("color-patina", 0.3) : tokRgba("color-bone", 0.07); g.lineWidth = 1; g.stroke(); }
  c.setAttribute("data-ready", "");
}
function projectSheets(P) {
  if (!P.renders) return [];
  const groups = SURF_GROUPS.map(([n]) => [n, ALL.filter(k => surfaceGroup(k) === n).length]).filter(g => g[1]);
  // More state: six or more files show four and a +N more sheet.
  return groups.length > 5 ? [{ more: groups.length - 4 }, ...groups.slice(0, 4).map(([n, c], i) => ({ n, m: `${c} pages`, art: i === 3 }))] : groups.map(([n, c], i) => ({ n, m: `${c} pages`, art: i === groups.length - 1 }));
}
function folder(P, i) {
  const sheets = projectSheets(P), k = sheets.length;
  const meta = P.renders ? `${INV.pages} pages // ${INV.elements} elements` : "No renders yet";
  return `<div class="fp-slot"><div class="fp" data-project="${P.id}" style="--fp-i:${i}">
    <div class="fp-stage"><div class="fp-tilt"><div class="fp-folder"><div class="fp-shadow" aria-hidden="true"></div>
      <div class="fp-back" aria-hidden="true" style="clip-path:path('${FP.back}')"><canvas class="fp-art" width="364" height="296"></canvas><span class="fp-back-eb">${esc(P.type)}</span></div>
      <svg class="fp-rim fp-back-rim" viewBox="0 0 340 272" aria-hidden="true"><path d="${FP.back}"/></svg>
      <div class="fp-front" style="clip-path:path('${FP.front}')"><div class="fp-tint" aria-hidden="true"></div><div class="fp-wash" aria-hidden="true"></div><div class="fp-edge" aria-hidden="true"></div><div class="fp-spec" aria-hidden="true"></div>
        <svg class="fp-rim fp-front-rim" viewBox="0 0 340 198" aria-hidden="true"><defs><linearGradient id="fp-rim-${P.id}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="currentColor" stop-opacity=".8"/><stop offset=".35" stop-color="currentColor" stop-opacity=".25"/><stop offset="1" stop-color="currentColor" stop-opacity=".08"/></linearGradient></defs><path d="${FP.front}" stroke="url(#fp-rim-${P.id})"/></svg>
        <button type="button" class="fp-hit" data-open-project="${P.id}" aria-label="Open ${esc(P.name)}, ${esc(P.type)}, ${esc(meta)}"></button>
        <div class="fp-lbl"><span class="fp-ttl">${esc(P.name)}</span><span class="fp-meta">${esc(meta)}</span></div></div></div>
      <div class="fp-files" aria-hidden="true">${sheets.map((it, v) => { const depth = k - 1 - v, off = v - (k - 1) / 2, j = fpRng(v * 97 + i * 13 + 5), jx = (j() - 0.5) * 8, jr = (j() - 0.5) * 3;
        return `<span class="fp-sh${it.more ? " more" : it.art ? " art" : ""}" style="z-index:${v + 10};--dl:${depth * 30}ms;--rx:${(off * 3 + jx).toFixed(1)}px;--ry:${(-depth * 5 + 18).toFixed(1)}px;--rr:${(off * 3 + jr).toFixed(2)}deg;--hx:${(off * 30 + jx).toFixed(1)}px;--hy:${(-depth * 10 - 34).toFixed(1)}px;--hr:${(off * 8 + jr).toFixed(2)}deg;--ts:${(1 - Math.abs(off) * 0.03).toFixed(3)}">${it.more ? `<span class="fp-hd"><span class="fp-nm">More</span><span class="fp-mt">groups</span></span><span class="fp-big">+${it.more}</span>` : `${it.art ? "" : '<span class="fp-ln"><i style="width:86%"></i><i style="width:92%"></i><i style="width:64%"></i><i style="width:88%"></i><i style="width:42%"></i></span>'}<span class="fp-hd"><span class="fp-nm">${esc(it.n)}</span><span class="fp-mt">${esc(it.m)}</span></span>`}</span>`; }).join("")}</div>
    </div></div></div></div>`;
}
function renderInventory() {
  let i = 0;
  $("inventory").innerHTML = BLUEPRINT_ROWS.map(([id, label]) => { const list = PROJECTS.filter(p => p.row === id); return list.length ? `<section class="lab-row" aria-label="${esc(label)}"><h2 class="rp-eyebrow">${esc(label)} // ${list.length}</h2><div class="lab-shelf">${list.map(p => folder(p, i++)).join("")}</div></section>` : ""; }).join("") +
    note("Each folder is one project, shelved by its Atlas blueprint type. Holodex is the only project with renders today; the others open to an empty board.");
  paintFolders();
  requestAnimationFrame(() => document.querySelectorAll(".fp").forEach(f => f.setAttribute("data-entered", "")));
}
function paintFolders() { document.querySelectorAll(".fp").forEach((f, i) => fpArt(f.querySelector(".fp-art"), 7 + i * 31)); }
// Pointer parallax: the folder tilts a little and a specular follows the cursor across the glass. Off under reduced motion.
document.addEventListener("pointermove", ev => {
  const f = ev.target.closest?.(".fp"); if (!f || reduced() || ev.pointerType !== "mouse") return;
  const r = f.getBoundingClientRect(); f.style.setProperty("--px", ((ev.clientX - r.left) / r.width * 2 - 1).toFixed(3)); f.style.setProperty("--py", ((ev.clientY - r.top) / r.height * 2 - 1).toFixed(3));
});
document.addEventListener("pointerover", ev => { const f = ev.target.closest?.(".fp-front"); if (f) f.closest(".fp").setAttribute("data-hover", ""); });
document.addEventListener("pointerout", ev => { const f = ev.target.closest?.(".fp-front"); if (f && !f.contains(ev.relatedTarget)) { const fp = f.closest(".fp"); fp.removeAttribute("data-hover"); fp.style.setProperty("--px", 0); fp.style.setProperty("--py", 0); } });
document.addEventListener("focusin", ev => { if (ev.target.classList?.contains("fp-hit")) ev.target.closest(".fp").setAttribute("data-hover", ""); });
document.addEventListener("focusout", ev => { if (ev.target.classList?.contains("fp-hit")) ev.target.closest(".fp").removeAttribute("data-hover"); });

/* ---------- project open and return: the blueprint rolls between the folder and the StageCanvas ---------- */
const rectOf = el => { const r = el.getBoundingClientRect(); return { left: r.left, top: r.top, width: r.width, height: r.height }; };
function roll(from, to, done) {
  if (reduced() || !from.width || !to.width) return done();
  const g = document.createElement("div"); g.className = "lab-roll"; g.setAttribute("aria-hidden", "true");
  const put = r => { g.style.left = r.left + "px"; g.style.top = r.top + "px"; g.style.width = r.width + "px"; g.style.height = r.height + "px"; };
  put(from); document.body.appendChild(g);
  let ended = false; const end = () => { if (ended) return; ended = true; g.style.opacity = "0"; setTimeout(() => g.remove(), 260); done(); };
  requestAnimationFrame(() => requestAnimationFrame(() => { put(to); }));
  g.addEventListener("transitionend", ev => { if (ev.propertyName === "width" || ev.propertyName === "height") end(); });
  setTimeout(end, 800);
}
function showView() {
  const inv = S.view === "inventory", P = proj();
  $("inventory").hidden = !inv; $("frame").hidden = inv;
  $("eyebrow").textContent = inv ? "ARKSTATION // UI LAB" : `${P.name.toUpperCase()} // UI LAB`;
  $("title").textContent = inv ? "Inventory" : P.name;
  $("lab").toggleAttribute("data-photons", !inv && live.state === "on" && hasRenders());
  if (!inv && live.state === "on" && hasRenders()) $("lab").dataset.edge = "agent"; else delete $("lab").dataset.edge;
  renderActions();
}
function setShell(id) { document.documentElement.dataset.shell = id; S.shell = id; renderActions(); if (S.view === "inventory") paintFolders(); }
function openProject(id) {
  const fp = document.querySelector(`.fp[data-project="${id}"]`), from = fp ? rectOf(fp.querySelector(".fp-stage")) : { width: 0 };
  S.project = id; S.view = "board"; S.pick = null; S.layer = null;
  setShell(proj().shell);
  if (!hasRenders() && S.side !== "thread") S.side = "thread";
  showView();
  if (narrow()) { $("frame").dataset.inspectorHidden = "true"; $("inspector").hidden = true; } else { $("frame").dataset.inspectorHidden = "false"; $("inspector").hidden = false; }
  setSide(S.side); renderStage(); renderInspect(); renderLayers(); renderThread();
  roll(from, rectOf($("stagehost")), () => stage?.el.focus({ preventScroll: true }));
}
function returnToInventory() {
  const from = rectOf($("stagehost")), id = S.project;
  S.view = "inventory"; showView(); renderInventory();
  const fp = document.querySelector(`.fp[data-project="${id}"]`);
  roll(from, fp ? rectOf(fp.querySelector(".fp-stage")) : { width: 0 }, () => fp?.querySelector(".fp-hit").focus({ preventScroll: true }));
}

/* ---------- wiring ---------- */
function renderActions() {
  const inv = S.view === "inventory", hidden = $("frame").dataset.inspectorHidden === "true";
  // Each Shell part on the page follows the picked Shell: Phosphor (ArkStation) or Bandit, Ryozan's Phosphor Lite.
  $("actions").innerHTML = switcher("Shell", [["phosphor", "Phosphor"], ["bandit", "Bandit"]], S.shell || "phosphor", "shell") +
    (inv ? "" : switcher("Lab view", [["board", "Board"], ["focus", "Focus", !hasRenders()]], S.view, "view") + (S.view === "focus" ? btn("Previous", 'data-step="-1"') + btn("Next", 'data-step="1"') : "") + btn(hidden ? "Open inspector" : "Hide inspector", `id="side-toggle" aria-expanded="${!hidden}" aria-controls="inspector"`));
}
function setInspector(show) { $("frame").dataset.inspectorHidden = String(!show); $("inspector").hidden = !show; renderActions(); requestAnimationFrame(() => stage?.set({ view: stage.view })); }
function setSide(side) {
  S.side = side;
  $("sideswitch").innerHTML = switcher("Inspector view", [["thread", "Thread"], ["inspect", "Inspect"], ["layers", "Layers"]], side, "side");
  for (const k of ["thread", "inspect", "layers"]) $("side-" + k).hidden = k !== side;
  if ($("inspector").hidden && !narrow()) setInspector(true);
}
function goFocus(k) { if (k !== S.focus) S.pick = null; S.focus = k; S.view = "focus"; save(); renderStage(); renderInspect(); renderLayers(); }
document.addEventListener("click", e => {
  const t = e.target.closest("button"); if (!t) return;
  const d = t.dataset;
  if (d.acc) { const open = !S.open[d.acc]; S.open[d.acc] = open; const sec = t.closest(".rp-accordion"), body = $(`acc-${d.acc}-body`); sec.dataset.open = String(open); t.setAttribute("aria-expanded", String(open)); t.querySelector(".rp-accordion__indicator").textContent = open ? "−" : "+"; body.setAttribute("aria-hidden", String(!open)); body.toggleAttribute("inert", !open); if (d.acc === "brief" && open) $("briefview").textContent = brief($("ask").value.trim() || "(your message)", !S.sessionId); return; }
  if (d.openProject) return openProject(d.openProject);
  if (d.shell) return setShell(d.shell);
  if (t.id === "hud-back") return returnToInventory();
  if (t.id === "ask-source") { $("ask").value = `Set up a UI render source for ${proj().name}: a job that writes one <page>.png and one <page>.json element map per page, in the shape Holodex's UI renders workflow writes, so the UI Lab can show its board.`; setSide("thread"); if ($("inspector").hidden) setInspector(true); $("ask").focus(); return; }
  if (d.view) { if (d.view === S.view) return; S.view = d.view; renderStage(); renderInspect(); renderLayers(); return; }
  if (d.side) return setSide(d.side);
  if (d.src) { S.source = d.src; renderStage(); return; }
  if (d.focus) { goFocus(d.focus); if (narrow()) setInspector(false); return; }
  if (d.layer) { const [kind, ...rest] = d.layer.split(":"), id = rest.join(":"); S.layer = S.layer?.kind === kind && S.layer.id === id ? null : { kind, id }; S.pick = null; stage?.set({ picked: null }); if (S.view === "board") renderStage(); renderLayers(); renderInspect(); if (S.layer) setSide("inspect"); return; }
  if (d.step) { const i = ALL.indexOf(S.focus); return goFocus(ALL[(i + Number(d.step) + ALL.length) % ALL.length]); }
  if (d.undo) { delete S.tweaks[d.undo]; queueLive(d.undo, null); save(); paint(); renderInspect(); renderLayers(); return; }
  if (d.elundo) { const x = S.elTweaks[d.elundo]; if (x) { delete x[d.p]; if (!Object.keys(x).length) delete S.elTweaks[d.elundo]; } save(); paint(); renderInspect(); return; }
  if (d.mission) { S.mission = S.mission === +d.mission ? null : +d.mission; return renderThreadMore(); }
  if (d.mcall) return missionCall(d.mcall);
  if (t.id === "side-toggle") return setInspector($("inspector").hidden);
  if (t.id === "side-close") return setInspector(false);
  if (t.id === "live-go") return liveConnect();
  if (t.id === "rerender") return loadFocus(true);
  if (t.id === "livereset") return liveReset();
  if (t.id === "clear") { for (const n of Object.keys(S.tweaks)) queueLive(n, null); S.tweaks = {}; S.elTweaks = {}; save(); paint(); renderInspect(); renderLayers(); return; }
  if (t.id === "w-reset" && spec) { for (const c of spec.controls) { if (c.id.startsWith("el-")) { const x = S.elTweaks[elKey()]; if (x) { delete x[c.id.slice(3)]; if (!Object.keys(x).length) delete S.elTweaks[elKey()]; } } else if (c.id in S.tweaks) { delete S.tweaks[c.id]; queueLive(c.id, null); } } save(); paint(); renderInspect(); renderLayers(); return; }
  if (t.id === "promote") { $("ask").value = "Promote these pending tweaks into Holodex: write each token value into the tokens it names, apply the element edits to the named controls, keep the token names, and show the before and after renders of every surface that changes."; setSide("thread"); $("ask").focus(); return; }
  if (t.id === "postnote") return postNote();
  if (t.id === "mission") return runMission();
  if (t.id === "t-refresh") return refreshThread();
  if (t.id === "t-new") { stopPoll(); S.sessionId = null; S.thread = null; save(); setFeedback("New thread. Your next message opens a fresh room with Smith.", ""); renderThread(); }
});
$("composer").addEventListener("submit", e => { e.preventDefault(); send(); });
document.addEventListener("input", e => {
  const t = e.target;
  if (t.id === "q") { S.q = t.value; if (S.q) for (const k of ["surfaces", "components", "effects", "tokens-color", "tokens-motion", "tokens-metric", "tokens-type"]) S.open[k] = true; return renderLayers(); }
  if (t.dataset.w && t.type !== "text") return widgetInput(t.dataset.w, t.value);
  if (t.dataset.mapprove) { const m = S.missions[+t.dataset.mapprove]; if (m) { m.approvalId = UUID.test(t.value.trim()) ? t.value.trim() : ""; save(); } }
  if (t.id === "ask" && S.open.brief && $("briefview")) $("briefview").textContent = brief(t.value.trim() || "(your message)", !S.sessionId);
});
document.addEventListener("change", e => {
  const t = e.target;
  if (t.dataset.w && t.type === "text") return widgetInput(t.dataset.w, t.value);
  if (t.id?.startsWith("flow-")) { store.set(t.id, t.checked); const sum = $("acc-brief-trigger")?.querySelector(".rp-accordion__summary"); if (sum) sum.textContent = `${FLOWS.filter(f => flowOn(f[0])).length} of ${FLOWS.length} flows`; if ($("briefview")) $("briefview").textContent = brief($("ask").value.trim() || "(your message)", !S.sessionId); }
});
document.addEventListener("keydown", e => {
  if (e.key === "Enter" && (e.ctrlKey || e.metaKey) && e.target.id === "ask") { e.preventDefault(); send(); }
  if (e.key === "Escape" && S.pick && !/INPUT|TEXTAREA|SELECT/.test(e.target.tagName)) setPick(null);
});
window.addEventListener("resize", () => { if (S.view !== "inventory") stage?.set({ view: stage.view }); });
if (narrow()) { $("frame").dataset.inspectorHidden = "true"; $("inspector").hidden = true; }
setShell("phosphor"); showView(); renderInventory(); renderThread();

(async () => {
  try { mcp = window.claude?.use ? await window.claude.use("mcp") : null; } catch { mcp = null; }
  if (!mcp) { hm = "none"; live.state = "off"; renderSource(); renderThread(); return; }
  let servers = [];
  try { servers = (await mcp.listTools()).servers || []; } catch {}
  const s = servers.find(x => x.server === HM);
  hm = s && s.authStatus !== "needs_reauth" ? "ok" : "missing";
  renderThread();
  // A host server is listed only inside the Claude app. When it is, try it once; the app asks the owner the first time.
  if (servers.some(x => x.server === HX)) await liveConnect(); else { live.state = "off"; renderSource(); }
})();
