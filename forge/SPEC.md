# 4GE (Forge) for Holodex: build spec

Status: MVP under construction on branch `feat/4ge` (GalaAI/holodex). This file is the contract every builder works
from. Product name is **4GE**; the folder is `forge/`.

## What it is

4GE is a builder inside Holodex. A user brainstorms an idea with **Prometheus**, gets a **Seed Scroll**, and builds it
in a full-screen IDE where they talk only to **Smith**. Smith delegates layers to the orchestra in parallel nested
sessions: **Hephaestus** (architecture and backend), **Athena** (orchestration, integrations, data flow), **Metis**
(research, extraction, evidence, design inventory) and **Icarus** (deploy, sandbox, test flight). Any kind of build
works: website, web app, mobile app, video game, ERP or CRM, desktop app for Windows, Linux or macOS.

4GE never calls ArkStation, Ark API, Nexus or the owner's Hivemind. It runs on the user's PC with the user's own keys
and accounts. Code is copied from the Ark repo (the private ArkStation repo; read it, never edit it) and adapted here.

## The flow (operator's words, condensed)

1. The user clicks **4GE** in Holodex. An **Imagistation** session opens where they brainstorm with Prometheus.
2. The user says what they want ("a website for my tattoo shop").
3. Prometheus asks follow-up questions and picks the right **Blueprint** from the user's **Atlas** inventory. If none
   fits, Prometheus crafts a new Blueprint with the user (a method, below) and registers it in their Atlas.
4. Prometheus writes a **Seed Scroll** and asks: build now or later?
   - Later: the Seed goes to **AInbox** with a reminder note to time-block it.
   - Now: the full-screen IDE opens.
5. Smith reads the Seed and starts. While the orchestra builds the hidden layers in parallel, Smith works with the
   user on the design system, the component inventory and the landing page, laid out as a mockup spread on a
   **StageCanvas**.
6. Smith suggests an AI orchestration to manage and promote what was built (for a site: content, SEO, social,
   bookings), connected to **Ryozan** and other apps, mods, MCP servers and adapters as the build needs.
7. When the build finishes, the frontend is wired to every layer. Tuning is the user's, guided by Smith, with **tweak
   controls and annotation tools**: colors, placement, layout, text, animation, motion, transitions, assets,
   components.
8. Icarus launches it in a **sandbox** (Eden AEthernet, local Docker): bots test the UI, and agents play both the
   **staff user** and the **customer user** so the builder can dry-run the app and tweak it.
9. When every layer's test flight is green, the build joins the user's **Apps** in the Holodex **Library** (the drawer's
   Library tab, also summonable by a gesture Spell), next to Ryozan and shortcuts to third-party repos.

**Library placement (operator rulings 2026-10-30): mods and apps live in the Holodex Library, grouped by origin.**
The Library tab lists every item under one of five origin groups, in this order:

| Group | Holds |
|---|---|
| **My projects** | apps the user built in 4GE (finished builds and builds in progress), and Ryozan |
| **Repos** | third-party repository shortcuts (GitHub and other git remotes) that open as launchers |
| **Mods** | Dev Mod, PC Mod, Launch Mod and the user's own mods, with the plus tile to create one |
| **Augments** | plugins: MCP servers, skills, prompts and tool packs a mod or build uses |
| **Adapters** | connections to accounts and services (Northflank, Cloudflare, GCP, Railway, Vercel, Supabase, Docker, Tailscale, Nebius, Hivemind, ...) |

Every Library item carries an `origin` field with one of `project`, `repo`, `mod`, `augment`, `adapter`
(`@4ge/contracts` `LibraryItem`), and the server exposes one `GET /library` list grouped by it. Clicking an item opens
the matching Smith session. The studio reads the same list; Holodex's Library tab is its home.

The preview shows a UI for each stage as it builds, and after each **sprint** the user gets a checkpoint to give input
on that layer.

## StageCanvas and the Smith thread (operator ruling 2026-10-30, after launch)

