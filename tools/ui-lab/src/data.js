const SIZES = __SIZES__;
const TOKENS = __TOKENS__;
const TUNABLE = ["dur-disclosure","dur-disclosure-item","dur-bloom","holodex-tip-dwell","holodex-tip-gap","holodex-tip-shift","holodex-tip-from-scale","holodex-select-radius-closed","holodex-select-radius-open","holodex-select-gap","holodex-select-row","holodex-select-chevron","holodex-select-stagger","holodex-turn-shift","holodex-reduced-motion","color-coral"];
const REPO = "GalaAI/holodex";
const HM = "Hivemind";

const SURF_GROUPS = [
  ["Drawer", k => k.startsWith("holodex")], ["HUD", k => k.startsWith("hud")], ["Wiz", k => k.startsWith("wiz")],
  ["AInbox", k => k.startsWith("ainbox")], ["Vault", k => k.startsWith("vault")], ["Lasso and picker", k => k.startsWith("snip")],
  ["Mouse Mod Station", k => k.startsWith("station") || k === "cursor-morph-sheet"], ["PC setup", k => k === "pc-setup"],
  ["Dialogs and tour", k => k.startsWith("dialog") || k === "tutorial"], ["Card kit", k => k.startsWith("kit-")],
  ["Toasts", k => k.startsWith("toast-")], ["Right-click menu", k => k.startsWith("menu") && !k.startsWith("menu-tray")], ["Tray menu", k => k.startsWith("menu-tray")],
];
const ALL = Object.keys(SIZES);
const surfaceGroup = k => (SURF_GROUPS.find(([, f]) => f(k)) || ["Other"])[0];
const has = (...pre) => k => pre.some(p => k.startsWith(p));

