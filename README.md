# Holodex

**NVIDIA Nemotron at the tip of your cursor, in any Windows app.**

Holodex is a native Windows desktop companion. It replaces the cursor with a wand and puts NVIDIA Nemotron, served by **Nebius Token Factory**, behind it: point at anything and ask, lasso a region of the screen, stack captures into a handoff, or let the Wiz agent do a multi-step job with tools. Every answer card names the model that produced it.

> **Nebius x NVIDIA Global AI Hackathon** · Track: **Personal AI** · License: **Apache-2.0** ([`LICENSE`](LICENSE))

Holodex is one of three related entries by the same author. Each runs on its own:

| Repo | What it is |
| --- | --- |
| **Holodex** (this repo) | Desktop companion: cursor, HUD, screen lasso, AInbox, Wiz agent. `.NET 10`, Win32. |
| [Hivemind](https://github.com/GalaAI/hivemind) | Multi-agent MCP server. Holodex can sign in to it (optional) to talk to Gala and other agents. |
| [Ryozan](https://github.com/GalaAI/ryozan) | Shop operating system for independent artists and studios. |

### Why I built this

> *"I needed help managing my business so I built my shop's app and website from scratch. I taught myself how to develop, build and ship full stack applications using AI. What I didn't expect was to fall in love with the process of architecting orchestrated software and now am founding a tech company called Ark Omnitek made by a tattooer, built for artists and creators. I also wanted to build something that would help other tattooers like my dad. But if it didn't help mine, what good would that do? Once my analytics proved it worked on my shop first, I knew I could confidently give him something that works and put it out there in the world."*
> **Justin Walker**

---

## Screenshots

| HUD (`Alt+Space`) | Holodex drawer (`Alt+H`) | Wiz agent (`Alt+W`) |
| --- | --- | --- |
| ![HUD](docs/shots/as-built/hud.png) | ![Holodex](docs/shots/as-built/holodex.png) | ![Wiz](docs/shots/as-built/wiz.png) |

| Lasso and element picker (`Alt+S` / `Alt+E`) | Guided tour (`Alt+T`) | AInbox (`Alt+I`) |
| --- | --- | --- |
| ![Snip](docs/shots/as-built/snip.png) | ![Tutorial](docs/shots/as-built/tutorial.png) | ![AInbox](docs/shots/as-built/ainbox.png) |

---

## How Holodex uses Nemotron and Token Factory

Every model call is an OpenAI-compatible `POST /v1/chat/completions` from the desktop app straight to `https://api.tokenfactory.nebius.com/v1` with an NVIDIA open model. There is no server to run and no GPU to manage; one Token Factory key covers every size, so Holodex picks the size per job.

| Surface | Model id on Token Factory | Why this size |
| --- | --- | --- |
| HUD text questions (`Alt+Space`), drawer chat while offline | `nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B` | Has to feel instant; 3B active parameters keep latency low. |
| AInbox ranking and Handoff Scroll synthesis (`Alt+I`) | `nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B` | Re-ranks on every capture you add, against a strict JSON contract. |
| Wiz agent (`Alt+W`) | `nvidia/nemotron-3-super-120b-a12b` | Multi-step tool calling, where reasoning quality matters more than speed. |
| Lasso, element picker, any HUD question with the screen attached | `nvidia/Nemotron-3.5-Super-VL` | The only Nemotron here that reads images. Text models reject `image_url`, so Holodex routes any request with an image to Super VL and says so on the card. |
| HUD model picker (manual choice) | `nvidia/Nemotron-3_5-Lightning`, `nvidia/Nemotron-3-Ultra-550b-a55b` | Offered for users who want to trade speed for depth. Not used by default. |

Where this lives in the code: [`ArkStationClient.cs`](ArkStationClient.cs) (`AskNebiusTokenFactoryAsync`, built-in model list), [`WizAgent.cs`](WizAgent.cs) (agent loop and tool calls), [`AInboxStack.cs`](AInboxStack.cs) (ranking contract), [`OmniwandHudForm.cs`](OmniwandHudForm.cs) (which model actually answered).

How Token Factory helped: the `/v1/models` listing let us check every model id the app ships against what the account can call, and the single OpenAI-compatible API made switching sizes a one-line change per surface. All five ids above were listed by `GET /v1/models` on 2026-10-29.

### Optional: Hivemind sign-in

Signed out, everything above runs on Nemotron directly. Signed in to a Hivemind server (OAuth 2.1 with PKCE against `https://hivemind.arkomnitek.com/mcp`), the HUD and the drawer can hand a question to Gala and other Hivemind agents, and work orders become Hivemind missions. Answers that came from Nemotron instead of Gala are labelled as such.

---

## Quickstart

You need Windows 10 or 11 (x64) and a Nebius Token Factory API key. Nothing else.

### 1. Get Holodex

**Release build (recommended).** Download `Holodex-v0.3.0-win-x64.zip` from [Releases](../../releases), check it against `Holodex-v0.3.0-win-x64.zip.sha256`, and extract it anywhere.

**From source.** Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then:

```powershell
dotnet build Omniwand.csproj -c Release -nologo
dotnet publish Omniwand.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish-win-x64 -nologo
```

### 2. Give it your Token Factory key

Holodex never writes the key to a file, a log, or a screenshot. Use one of:

```powershell
# Environment variable for your Windows user
[Environment]::SetEnvironmentVariable("NEBIUS_API_KEY", "<your-token-factory-key>", "User")

# Or Windows Credential Manager
cmdkey /generic:Omniwand:NebiusTokenFactory /user:nebius /pass:<your-token-factory-key>
```

You can also paste it in the drawer (`Alt+H`) profile panel under **Replace key**, which stores it in Windows Credential Manager.

### 3. Run

Launch `Omniwand.exe`. The emblem appears in the system tray, the wand cursor takes over, and a short guided tour starts. Press `Alt+Space` and ask something.

> The executable, credential names (`Omniwand:*`) and the `%LOCALAPPDATA%\Omniwand` profile folder keep the project's earlier name so existing installs and the Logitech plugin keep working.

---

## Features

- **HUD (`Alt+Space`).** Ask about whatever is under the cursor. The surrounding screen can go with the question, in which case Super VL answers.
- **Lasso and element picker (`Alt+S`, `Alt+E`).** Free-form or rectangle snip with pens, arrows and boxes, or pick a window element (class, title, bounds, a magnifier with click-to-copy color). Send it to Nemotron or save it to the Codex.
- **AInbox (`Alt+I`).** Drop, paste or pick any number of captures. They stack by window context; a keyword first pass is re-ranked by Nemotron Nano, which explains its choice. Text captures and text files are scanned and secrets are redacted before anything leaves; the resulting Handoff Scroll is sealed with SHA-256.
- **Wiz (`Alt+W`).** An agent on Nemotron Super that calls tools (capture, lasso, AInbox, Codex, work orders) and asks before it changes anything.
- **Holodex drawer (`Alt+H`).** Session, AInbox, Codex, GrimX and Mods tabs, plus the profile panel for keys and Hivemind sign-in.
- **Cursor and gestures.** A morphing cursor drawn per frame with layered windows, four cursor styles, and `Ctrl`-hold gestures (shake, circle, sweep) that open Wiz, the lasso, a spell slot or the drawer.
- **Mouse awareness.** Detects the connected mouse by vendor and model and keeps a per-device profile.

## Shortcuts

| Action | Shortcut |
| --- | --- |
| HUD | `Alt+Space` (or double-click the tray icon) |
| Holodex drawer | `Alt+H` or `Ctrl` + sweep right |
| Lasso | `Alt+S` or `Ctrl` + shake up and down |
| Element picker | `Alt+E` |
| Wiz agent | `Alt+W` |
| AInbox | `Alt+I` |
| Guided tour | `Alt+T` |
| Spell orb | `F15`, `Ctrl+Shift+Space`, or hold the middle button |
| Cycle cursor style | `Alt+C` |
| Show or hide the Windows cursor | `Ctrl+Alt+C` |
| Quit and restore the Windows cursor | `Ctrl+Alt+Q` |

## Known limitations

- Windows only.
- Images and recordings are not scanned for secrets; only text captures and text files are.
- Screen recording and voice capture are designed but not built; the UI shows them as planned.
- The AInbox first-pass ranking is keyword heuristics; Nemotron refines it when a key is set.

## Built during the submission period

The repository was published on 2026-10-20; every commit is inside the hackathon window (from 2026-08-26). Work in that window includes the Token Factory model routing above, the Wiz agent's tool calling on Nemotron Super, the AInbox stack with Nemotron ranking and sealed Handoff Scrolls, the secret redaction gate, Hivemind sign-in over MCP with OAuth, honest model labels on every card, and the guided tour.

## License

Apache License 2.0, see [`LICENSE`](LICENSE). Bundled fonts (Inter, Space Grotesk, JetBrains Mono, Archivo Black) are under the SIL Open Font License 1.1, see [`NOTICE`](NOTICE).
