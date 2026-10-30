# Holodex

**NVIDIA Nemotron at the tip of your cursor, in any Windows app.**

Holodex is a native Windows desktop companion. It replaces the cursor with a wand and puts NVIDIA Nemotron, served by **Nebius Token Factory**, behind it: point at anything and ask, lasso a region of the screen, stack captures into a handoff, or let the Wiz agent do a multi-step job with tools. Every answer card names the model that produced it.

> **Nebius x NVIDIA Global AI Hackathon** · Track: **Personal AI** · License: **Apache-2.0** ([`LICENSE`](LICENSE))

Holodex is one of three related entries by the same author. Each runs on its own:

| Repo | What it is |
| --- | --- |
| **Holodex** (this repo) | Desktop companion: cursor, HUD, screen lasso, AInbox, Wiz agent. `.NET 10`, Win32. |
| [Hivemind](https://github.com/GalaAI/hivemind) | Multi-agent MCP server. Signing in to it is how a fresh Holodex gets Nemotron answers with no key. |
| [Ryozan](https://github.com/GalaAI/ryozan) | Shop operating system for independent artists and studios. |

### Why I built this

> *"I needed help managing my business so I built my shop's app and website from scratch. I taught myself how to develop, build and ship full stack applications using AI. What I didn't expect was to fall in love with the process of architecting orchestrated software and now am founding a tech company called Ark Omnitek made by a tattooer, built for artists and creators. I also wanted to build something that would help other tattooers like my dad. But if it didn't help mine, what good would that do? Once my analytics proved it worked on my shop first, I knew I could confidently give him something that works and put it out there in the world."*
> **Justin Walker**

---

## Screenshots

| HUD (`Ctrl+Alt+Space`) | Holodex drawer (`Ctrl+Alt+H`) | Wiz agent (`Ctrl+Alt+W`) |
| --- | --- | --- |
| ![HUD](docs/shots/as-built/hud.png) | ![Holodex](docs/shots/as-built/holodex.png) | ![Wiz](docs/shots/as-built/wiz.png) |

| Lasso and element picker (`Ctrl+Alt+S` / `Ctrl+Alt+E`) | Guided tour (`Ctrl+Alt+T`) | AInbox (`Ctrl+Alt+I`) |
| --- | --- | --- |
| ![Snip](docs/shots/as-built/snip.png) | ![Tutorial](docs/shots/as-built/tutorial.png) | ![AInbox](docs/shots/as-built/ainbox.png) |

---

## How Holodex uses Nemotron and Token Factory

Every model call is an OpenAI-compatible chat completion with an NVIDIA open model on Token Factory. With no key at all, Holodex asks through your Hivemind sign-in (`model_invoke`); with your own Token Factory key it calls `https://api.tokenfactory.nebius.com/v1` directly, and gets tools (web search, files, the approved PC command line, bridged MCP servers). There is no server to run and no GPU to manage, so Holodex picks the size per job.

| Surface | Model id on Token Factory | Why this size |
| --- | --- | --- |
| HUD text questions (`Ctrl+Alt+Space`), drawer chat while offline | `nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B` | Has to feel instant; 3B active parameters keep latency low. |
| AInbox ranking and Handoff Scroll synthesis (`Ctrl+Alt+I`) | `nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B` | Re-ranks on every capture you add, against a strict JSON contract. |
| Wiz agent (`Ctrl+Alt+W`) | `nvidia/nemotron-3-super-120b-a12b` | Multi-step tool calling, where reasoning quality matters more than speed. |
| Lasso, element picker, any HUD question with the screen attached | `nvidia/Nemotron-3.5-Super-VL` | The only Nemotron here that reads images. Text models reject `image_url`, so Holodex routes any request with an image to Super VL and says so on the card. |
| HUD model picker (manual choice) | `nvidia/Nemotron-3_5-Lightning`, `nvidia/Nemotron-3-Ultra-550b-a55b` | Offered for users who want to trade speed for depth. Not used by default. |

Where this lives in the code: [`ArkStationClient.cs`](ArkStationClient.cs) (`AskNebiusTokenFactoryAsync`, built-in model list), [`WizAgent.cs`](WizAgent.cs) (agent loop and tool calls), [`AInboxStack.cs`](AInboxStack.cs) (ranking contract), [`OmniwandHudForm.cs`](OmniwandHudForm.cs) (which model actually answered).

How Token Factory helped: the `/v1/models` listing let us check every model id the app ships against what the account can call, and the single OpenAI-compatible API made switching sizes a one-line change per surface. All five ids above were listed by `GET /v1/models` on 2026-10-29.

### Answer routes, in order

1. **Hivemind sign-in (no key).** Sign in from the Holodex drawer (OAuth 2.1 with PKCE against `https://hivemind.arkomnitek.com/mcp`). Nemotron answers through it as text; web search runs as a one-line `SEARCH:` request. Judges can use the demo account from the Devpost testing instructions, or create their own account with an invite code.
2. **Your own Token Factory key (optional).** Paste it into the Vault and Holodex calls Token Factory directly, with images (Super VL) and tools.
3. **A model on this PC (optional).** Chat models that LM Studio or Ollama serve show up in the model picker as local models and answer from your own machine.

Signed in, the HUD and the drawer can also hand a question to Gala and other Hivemind agents, and work orders become Hivemind missions. Every answer card names the model and route that answered.

---

## Quickstart

You need Windows 10 or 11 (x64). A Hivemind sign-in is enough for answers; a Nebius Token Factory key is optional.

### 1. Get Holodex

**Release build (recommended).** Download `Holodex-v0.4.1-win-x64.zip` from [Releases](../../releases), check it against `Holodex-v0.4.1-win-x64.zip.sha256`, and extract it anywhere.

**From source.** Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then:

```powershell
dotnet build Omniwand.csproj -c Release -nologo
dotnet publish Omniwand.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish-win-x64 -nologo
```

### 2. Sign in, or give it your Token Factory key

Launch Holodex. On a PC with no key, no Ark and no sign-in, the Holodex drawer opens on its first steps: **Sign in with Hivemind** is the one step answers need.

To use your own key instead (or as well): Holodex ships with no keys, and every key stays on your PC in Windows Credential Manager (`wincred://Omniwand:*`), never in source, config files, logs or screenshots.

1. Get a key at [tokenfactory.nebius.com](https://tokenfactory.nebius.com/).
2. Open the **Vault** from the tray menu.
3. Paste the key into the intake and press **Add to vault**. Holodex checks it against Token Factory and marks it **Works**.

Prefer environment variables? Holodex reads them first, and the Vault lists them under **From your environment**:

```powershell
[Environment]::SetEnvironmentVariable("NEBIUS_API_KEY", "your-nebius-token-factory-key", "User")
```

### 2b. Turn on web search (optional)

Add a [Tavily](https://app.tavily.com/home) key the same way (paste it into the Vault, or set `TAVILY_API_KEY`). Nemotron then gets a `web_search` tool: it decides when to search the live web, and the answer lists the pages it used. Without a Tavily key Holodex answers from the model alone.

### Your Vault: apps, services and keys

The **Vault** (Inventory // Adapters) connects the rest of your tools, with or without a Hivemind account:

| Paste or pick | What happens |
| --- | --- |
| An API key or an `ENV_NAME=value` line | Recognized by its prefix or name (OpenAI, Anthropic, Gemini, OpenRouter, GitHub, Stripe and more), or by asking each provider's read-only endpoint, then checked and stored in Credential Manager. Copy (clipboard clears after 30 s), Rotate in place, Check and Delete on every key. |
| An MCP server link, or **Sign in** on a catalog card | The server's own OAuth sign-in (protected resource metadata, dynamic client registration, PKCE) in your browser: Linear, Notion, Figma, Canva, Vercel, Railway, Supabase, Cloudflare, Sentry, Stripe, PayPal, Webflow, Wix. Hugging Face needs no sign-in. |
| **Connect** on a local service | Ollama, LM Studio or ComfyUI running on this PC. |
| **Files** | A folder you pick (default `Documents\Holodex`). Nemotron gets `list_files`, `read_file` and `write_file` there; paths outside it are refused. |
| **PC command line** | Nemotron gets `run_command` (PowerShell) to configure PC Mod and other mods. Every command opens an approval window with the exact command; nothing runs until you choose **Run**. |
| **Google Drive** | Bring your own Google OAuth client: create a *Desktop app* client in [Google Cloud Credentials](https://console.cloud.google.com/apis/credentials), paste its downloaded JSON into the intake, then **Sign in**. Read-only Drive access; Nemotron gets `drive_search` and `drive_read`. Holodex ships no Google client or secret. |

### AInbox destinations

On a PC without an Ark, AInbox sends a Handoff Scroll to a **destination** instead of a pod. **Holodex chat** (NVIDIA Nemotron on Token Factory) is the default and always there; every chat agent you connect in the Vault joins the list (ChatGPT, Claude, Gemini, OpenRouter, Grok, Groq, DeepSeek, Mistral, Together, Fireworks, Cerebras, LM Studio, Ollama), plus **Hivemind** when you are signed in and **Save on this PC**. Pick one with the **To ...** button next to **Send**: the Scroll lands in the Holodex Session chat and the destination's answer appears under it. A sealed copy is kept in `Documents\Holodex\Scrolls` either way.

### 3. Run

Launch `Omniwand.exe`. The emblem appears in the system tray, the wand cursor takes over, and a short guided tour starts. Press `Ctrl+Alt+Space` and ask something.

> The executable, credential names (`Omniwand:*`) and the `%LOCALAPPDATA%\Omniwand` profile folder keep the project's earlier name so existing installs and the Logitech plugin keep working.

---

## Features

- **HUD (`Ctrl+Alt+Space`).** Ask about whatever is under the cursor. The surrounding screen can go with the question, in which case Super VL answers.
- **Lasso and element picker (`Ctrl+Alt+S`, `Ctrl+Alt+E`).** Free-form or rectangle snip with pens, arrows and boxes, or pick a window element (class, title, bounds, a magnifier with click-to-copy color). Send it to Nemotron or save it to the Codex.
- **AInbox (`Ctrl+Alt+I`).** Drop, paste or pick any number of captures. They stack by window context; a keyword first pass is re-ranked by Nemotron Nano, which explains its choice. Text captures and text files are scanned and secrets are redacted before anything leaves; the resulting Handoff Scroll is sealed with SHA-256.
- **Wiz (`Ctrl+Alt+W`).** An agent on Nemotron Super that calls tools (capture, lasso, AInbox, Codex, work orders) and asks before it changes anything.
- **Holodex drawer (`Ctrl+Alt+H`).** Session, AInbox, Codex, GrimX and Mods tabs, plus the profile panel for keys and Hivemind sign-in.
- **Cursor and gestures.** A morphing cursor drawn per frame with layered windows and four cursor styles. Hold-to-draw gestures (shake, circle, sweep) that open Wiz, the lasso, a spell slot or the drawer are off on a new install; turn them on in tray **Settings**, **Spells you draw**.
- **Mouse awareness.** Detects the connected mouse by vendor and model and keeps a per-device profile.

## Shortcuts

| Action | Shortcut |
| --- | --- |
| HUD | `Ctrl+Alt+Space` |
| Holodex drawer | `Ctrl+Alt+H` (or `Ctrl` + sweep right, once hold-to-draw is on) |
| Lasso | `Ctrl+Alt+S` (or `Ctrl` + shake up and down, once hold-to-draw is on) |
| Element picker | `Ctrl+Alt+E` |
| Wiz agent | `Ctrl+Alt+W` |
| AInbox | `Ctrl+Alt+I` |
| Guided tour | `Ctrl+Alt+T` |
| Spell orb | `F15` or `Ctrl+Shift+Space` (hold the middle button once you turn it on in tray **Spell Orb**) |
| Next Input Studio profile (Ark installs) | `Ctrl+Alt+P` |
| Show or hide the Windows cursor | `Ctrl+Alt+C` |
| Quit and restore the Windows cursor | Tray menu, **Quit Holodex** |

On a keyboard layout with AltGr (German, French, Polish and others), any `Ctrl+Alt` shortcut that types a character there (for example AltGr+E for the euro sign) is left to typing, and Holodex says which in a notice. Those tools stay in the tray menu.

If Holodex is ever killed while the wand cursor is showing, the next start puts the Windows cursors and your mouse speed back first.

## Known limitations

- Windows only.
- Images and recordings are not scanned for secrets; only text captures and text files are.
- Screen recording and voice capture are designed but not built; the UI shows them as planned.
- The AInbox first-pass ranking is keyword heuristics; Nemotron refines it when a key is set.
- MCP servers connected over HTTP in the Vault are listed, not yet used in answers. Servers bridged from your other MCP clients offer their tools on the Nebius direct route only; each row says which.

## Built during the submission period

The repository was published on 2026-10-20; every commit is inside the hackathon window (from 2026-08-26). Work in that window includes the Token Factory model routing above, the Wiz agent's tool calling on Nemotron Super, the AInbox stack with Nemotron ranking and sealed Handoff Scrolls, the secret redaction gate, Hivemind sign-in over MCP with OAuth, honest model labels on every card, and the guided tour.

## License

Apache License 2.0, see [`LICENSE`](LICENSE). Bundled fonts (Inter, Space Grotesk, JetBrains Mono, Archivo Black) are under the SIL Open Font License 1.1, see [`NOTICE`](NOTICE).
