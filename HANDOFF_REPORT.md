# Handoff Report: Omniwand Open Source Release (`omniwand-oss`) — Gate A

- **Working Tree:** `E:\ArkStation\augments\omniwand-oss`
- **Original Dev Folder (`E:\ArkStation\augments\omniwand`):** Untouched (zero modifications; dev `Omniwand.exe` restored and running from `E:\ArkStation\augments\omniwand\dist\Omniwand.exe`).
- **Source Handoff Spec:** `E:\ArkStation\.ark\agents\gemini\handoffs\omniwand-opensource-handoff.md`
- **Gate Status:** **Stopped at Gate A** (local git repo initialized, built, self-contained `win-x64` published, live Nebius Token Factory + NVIDIA Nemotron calls verified `200 OK`, standalone surfaces captured, secret scan passed; **no public GitHub repo created, pushed, or released**).

---

## 0. Comparison with Earlier Guidance (Before Handoff)

Before receiving `omniwand-opensource-handoff.md`, our earlier packaging walkthrough proposed:
1. Adding a cursor sensitivity multiplier submenu (`Relaxed 0.75x`, `Balanced 1.0x`, `Responsive 1.35x`, `Raw 1.75x`).
2. Adding a `%APPDATA%\Omniwand\settings.json` persistence layer.
3. Stripping ArkStation widgets (`Wiz`, `AInbox`, `GrimXFlowOrb`) out of the open-source edition into a cursor-only shell.
4. Adding `arkomnitek.com` links directly to the tray menu.

**How `omniwand-opensource-handoff.md` superseded that plan:**
- **Scope Rule:** Section 2 (`### 2. Standalone mode (the only real code change)`) specifies that **Standalone mode is the only real code change**—preserving the full Omniwand experience (Phosphor HUD, Wiz Crafting, AInbox Intake, 8-slot GrimX Flow Orb, GhostTrailer ribbon, and existing tray menu) rather than stripping widgets or adding new cursor physics/settings menus.
- **Provider Routing:** Keeps the Ark path (`:8788`) first when reachable, and adds a direct **Nebius Token Factory** (`https://api.tokenfactory.nebius.com/v1`) OpenAI-compatible provider defaulting to live **NVIDIA Nemotron** models (`nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B` fast tier default, `nvidia/Nemotron-3_5-Lightning`, `nvidia/nemotron-3-super-120b-a12b`, `nvidia/Nemotron-3.5-Super-VL`, `nvidia/Nemotron-3-Ultra-550b-a55b`) when the gateway is unreachable.
- **Key Handling:** Reads the Token Factory API key strictly from environment variables (`NEBIUS_API_KEY`, `TOKEN_FACTORY_API_KEY`, `OMNIWAND_NEBIUS_API_KEY`) or Windows Credential Manager (`CredReadW` for `Omniwand:NebiusTokenFactory` / `NEBIUS_API_KEY`), never storing keys in config files, code, logs, or screenshots, and raising a clear `PhosphorToast` when missing without hanging.
- **Graceful Degradation:** Input Studio (`:8842`) and PC-Modz (`:8830`) degrade gracefully with a `PhosphorToast` when absent.

---

## 1. Step 1 — Clean Repository Preparation (`E:\ArkStation\augments\omniwand-oss`)

