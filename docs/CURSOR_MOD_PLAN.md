# Holodex Cursor Mod: moving the Input Spine inside Holodex

Status: plan, 2026-11-08. Nothing is built yet. Ruling (Justin, 2026-11-08, index
session question card): Input Studio is now the Holodex Cursor Mod, and its
input endpoint lives inside Holodex.

## What runs today

Evidence: `GalaAI/ark` `origin/main`, `GalaAI/holodex` `master` at `603360f`.

| Piece | Where | What it does |
| --- | --- | --- |
| Input Studio service | `ark/apps/input-studio/server.ts` (954 lines), Bun, port 8842 | Serves the page and the routes below |
| Input Studio page | `ark/apps/input-studio/public/index.html` (1782 lines) | Cursor designer, button and gesture bindings |
| Spine logic | `ark/packages/input-spine-core` (`index.ts`, `resolve.ts`, `run.ts`) | Trigger to action resolution and the runner. Real input goes to PC Mod `:8830/operations/<op>` |
| GrimX flows | `ark/apps/input-studio/grimx-flows.ts` | Binds GrimX Spells to spine slots |
| AHK bridge | `ark/apps/input-studio/bridge/ark-input-bridge.ahk`, `keymap.ini` | Mouse buttons to `POST /api/spine/event` |
| Holodex embed | `holodex/MouseModStationForm.cs` | WebView2 page at `http://127.0.0.1:8842/` |

Routes on 8842:

- Spine: `GET /api/spine/config`, `POST /api/spine/save`, `POST /api/spine/grimx/bind`, `GET /api/spine/grimx/spells`, `POST /api/spine/run`, `POST /api/spine/event`, `GET /api/spine/recent`
- Cursor: `POST /api/cursor/build`, `/install`, `/restore`, `/generate`, `/profile/save`, `/profile/load`, `/profile/delete`, `GET /api/cursor/profiles`

Callers:

| Caller | Calls |
| --- | --- |
| Holodex Spells (`ArkStationClient.cs` 703, 772) | `GET /api/spine/grimx/spells`, `POST /api/spine/event` |
| Holodex HUD and PC scan (`ArkStationClient.cs` 540, `PcSetupScan.cs` 97) | `GET /api/spine/config` as a health probe |
| Holodex Wiz (`ArkStationClient.cs` 855) | `POST /api/omniwand/wiz/chat`, which does not exist on main (dead call) |
| Stream Deck (`ark/apps/streamdeck-arkstation/src/summon.ts`) | `POST /api/spine/event`, `GET /api/spine/config` |
| AHK bridge | `POST /api/spine/event` |
| Ark launcher (`ark/scripts/start-ark-local.ps1` 2900) | Starts and health-checks `input-studio` |
| Eden registry (`ark/packages/eden-config/src/components.ts` 238) | `ark://input-studio` on 8842 |

Known defect: the runner's PC Mod call (`server.ts` 450 to 460) posts to
`:8830/operations/<op>`, which does not exist, so live input never reaches PC Mod
(PC Mod audit #1628, row 16). The uncovered-apps audit says not to fix that
route in Input Studio; the move fixes it instead.

## Target

Holodex owns port 8842 and serves the same routes. Callers keep their URLs, so
the Stream Deck, the AHK bridge and Spells change nothing on day one.

- **Endpoint:** a loopback-only `HttpListener` in Holodex on `127.0.0.1:8842`, with the same Origin check Input Studio uses.
- **Cursor writing:** `.cur` and `.ani` files and the Windows cursor scheme move into C#. Holodex already restores cursors on crash (`CursorCrashGuard.cs`), so install and restore share one owner.
- **Real input:** Holodex already drives input natively (`MouseHardwareAndWandEngine.cs`). The runner calls that directly instead of PC Mod's missing route.
- **Page:** the Mouse Mod window becomes the Cursor Mod tab, built from the Holodex Phosphor kit rather than an embedded web page.
- **Spine config:** keeps its file format and contract version (`INPUT_SPINE_CONTRACT_VERSION` 0.1.0), so saved bindings carry over.

## Steps (one draft PR each)

1. **Holodex: spine endpoint.** Port `resolve.ts` and `run.ts` to C# with tests that replay `spine.test.ts` cases. Serve the 7 spine routes on 8842 only when nothing else holds the port, so it runs side by side with Input Studio while testing.
2. **Holodex: cursor routes.** Port the 8 cursor routes and the `.cur`/`.ani` writer. Test the writer byte for byte against Input Studio's output for the same input.
3. **Holodex: Cursor Mod tab.** Replace the WebView2 embed in `MouseModStationForm.cs` with native panels. Drop the dead `/api/omniwand/wiz/chat` call.
4. **Ark: point callers at Holodex.** Remove `input-studio` from `start-ark-local.ps1`, point the `ark://input-studio` registry entry at the Holodex Cursor Mod, and move the AHK bridge into the holodex repo. The Stream Deck keeps its URL.
5. **Ark: remove `apps/input-studio`.** Done by the UI sweep session once steps 1 to 4 are merged and proven on Justin's PC.

`packages/input-spine-core` stays in Ark until step 5. If no other app imports
it then, it goes too.

## Open questions

1. Port the spine to C# (one runtime, as planned above), or have Holodex start a bundled Bun process for the existing TypeScript? Recommended: C#.
2. Should Holodex hold port 8842 when Ark is not installed (a public user with a Stream Deck)? Recommended: yes, loopback only.
3. The spine can run shell commands (`run.ts`). Keep that in the public build, or limit it to Ark owner PCs? Recommended: owner PCs only.