// Components and effects, each with the tokens it is built from and the renders that show it.
const COMPONENTS = [
  { id: "button", name: "Button", cls: "PhosphorButton", tokens: ["radius-btn","touch-min","text-size-btn","color-bone","color-line-2","border-hair"], on: has("holodex","hud","wiz","vault","dialog","tutorial","station","ainbox","kit-","pc-setup","snip") },
  { id: "card", name: "Card", cls: "ThreadView card", tokens: ["radius-tile","color-surface-2","color-line","s3","text-size-body","color-patina","color-coral"], on: has("holodex","kit-","wiz","vault-catalog","hud-","ainbox-review","station") },
  { id: "chip", name: "Model chip", cls: "ModelChip / SuggestionChip", tokens: ["radius-inset","text-size-data","color-dim","color-ember"], on: has("hud","wiz","snip") },
  { id: "field", name: "Composer field", cls: "PromptTextBox / PhosphorField", tokens: ["radius-tile","color-surface-2","color-line-2","text-size-body","color-ghost"], on: has("hud","wiz","vault","ainbox","snip","holodex-grimx") },
  { id: "rail", name: "Rail", cls: "RailButton / RailMark", tokens: ["color-surface","color-line","radius-btn","touch-min"], on: has("holodex","station") },
  { id: "led", name: "LED row", cls: "LedRow", tokens: ["led-size","dot-size","text-size-data","color-patina","color-ember","color-ghost"], on: has("holodex","hud","ainbox","station","snip","wiz") },
  { id: "toast", name: "Toast", cls: "PhosphorToast", tokens: ["radius-widget","color-surface","eyebrow-size","eyebrow-tracking","text-size-body"], on: has("toast-") },
  { id: "menu", name: "Tray menu", cls: "TrayPage", tokens: ["radius-menu-open","text-size-body","text-size-data","color-dim","color-patina"], on: has("menu") },
  { id: "select", name: "Fluid select", cls: "PhosphorFluidSelect", tokens: ["holodex-select-radius-closed","holodex-select-radius-open","holodex-select-gap","holodex-select-row","holodex-select-chevron","holodex-select-stagger","color-coral"], on: has("hud","wiz") },
  { id: "dialog", name: "Approval dialog", cls: "CommandApprovalForm / CursorModConfirmForm", tokens: ["radius-widget","color-surface","color-line-2","text-size-body"], on: has("dialog") },
  { id: "tip", name: "Tooltip", cls: "PhosphorTips", tokens: ["holodex-tip-dwell","holodex-tip-gap","holodex-tip-shift","holodex-tip-from-scale"], on: has("tutorial") },
  { id: "window", name: "Window frame", cls: "WidgetChrome", tokens: ["radius-widget","color-surface","color-line-2","eyebrow-size","eyebrow-tracking","s3"], on: () => true },
];
const EFFECTS = [
  { id: "aura", name: "Highlight Aura", note: "The coral glow that glides between rows and marks the active choice.", tokens: ["color-coral","holodex-select-stagger"], on: has("hud","wiz","station","holodex") },
  { id: "sidelight", name: "Sidelight edge", note: "The 3 px patina or coral edge on windows and cards that says live or create.", tokens: ["color-patina","color-coral","color-ember"], on: has("holodex","wiz","kit-","toast-","tutorial","snip") },
  { id: "bloom", name: "Bloom", note: "How a window opens (SurfaceMotion.Present).", tokens: ["dur-bloom"], on: () => true },
  { id: "disclosure", name: "Disclosure", note: "How rows enter: the beat, each item, and the stagger.", tokens: ["dur-disclosure","dur-disclosure-item","disclosure-stagger-step","disclosure-stagger-cap"], on: has("menu","hud","wiz") },
  { id: "cadence", name: "Cadence and pulse", note: "Waiting dots, arcs, caret and loader sweep.", tokens: ["dur-cadence-dots","dur-cadence-arc","dur-caret","dur-pulse","dur-loader-sweep"], on: has("hud-asking","kit-ai-components","holodex-grimx") },
  { id: "turn", name: "Tray page turn", note: "The slide between tray menu pages.", tokens: ["holodex-turn-shift","dur-step"], on: has("menu") },
  { id: "focus", name: "Focus ring", note: "Keyboard focus on every control.", tokens: ["focus-ring","focus-offset","color-patina"], on: () => true },
  { id: "motion", name: "Reduced motion", note: "Holodex's own reduced-motion switch.", tokens: ["holodex-reduced-motion"], on: () => true },
];
// Audit findings tied to renders (from docs/UI_AUDIT.md, run 42855962149).
const FINDINGS = {
  "holodex": [["D1","defect","Escape did not close the drawer. Fixed in #17."],["A1","ask","Devpost judges line in the first step."]],
  "holodex-mods": [["D2","defect","Profile panel covered the Mods list. Fixed in #18."]],
  "holodex-grimx": [["D3","defect","Profile panel covered the Spells composer. Fixed in #18."]],
  "holodex-codex": [["D4","defect","POINT AT UI (CTRL+ALT+E) filled its button. Fixed in #18."]],
  "tutorial": [["D5","defect","Cramped action button; “your  Mouse”. Fixed in #18."]],
  "hud": [["D6","defect","Hint cut to ENTER… Fixed in #18."]],
  "station-live": [["D7","defect","Page heading said INPUT STUDIO. Fixed in #19."],["A3","ask","The Cursor Mod page keeps Input Studio's look."]],
  "toast-crafted": [["D8","defect","Stale Input Studio copy. Fixed in #19."]],
  "toast-cursor-profile": [["D8","defect","Stale Input Studio copy. Fixed in #19."]],
  "menu-signed-in": [["A2","ask","Shows the raw railway.app host."]],
  "menu-models": [["A5","ask","Raw model ids in mixed case."]],
  "snip": [["P1","polish","Inspector card runs past its outline."]],
  "station": [["P2","polish","SPEED 10 OF 20 above its label."],["C3","gap","WebView pane is black offscreen."]],
  "kit-ai-approvals": [["P3","polish","First SKIP is wider."]],
  "wiz-agent": [["P4","polish","Scrolled list cuts a card with no fade."]],
  "pc-setup": [["C1","gap","Drawn before any scan."]],
};
const FLOWS = [
  ["edit", "Edit Holodex code", true], ["build", "Release build", true], ["tests", "Cursor Mod tests", true],
  ["renders", "UI renders before and after", true], ["audit", "Update docs/UI_AUDIT.md", false], ["graft", "Ship with SolaTree (solatree:graft)", true],
];