The build canvas works like the **Ark UI Audit Board** (artifact https://claude.ai/artifact/6ZZss13J2bd1rzLLrHiMJE), and
the user talks to Smith through a thread component like the **Holodex Audit Ledger**'s comment threads.

- **StageCanvas = audit board.** A pannable, zoomable canvas (drag to pan, wheel or pinch to zoom, Fit all, 1:1) with a
  dotted grid. Each stage of the build is a titled row; each screen, layer view or artboard of the build is a frame at
  1:1 with a label above it. A frame carries a status ring: green (looks right / test flight green), amber (needs work /
  building), red (broken / failed). A toolbar has jump-to-stage buttons, zoom controls, filters (All, Not reviewed, Needs
  work, Broken) and live counts. Clicking a frame selects it (outline) and opens an inspector on the right: a preview,
  meta chips (stage, agent, receipt), the verdict buttons (Looks right, Needs work, Broken), a note, Previous and Next,
  keyboard keys (arrows, 1 2 3 for verdicts, F to fit, Esc). Frames update live as the orchestra builds.
- **Smith thread = ledger comment threads.** The user pins a comment on any frame, element or region (anchored to it,
  with the selected text or element named), and the thread opens beside it: the user's message, Smith's replies marked
  as Smith, the agent Smith delegated to and its result, the status (open, in progress, done) and a resolve action.
  "Send to Smith" activates a thread for Smith; Smith acts on it (a tweak, a rebuild of a layer, an answer) and replies
  in the same thread with what changed. The left Smith panel is the same thread component for the build-wide
  conversation, and a findings list (like the ledger rows: what you saw, what it should do, priority, status) collects
  every open thread. Reviewing a build is walking the canvas, setting verdicts and leaving threads for Smith.

## Mods, adapters, augments

- Each user has their own **Mods**: **Dev Mod**, **PC Mod**, **Launch Mod**, plus any mod a build connects. A mod is
  agent-run and managed in 4GE; its inventory UI is minimal and lives in the Holodex Library (Mods section). Clicking a mod opens a Smith session about that mod
  (upgrade it, learn it). A **plus** tile creates a mod from adapters and augments.
- **Adapter** = a connection to an account or service. **Augment** = a plugin (tools, skills, prompts). Model these on
  current industry practice: MCP server configs (stdio and streamable HTTP), Anthropic Agent Skills (`SKILL.md`) and
  Claude Code plugin manifests, OpenAI Apps SDK and connectors. Cite the docs you followed in `forge/packages/mods`.
- Launch Mod adapters to start with: **Northflank, Cloudflare, GCP, Railway, Vercel, Supabase, Docker, Tailscale**.
  The user connects an existing account or opens a new one (sign-up link). Auth runs through each provider's own CLI
  login or token flow, so domains, projects, credentials and profiles are managed from the Launch Mod with the user's
  own account. Credentials live in the user's OS credential store (Windows Credential Manager on Windows), never in
  files, logs or the repo.

## Architecture

```
forge/
  package.json, pnpm-workspace.yaml, tsconfig.base.json
  packages/
    contracts/   shared types and zod schemas for everything below (owned by the foundation step)
    bamf/        .bamf command envelope (copied from @ark/bamf-schema) + `bamf` CLI (validate, plan, run)
    gate/        4GE stage gate and receipts (copied from @ark/shared-types stage-gate)
    atlas/       Blueprint schema, starter Blueprints, the user's Atlas registry, Blueprint crafting method
    orchestra/   agents, model client, nested session tree, sprints and checkpoints, Seed Scroll
    mods/        mods, adapters, augments, catalogs, credential store, CLI and MCP runners
    kit/         design tokens (copied from @ark/tokens) and the Phosphor React components the studio uses
  apps/
    server/      Node HTTP + SSE API on 127.0.0.1:17400; hosts the orchestra, builds, mods, sandbox control
    studio/      Vite + React 19 web app on 127.0.0.1:17401 (dev) or served by the server (prod)
  sandbox/       docker compose: app, aethernet (Chromium + CDP), bots (Playwright personas); Icarus test flights
  starters/      app templates the Blueprints scaffold from
  e2e/           Playwright tests of the studio and the flow, run against the sandbox
```

- Language: TypeScript, ESM, Node 24, pnpm workspace. Package scope `@4ge/*`. zod 4 for schemas.
- Data lives under `%LOCALAPPDATA%\Holodex\4ge` (Windows) or `~/.4ge` elsewhere: `atlas/`, `seeds/`, `builds/<id>/`,
  `mods/`, `sessions/`. Resolve it in `@4ge/contracts` (`dataRoot()`), nowhere else.
- Ports: server 17400, studio dev 17401, sandbox app 17410, sandbox CDP 17411, sandbox viewer 17412. Never use 5300,
  8788, 4200, 15300, 18788 or 14200.
- Models: OpenAI-compatible chat completions. Route order: the user's Nebius Token Factory key (env
  `NEBIUS_API_KEY`, base `https://api.tokenfactory.nebius.com/v1`), then the user's Hivemind `model_invoke` (env
  `HIVEMIND_URL` + `HIVEMIND_TOKEN`), then none (the UI says how to connect a model). Default models: Smith and
  Prometheus `nvidia/nemotron-3-super-120b-a12b`, workers `nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B`, vision
  `nvidia/Nemotron-3.5-Super-VL`. Read the real model ids the provider lists at runtime; never hardcode a key.