1. **Isolated Copy:** Copied source files from `E:\ArkStation\augments\omniwand` into `E:\ArkStation\augments\omniwand-oss`, excluding `bin/`, `obj/`, `dist/`, and `logi-mx4-plugin/`.
2. **Logi Actions SDK Check (`logi-mx4-plugin`):** Inspected `logi-mx4-plugin/ArkStationOmniwandPlugin.csproj`, which references proprietary local Loupedeck/Logi assemblies (`C:\Program Files\Logi\LogiPluginService\PluginApi.dll`). Per Step 1 instructions (*"If redistribution is unclear, exclude it from the public repo and note it as optional"*), excluded `logi-mx4-plugin/` from `omniwand-oss` and documented MX Master 4 support as optional in `README.md`.
3. **Self-Contained Font Packaging (`Omniwand.csproj`):** Copied `JetBrainsMono-Regular.ttf`, `JetBrainsMono-OFL.txt`, `ArchivoBlack-Regular.ttf`, and `OFL-ArchivoBlack.txt` from `E:\ArkStation\.ark\packages\tokens\assets\fonts` into `E:\ArkStation\augments\omniwand-oss\assets\fonts\` and updated `Omniwand.csproj` to use relative paths (`assets\fonts\...`) so the project builds cleanly on any machine without `E:\ArkStation\.ark`.
4. **Git & Licensing:**
   - Initialized local Git repository (`git init` in `E:\ArkStation\augments\omniwand-oss`).
   - Added `.gitignore` (`bin/`, `obj/`, `dist/`, `publish/`, `*.user`, `.vs/`, `*.log`, `.env*`).
   - Added `LICENSE` (**Apache License 2.0**, ready for Justin's confirmation) and `NOTICE` attributing all four bundled SIL OFL 1.1 font families (`Inter`, `Space Grotesk`, `JetBrains Mono`, `Archivo Black`).
   - Added public `README.md` covering Omniwand overview, setup, `NEBIUS_API_KEY` & Windows Credential Manager configuration, how NVIDIA Nemotron & Nebius Token Factory are used, NVIDIA Broadcast microphone tip for GhostTrailer dictation, optional ArkStation & MX Master 4 integration, post-Aug 26 2026 update summary, and as-built screenshots.

---

## 2. Step 2 — Standalone Mode Implementation

### Files Modified in `E:\ArkStation\augments\omniwand-oss`
- **`Omniwand.csproj`**: Replaced absolute `E:\ArkStation\.ark\packages\tokens\assets\fonts\...` paths with repo-relative `assets\fonts\...` paths.
- **`ArkStationClient.cs`**:
  - Preserved Ark Model Gateway (`:8788`) priority when reachable (`GatewayOnline`).
  - Added `DefaultNebiusBaseUrl = "https://api.tokenfactory.nebius.com/v1"` (overridable via `NEBIUS_BASE_URL` or `TOKEN_FACTORY_BASE_URL`).
  - Confirmed live NVIDIA Nemotron model IDs from `GET https://api.tokenfactory.nebius.com/v1/models` and populated `BuiltInNemotronModels` with `DefaultNemotronFastModel = "nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B"` (fast tier default for cursor HUD & GhostTrailer calls), `"nvidia/Nemotron-3_5-Lightning"`, `DefaultNemotronSuperModel = "nvidia/nemotron-3-super-120b-a12b"`, `"nvidia/Nemotron-3.5-Super-VL"` (vision-language), and `"nvidia/Nemotron-3-Ultra-550b-a55b"`.
  - Added `ReadNebiusApiKey()` checking environment variables (`NEBIUS_API_KEY`, `TOKEN_FACTORY_API_KEY`, `OMNIWAND_NEBIUS_API_KEY`) and Windows Credential Manager via Win32 `CredReadW` (`advapi32.dll`) for generic credentials (`Omniwand:NebiusTokenFactory`, `NebiusTokenFactory`, `NEBIUS_API_KEY`, `TOKEN_FACTORY_API_KEY`).
  - Added `AskNebiusTokenFactoryAsync()` with bounded 2-attempt retry (`TimeSpan.FromSeconds(20)` per attempt, immediate stop on HTTP 401/403), raising a `PhosphorToast` when the key is missing or rejected and never hanging.
  - Updated `GetAvailableModelsAsync()` so `ModelPicker` displays NVIDIA Nemotron models in standalone mode and dynamically merges any additional Nemotron IDs returned by `GET /v1/models` on Nebius Token Factory when a key is present.
  - Updated `TriggerGrimXFlowAsync()` and `SubmitPodInboxIntakeAsync()` so Input Studio (`:8842`) and PC-Modz (`:8830`) degrade gracefully with clear Phosphor toasts when absent.
