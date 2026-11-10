# Holodex UI audit, 2026-11-10

Status: findings recorded; the clear defects are fixed in #17, #18 and #19 (open, drafts).

Scope: every surface Holodex draws. That is the drawer (each tab), the HUD, Wiz, AInbox, the Vault, the lasso and
picker, the Mouse Mod Station (offscreen and live), PC setup, the tour, the approval dialogs, the toasts, the cursor
morph sheet, and every page of the right-click and tray menus.

Evidence: the `ui-renders` artifact of the UI renders workflow (`.github/workflows/ui-renders.yml`), run
42855962149 on `ui-audit` at e1726b9. It holds 75 PNGs drawn by the app itself on a Windows runner through
`render:<folder>` (SurfaceCapture), plus `station-live.png`, a capture of the Station with its page loaded. The CI
machine is signed out with no key, so every surface shows its signed-out state.

How to read the ranks: **Defect** is broken layout, clipped text, stale copy or a dead control, fixed without asking.
**Ask** is a design or copy call that is Justin's. **Polish** is small and safe, left for a later pass. **Coverage**
is a gap in what the renders show, not in the product.

## Defects (fixed)

| # | Surface | What the render shows | Fix |
|---|---------|-----------------------|-----|
| D1 | Drawer, every tab | Escape does not close the drawer; every other window closes on Escape (from code: `ProcessCmdKey` had no Escape case). | #17 |
| D2 | Drawer, Mods tab | The profile panel, open by itself while signed out, floats over the list. Cards run under it and show again below it. | #18 |
| D3 | Drawer, Spells tab | The same panel covers the "Describe a Spell" composer, so it cannot be used until the panel is closed. | #18 |
| D4 | Drawer, Library tab | "POINT AT UI (CTRL+ALT+E)" fills its button edge to edge. | #18 |
| D5 | Tour, step 1 | "CYCLE CURSOR STYLE" fills its button edge to edge; the text reads "your  Mouse" with a double space. | #18 |
| D6 | HUD footer | The hint beside the model picker is cut to "ENTER..." (it has about 70 px). | #18 |
| D7 | Mouse Mod Station, live | The Cursor Mod page's heading reads "INPUT STUDIO". | #19 |
| D8 | Toasts | "Active Input Studio profile" and "Bind it to a ring slot in Input Studio" under the retired "WIZ // 4GE BENCH" eyebrow. The catalog had drifted from the real toasts. | #19 |
| D9 | Hotkey and Spell copy | Ctrl+Alt+P is labelled "Next Input Studio profile"; an unbound Spell says "Bind it in Input Studio". | #19 |

## Asks (Justin's call)

| # | Surface | Question |
|---|---------|----------|
| A1 | Drawer, Session first step | Card 1 says "Judges can use the demo account from the Devpost testing instructions". Keep it while the Devpost entry is live, or drop the hackathon line from the product? |
| A2 | Menus, Hivemind account | The signed-in page reads "Signed in to hivemind-public-production.up.railway.app", the raw host. Show "Signed in to Hivemind" (and the account) instead? |
| A3 | Mouse Mod Station, live | The Cursor Mod page keeps Input Studio's own look (an orange "Build & install", browser selects and scrollbar, its own header) beside the Phosphor rail. This follows the 2026-11-10 ruling (serve it as is, port later); listed so the port has the evidence. |
| A4 | Stats tab | "Share to ArkHub" uses a stock Windows MessageBox (`HolodexStatsPanel.cs:350`, from code; the tab is not in the renders). Replace with a Phosphor dialog? |
| A5 | Menus, Models | Model names show raw ids in mixed case ("NVIDIA-Nemotron-3-Nano-30B-A3B", "nemotron-3-super-120b-a12b", "Nemotron-3_5-Lightning"). Show display names? |

## Polish (not fixed)

| # | Surface | What the render shows |
|---|---------|-----------------------|
| P1 | Lasso and picker | The element inspector card runs about 3 px past the teal outline of the picked box on its right edge. |
| P2 | Mouse Mod Station rail | "SPEED 10 OF 20" sits above its own section label "MOUSE SPEED // SLOW TO FAST" instead of under it. |
| P3 | Kit, approval cards | The first SKIP is wider than the other two. |
| P4 | Wiz, AInbox, Vault catalog | Scrolled lists cut the last or first card mid-card with no fade at the edge. |
| P5 | Drawer, Library tab | The button row ends about 5 px right of the card column under it. |
| P6 | Code only | Literal colours outside the tokens: `AInboxIntakeWidgetForm.cs:1028` (22, 27, 34) and `ScrollBeam.cs:27` (228, 217, 192). Neither is visible in a render; check against the tokens when those files are next touched. |

## Coverage gaps in the renders

| # | Render | Gap |
|---|--------|-----|
| C1 | `pc-setup.png` | Drawn before any scan, so it shows only the header. The real window always scans on open (`PcSetupScan.ShowAsync`). Render it after a scan, or with the SCANNING card. |
| C2 | `holodex-first-steps.png` | The same image as `holodex.png`. |
| C3 | `station.png` | The page pane is black: the WebView does not draw offscreen. `station-live.png` covers it. |
| C4 | Stats tab | Not rendered. |
| C5 | Signed-in states | The runner is signed out, so no signed-in or keyed state is drawn. |

## What held up

The menus and tray menus (all 28 pages, right-click and tray) are consistent: one header style, ESC on every sub-page, shortcuts
right-aligned in the data face, checks in one column. The toasts share one frame and eyebrow style. The approval
dialogs keep Cancel or Don't run as the quiet button and one bone primary, and the command is shown in the mono face
before it runs.