- Holodex starts the server with the user's keys in its environment and opens the studio full screen in WebView2
  (`ArkPageForm` pattern). The Seed "later" path writes into the AInbox queue
  (`%LOCALAPPDATA%\Omniwand\ainbox-queue`) in AInbox's own item format.

## Reuse map (paths in the ArkStation repo, origin/main)

| Need | Source |
|---|---|
| .bamf envelope | `packages/bamf-schema/src/index.ts` |
| Stage gate | `packages/shared-types/src/stage-gate.ts`, `scripts/4ge-stage-gate.mjs` |
| Blueprints, Seed | `packages/atlas-core/src/{blueprint,schemas,seed,build-registry,library}.ts` and the `*-blueprint.ts` files |
| Seed Scroll, Imagistation, Prometheus | `docs/architecture/SEED_SCROLL.md`, `apps/web-console/app/shell/ImagistationView.tsx`, `agents/shared/proposals/arkitek/agent-prometheus.manifest.json` |
| Agent roles | `agents/shared/registry.json`, `packages/lexicon/src/veritas-canon.ts` |
| Design tokens | `packages/tokens/src/*.css` |
| Phosphor components | `packages/ui/src` (StageCanvas, Button, Eyebrow, ActionPill, surfaces) and each `*.prompt.md` |
| IDE layout | `apps/web-console/app/shell/ForgeView.tsx`, `VesselPreviewer.tsx` |
| Sandbox image | `infra/local/eden-desktop/Dockerfile` target `aethernet` |
| Sandbox compose pattern | `eden/matrices/bttco/compose.yaml` (Eden) |
| Browser bot runner | `apps/desktop-node/aethernet/runner.mjs` |
| Holodex WebView2 host | `ArkPageForm.cs`, `AugmentInventoryForm.cs` in this repo |

Copy the narrow part you need, strip Ark-only imports (anvl-core, pc-mod, lexicon wiring, Ark endpoints) and say in a
header comment where it came from. Do not import from the Ark repo at build time.

## Design rules

- Phosphor is the principle 4GE builds from: dark surfaces, `//` mono eyebrows as section labels, one input and one
  primary action per surface, the AI proposes and the human confirms, summary first and detail on demand, plain words
  where people act, decoration only when it carries data. Use `@4ge/kit` tokens; no raw hex in components.
- UX reference: Claude Code (one conversation drives the work; nested agents shown as a collapsible tree with live
  status; diffs and receipts on demand) and Claude Design (canvas spread of artboards, inline comments and annotations,
  tweak panels). Fill their gaps, keep this architecture.
- Use the 21st registry (`21st-registry`, `21st-ui` skills) to find component candidates before writing one.

## Done means

- `pnpm -C forge install && pnpm -C forge build && pnpm -C forge test` pass with no type errors.
- The flow above runs end to end on gala with a real model: Imagistation to Seed to Blueprint to parallel build to
  sandbox test flight to inventory, for at least the website starter, with each stage's preview and checkpoint shown.
- The sandbox comes up with `docker compose`, bots play staff and customer, and the e2e suite (including edge cases:
  no model key, model error, empty idea, cancelled build, failed layer, port taken, Docker down) is green.
- Holodex opens 4GE full screen, the "later" Seed lands in AInbox, and the Library groups every item by origin (My
  projects, Repos, Mods with the plus tile, Augments, Adapters); clicking an item opens the matching Smith session.
- No secret, token, email or personal path is committed.