- **`OmniwandHudForm.cs` & `WizCraftingWidgetForm.cs`**:
  - Updated status badges and runtime lines to display `STANDALONE // NEBIUS TOKEN FACTORY // NEMOTRON` and `HOST // NEBIUS TOKEN FACTORY // MODEL // NVIDIA/NVIDIA-NEMOTRON-3-NANO-30B-A3B` with the `NEMOTRON` badge when `:8788` is unreachable.
- **`Program.cs`**:
  - Added `wiz:ask:<prompt>` and `hud:ask:<prompt>` control-pipe commands alongside `render:<folder>` for deterministic verification.

---

## 3. Step 3 — Build, Verification, SurfaceCapture, and Secret Scan

### 3.1 Exact Commands Run

```powershell
Set-Location "E:\ArkStation\augments\omniwand-oss"
dotnet build Omniwand.csproj -c Debug --disable-build-servers -p:UseSharedCompilation=false -nologo
dotnet publish Omniwand.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64 --disable-build-servers -p:UseSharedCompilation=false -nologo
```

### 3.2 Build & Publish Output

```text
  Determining projects to restore...
  Restored E:\ArkStation\augments\omniwand-oss\Omniwand.csproj (in 165 ms).
  Omniwand -> E:\ArkStation\augments\omniwand-oss\bin\Debug\net10.0-windows\Omniwand.dll

Build succeeded.
    4 Warning(s)
    0 Error(s)
Time Elapsed 00:00:07.73

  Determining projects to restore...
  Restored E:\ArkStation\augments\omniwand-oss\Omniwand.csproj (in 635 ms).
  Omniwand -> E:\ArkStation\augments\omniwand-oss\bin\Release\net10.0-windows\win-x64\Omniwand.dll
  Omniwand -> E:\ArkStation\augments\omniwand-oss\publish\win-x64\
```

### 3.3 Standalone Runtime, `SurfaceCapture`, & Live Nemotron Call Result

Executed `E:\ArkStation\augments\omniwand-oss\publish\win-x64\Omniwand.exe` in standalone mode (`OMNIWAND_STANDALONE=1`) with the Token Factory key stored in Windows Credential Manager (`Omniwand:NebiusTokenFactory`):

1. **Orb Summon & Slot Cast Degradation:**
   - `orb:toggle` -> `ok:orb:toggle -> ok:orb:toggle`
   - `slot:spell` -> `ok:slot:spell:CAST` (cleanly showed warning toast: *"Standalone mode: Input Studio (:8842) is optional and offline. Use Wiz (Alt+W) or HUD (Alt+Space) to craft spell with Nemotron."*)
2. **`SurfaceCapture` (`render:E:\ArkStation\augments\omniwand-oss\docs\shots\as-built`):**
   - `SURFACE_CAPTURE=ok:render:10:menu.png,menu-orb-lit.png,menu-orb-submenu.png,toast-info.png,toast-success.png,toast-warning.png,toast-error.png,hud.png,wiz.png,ainbox.png`
   - Verified in rendered PNGs (`hud.png` and `wiz.png`) that both HUD and Wiz show the `NEMOTRON` badge, default to `NVIDIA/NVIDIA-NEMOTRON-3-NANO-30B-A3B`, and display `STANDALONE // NEBIUS TOKEN FACTORY // NEMOTRON // UNIVERSAL MOUSE` and `HOST // NEBIUS TOKEN FACTORY // MODEL // NVIDIA/NVIDIA-NEMOTRON-3-NANO-30B-A3B`.
