# Holodex

**NVIDIA Nemotron at the tip of your cursor, in any Windows app.**

Holodex is a native Windows desktop app. It replaces the cursor with a wand and puts NVIDIA Nemotron behind it: point at anything and ask, cut out part of the screen and ask about it, stack captures into a handoff, or let the Wiz agent do a multi-step job. Every answer card names the model and the route that answered.

Holodex is the desktop client. **Hivemind**, its backend, is the hosted sign-in that serves Nemotron to Holodex with no key of your own.

> **Nebius x NVIDIA Global AI Hackathon** · Track: **Personal AI** · License: **Apache-2.0** ([`LICENSE`](LICENSE))

### Why I built this

> *"I needed help managing my business so I built my shop's app and website from scratch. I taught myself how to develop, build and ship full stack applications using AI. What I didn't expect was to fall in love with the process of architecting orchestrated software and now am founding a tech company called Ark Omnitek made by a tattooer, built for artists and creators. I also wanted to build something that would help other tattooers like my dad. But if it didn't help mine, what good would that do? Once my analytics proved it worked on my shop first, I knew I could confidently give him something that works and put it out there in the world."*
> **Justin Walker**

---

## How Holodex uses NVIDIA Nemotron

Every question goes to an NVIDIA Nemotron open model on **Nebius Token Factory** as an OpenAI-compatible chat completion. Holodex picks the size per job.

| Surface | Model | Where in the code |
| --- | --- | --- |
| HUD ask box (`Ctrl+Alt+Space`) and the Holodex drawer chat (`Ctrl+Alt+H`) | **Nemotron 3 Nano** (`nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B`) by default; another can be picked on the model chip | `ArkStationClient.SelectedModel` starts at `DefaultNemotronFastModel` |
| HUD with the screen attached | **Nemotron 3.5 Super VL** (`nvidia/Nemotron-3.5-Super-VL`) | `OmniwandHudForm.AnsweringModel`; `ArkStationClient.AskAsync` sends any image to the VL model |
| Snip and Lasso (`Ctrl+Alt+S`), Pick a thing (`Ctrl+Alt+E`) | **Nemotron 3.5 Super VL**, first in the vision picker | `ScreenSnipAndElementPickerForm.VisionModels`, `ArkStationClient.AskVisionModelAsync` |
| Wiz plan and agent loop (`Ctrl+Alt+W`) | **Nemotron 3 Super** (`nvidia/nemotron-3-super-120b-a12b`) | `WizAgent.DefaultReasoningModel`; the tool-calling loop is `WizAgent.RunAsync` |
| AInbox re-rank of captures | **Nemotron 3 Nano** | `AInboxStack.cs` (`Model = DefaultNemotronFastModel`) |
| Model picker, by choice | **Nemotron 3 Ultra** (`nvidia/Nemotron-3-Ultra-550b-a55b`) and Nemotron 3.5 Lightning | `ArkStationClient.BuiltInNemotronModels` |

**Route order.** Each ask tries, in order (`ArkStationClient.AskAsync`):

1. **The Ark API**, only on a PC that runs its owner's Ark: the gateway must answer with its own health shape and the PC must hold its inbound key (`ProbeArkApiAsync`, `ArkRouteReady`).
2. **Your own Nebius Token Factory key**, if you added one: Holodex calls `https://api.tokenfactory.nebius.com/v1` directly (`AskNebiusTokenFactoryAsync`).
3. **Hivemind**, when you are signed in: the same Nemotron models, served by Hivemind with no key on your PC (`AskHivemindAsync`, Hivemind's `model_invoke`).

A model that LM Studio or Ollama serves on your own PC can also be picked on the chip. It answers from your machine and never falls over to the cloud.

**Where Nebius Token Factory sped the work up.**

- **No key for judges.** Hivemind serves the same Nemotron models from Token Factory, so a fresh install answers after one sign-in, with no Nebius account.
- **The fast tier for re-ranking.** Nemotron 3 Nano re-ranks the AInbox stack on every capture you add; about three billion active parameters keep that quick.
- **Super VL for vision.** One vision model reads screen captures for Snip, Lasso and the HUD, and the answer card says so.
- **One API for every size.** Moving a surface between Nano, Super, Ultra and Super VL is a model id and nothing else.

---

## Hivemind

Hivemind is a hosted sign-in and remote MCP gateway: one account, and the tools behind it reached over the Model Context Protocol.

- **Sign-in.** Holodex signs in with OAuth 2.1, dynamic client registration and PKCE, in your browser (`HivemindClient`). Tokens stay in Windows Credential Manager on your PC.
- **What it serves Holodex on Nebius Token Factory, with no key:** Nemotron text answers (`model_invoke`); image questions for Snip and Lasso where the Hivemind instance takes image parts (where it does not, Holodex says an image needs a Nebius key and keeps answering text); and Tavily web search (`web_search`) where the instance offers it. Answer cards show the receipt ids Hivemind returns.
- **Using Hivemind directly.** Hivemind portal: see the Devpost testing instructions. Its **Connect a client** page connects other MCP clients.
- **Demo login.** Use the demo account in the Devpost testing instructions, or create your own account with an invite code. No credentials are in this repository.

---

## Install and first run

You need Windows 10 or 11 (x64). A Nebius key is optional.

1. Download `Holodex-<version>-win-x64.zip` and the `.sha256` file beside it from [Releases](../../releases).
2. Check it: `(Get-FileHash .\Holodex-<version>-win-x64.zip -Algorithm SHA256).Hash.ToLower()` must match the `.sha256` file. Extract it anywhere.
3. Run `Omniwand.exe`. (The executable, the `Omniwand:*` credential names and the `%LOCALAPPDATA%\Omniwand` folder keep the project's earlier name so existing installs keep working.)
4. On a fresh install the Holodex drawer opens on three first steps. **Sign in with Hivemind** is the one answers need; then ask Nemotron in the box below.
5. **Your PC** (tray menu, or the third first step) scans this PC: apps, local models, MCP servers, services and hardware. Nothing connects without your OK; each row has its own Connect.

**Optional key.** To call Token Factory directly (images on any Hivemind, and tools: web search with a Tavily key, files, the approved PC command line, bridged MCP servers), open **Vault** from the tray and paste your key from [tokenfactory.nebius.com](https://tokenfactory.nebius.com/). Keys stay in Windows Credential Manager, never in a file or a log. Holodex also reads `NEBIUS_API_KEY` from the environment.

**From source.** Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then:

```powershell
dotnet publish Omniwand.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish-win-x64 -nologo
```

---

## What it does

- **HUD (`Ctrl+Alt+Space`).** Ask about whatever is under the cursor; the screen around it can go with the question.
- **Snip and Lasso (`Ctrl+Alt+S`), Pick a thing (`Ctrl+Alt+E`).** Cut out any shape, mark it up, ask about it, or save it to your Library.
- **AInbox (`Ctrl+Alt+I`).** Stack captures, let Nemotron rank them, and send a sealed handoff. Text is scanned and secrets are redacted before anything leaves.
- **Wiz (`Ctrl+Alt+W`).** An agent on Nemotron Super that plans and, with a Nebius key, acts with tools, asking before it changes anything.
- **Holodex drawer (`Ctrl+Alt+H`).** Session chat, AInbox, Library, Spells and settings.
- **Vault (tray).** Keys, MCP sign-ins and local services in one place. Each connected MCP server says whether answers use its tools yet.
- **The wand cursor.** Drawn every frame, four looks. If Holodex is ever killed, its next start puts your Windows cursors and mouse speed back first.

## Shortcuts

| Action | Shortcut |
| --- | --- |
| Ask (HUD) | `Ctrl+Alt+Space` |
| Holodex drawer | `Ctrl+Alt+H` |
| Snip and Lasso | `Ctrl+Alt+S` |
| Pick a thing or a color | `Ctrl+Alt+E` |
| Wiz | `Ctrl+Alt+W` |
| AInbox | `Ctrl+Alt+I` |
| Show me around | `Ctrl+Alt+T` |
| Spell Orb | `F15` or `Ctrl+Shift+Space` |
| Show or hide the Windows cursor | `Ctrl+Alt+C` |
| Quit (puts the cursor back) | Tray menu, **Quit Holodex** |

The tray lists only the shortcuts this PC registered. On a keyboard with AltGr (German, French, Polish and others), a `Ctrl+Alt` shortcut that types a character there is left to typing, and Holodex says which. Holding the middle button for the Orb and hold-to-draw gestures are off on a new install; turn them on in the tray.

## Known limits

- Windows only.
- Image questions through Hivemind need a Hivemind instance that takes image parts; without one, an image needs a Nebius key.
- Wiz acts with tools only with a Nebius key; through Hivemind it drafts the plan.
- MCP servers connected over HTTP in the Vault are listed, not yet used in answers. Servers bridged from your other MCP clients offer their tools on the Nebius direct route only.
- Images and recordings are not scanned for secrets; only text is. Screen recording and voice capture are shown as planned.

## License

Apache License 2.0, see [`LICENSE`](LICENSE). Bundled fonts (Inter, Space Grotesk, JetBrains Mono, Archivo Black) are under the SIL Open Font License 1.1, see [`NOTICE`](NOTICE).