3. **Live Authenticated Nebius Token Factory + NVIDIA Nemotron Completions (`200 OK`):**
   - **Wiz Call (`wiz:ask`):**
     ```text
     WIZ_LIVE_NEMOTRON_RESULT=ok:wiz:ask:ok=True:runtime=nebius-token-factory:model=nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B:output=Spell confirmed: Omniwand is connected to NVIDIA Nemotron on Nebius Token Factory.
     ```
   - **HUD Call (`hud:ask`):**
     ```text
     HUD_LIVE_NEMOTRON_RESULT=ok:hud:ask:ok=True:output=Yes, the Omniwand HUD query is running on NVIDIA Nemotron.
     ```

### 3.4 Secret Scan Result

Scanned all 37 tracked source/text files in `E:\ArkStation\augments\omniwand-oss` and all config/binary metadata files in `E:\ArkStation\augments\omniwand-oss\publish\win-x64`:

```text
REPO_TEXT_FILES_SCANNED=37
REPO_SECRET_HITS=0
PUBLISH_FILES_SCANNED=10
PUBLISH_SECRET_HITS=0
```

---

## 4. Repository File List (`E:\ArkStation\augments\omniwand-oss`)

```text
.gitignore
AInboxIntakeWidgetForm.cs
ArkEmblem.cs
ArkEmblemGeometry.cs
ArkStationClient.cs
CLAUDE.md
GhostTrailerEngine.cs
GrimXFlowOrb.cs
HANDOFF_REPORT.md
INVENTORY.json
LICENSE
ModelPicker.cs
NOTICE
Omniwand.csproj
OmniwandHudForm.cs
OverlayZGuard.cs
PhosphorKit.cs
PhosphorMenuRenderer.cs
PhosphorTheme.cs
PhosphorToast.cs
PhosphorTokens.cs
Program.cs
README.md
RotationPreview.cs
SurfaceCapture.cs
VrStreamDetector.cs
WidgetDrag.cs
WizCraftingWidgetForm.cs
assets/omniwand.ico
assets/fonts/ArchivoBlack-Regular.ttf
assets/fonts/Inter-ExtraBold.ttf
assets/fonts/Inter-Regular.ttf
assets/fonts/Inter-SemiBold.ttf
assets/fonts/JetBrainsMono-OFL.txt
assets/fonts/JetBrainsMono-Regular.ttf
assets/fonts/OFL-ArchivoBlack.txt
assets/fonts/OFL-Inter.txt
assets/fonts/OFL-SpaceGrotesk.txt
assets/fonts/SpaceGrotesk-Medium.ttf
assets/fonts/SpaceGrotesk-Regular.ttf
atlas/omniwand.blueprint.bamf.json
atlas/omniwand.md
docs/shots/ring-chips-2026-10-17.png
docs/shots/ring-icons-2026-10-16.png
docs/shots/rotation-previews-2026-10-17.png
docs/shots/toasts-2026-10-16.png
docs/shots/tray-menu-2026-10-17.png
docs/shots/as-built/ainbox.png
docs/shots/as-built/hud.png
docs/shots/as-built/menu-orb-lit.png
docs/shots/as-built/menu-orb-submenu.png
docs/shots/as-built/menu.png
docs/shots/as-built/toast-error.png
docs/shots/as-built/toast-info.png
docs/shots/as-built/toast-success.png
docs/shots/as-built/toast-warning.png
docs/shots/as-built/wiz.png
tools/gen_app_icon.py
tools/gen_emblem_geometry.py
tools/gen_phosphor_tokens.py
```

---

## 5. Next Gate (Gate B — Publish)

All Gate A requirements are complete and verified. Upon your explicit **yes**:
1. Confirm **Apache 2.0** (`LICENSE`) and the target GitHub account/repository name (e.g., `GalaAI/omniwand` or your preferred account/slug).
2. Create the public GitHub repository, push `omniwand-oss`, set the About description and license, and publish the self-contained `win-x64` release zip + SHA256 checksum on GitHub Releases.
