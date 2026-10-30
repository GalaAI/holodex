# @4ge/mods

Mods, adapters and augments for 4GE, plus the Library grouped by origin.

- **Adapter**: a connection to one of the user's accounts or services. Auth runs through the provider's own CLI login or
  token flow. 4GE stores nothing for CLI-login adapters (the provider's CLI keeps its own session). Tokens go to
  Windows Credential Manager under a `4GE:` target and nowhere else.
- **Augment**: a plugin a mod or build uses: an MCP server, an Agent Skill, a prompt or a tool pack.
- **Mod**: an agent-run bundle of adapters and augments. Every user starts with **Dev Mod**, **PC Mod** and
  **Launch Mod**. The plus tile composes a new one.

## Layout

| File | Owns |
|---|---|
| `src/catalog/adapters.ts` | The adapter catalog: Northflank, Cloudflare, Google Cloud, Railway, Vercel, Supabase, Docker, Tailscale, Nebius, Hivemind. Sign-up and docs links, CLI install and login, status probe, MCP note, and the actions each one exposes. |
| `src/catalog/augments.ts` | Built-in augments: each provider's official MCP server, the three shipped skills, the PC detection tool pack. |
| `src/catalog/mods.ts` | Dev Mod, PC Mod, Launch Mod. |
| `src/credentials.ts` | Windows Credential Manager store (CredReadW, CredWriteW, CredDeleteW through PowerShell), memory store for tests. |
| `src/cli.ts` | PATH lookup, running a CLI without a shell (execa), opening one in its own window, output redaction. |
| `src/detect.ts` | Is each CLI installed and signed in; live key checks for Nebius and Hivemind; cached and saved results. |
| `src/actions.ts` | Connect, disconnect, and run adapter actions with checked parameters. |
| `src/manifest.ts` | The mod manifest, `.mcp.json` export, Claude Code plugin manifest, Agent Skills checks. |
| `src/service.ts` | Mods and augments on disk, status from live detection, the plus tile. |
| `src/library.ts` | `GET /library`: My projects, Repos, Mods, Augments, Adapters. |
| `skills/` | `launch-mod`, `dev-mod`, `pc-mod` Agent Skills, copied to `<data root>/mods/skills` on start. |

The server mounts it in `apps/server/src/routes/mods.ts` and `apps/server/src/routes/library.ts`.

## Manifest formats

### 1. Mod manifest: `mods/user/<slug>/mod.json`

```json
{
  "manifestVersion": 1,
  "id": "mod:shop-launch",
  "name": "Shop Launch",
  "description": "Puts the shop site on Vercel with Supabase bookings.",
  "version": "0.1.0",
  "kind": "user",
  "agent": "smith",
  "adapters": [
    { "id": "adapter:vercel", "auth": "cli-login" },
    { "id": "adapter:nebius", "auth": "token", "credentialTarget": "4GE:adapter:nebius" }
  ],
  "augments": ["augment:supabase-mcp", "augment:launch-mod-skill"],
  "createdAt": "2026-10-30T12:00:00.000Z",
  "updatedAt": "2026-10-30T12:00:00.000Z"
}
```

It names credential **targets**, never values. An augment's required adapters are added to the mod automatically.

### 2. MCP servers: `mods/user/<slug>/.mcp.json`

The shape Claude Code and other MCP clients read. Secrets are `${VAR}` placeholders the client expands from its own
environment; 4GE never writes a value here.

```json
{
  "mcpServers": {
    "vercel": { "type": "http", "url": "https://mcp.vercel.com" },
    "railway": { "command": "railway", "args": ["mcp"] },
    "keyed": { "command": "node", "args": ["server.js"], "env": { "API_TOKEN": "${API_TOKEN}" } }
  }
}
```

In 4GE's own augment records (`@4ge/contracts` `mcpServerConfigSchema`) the same servers are
`{ "transport": "stdio", "command", "args", "envKeys" }` or `{ "transport": "streamable-http", "url", "headerKeys" }`,
the two standard MCP transports.

### 3. Claude Code plugin manifest: `mods/user/<slug>/.claude-plugin/plugin.json`

```json
{ "name": "4ge-shop-launch", "displayName": "Shop Launch", "version": "0.1.0", "description": "...", "keywords": ["4ge", "mod", "user"] }
```

With `.mcp.json` and `skills/<name>/SKILL.md` at the folder root (the default plugin layout), a mod folder loads as a
Claude Code plugin as is.

### 4. Agent Skills: `skills/<name>/SKILL.md`

YAML frontmatter with `name` (1 to 64 characters, lowercase letters, numbers and single hyphens, equal to the folder
name) and `description` (1 to 1024 characters, what it does and when to use it); optional `license`, `compatibility`
(up to 500 characters), `metadata` (text to text) and `allowed-tools`. `checkSkillFolder()` enforces these rules before
a skill augment is accepted.

### 5. Augment records: `mods/augments/<slug>.json`

`@4ge/contracts` `augmentDraftSchema`: `id` (`augment:<slug>`), `name`, `kind` (`mcp-server`, `skill`, `prompt`,
`tool-pack`, `plugin`), `description`, and `mcp`, `skillPath` (inside the mods folder) or `prompt` by kind, plus
`requires` (adapter ids).

## Adapters

| Adapter | CLI and sign-in | Status probe | Official MCP server | Actions |
|---|---|---|---|---|
| Northflank | `npm i -g @northflank/cli`, `northflank login` (pick an API token in the browser) | `northflank context ls` | none official (third-party hosted only) | create project, make secret group, switch context |
| Cloudflare | `npm i -g wrangler`, `wrangler login` | `wrangler whoami --json` | `https://mcp.cloudflare.com/mcp` | Pages project, KV, R2, D1, Worker secret |
| Google Cloud | Cloud SDK installer, `gcloud auth login` | `gcloud auth list --filter=status:ACTIVE` | gcloud MCP (`npx -y @google-cloud/gcloud-mcp`, stdio) | create project, service account, configuration profile, buy a domain (Cloud Domains) |
| Railway | `npm i -g @railway/cli`, `railway login` | `railway whoami --json` | `railway mcp` (in the CLI, stdio); hosted `mcp.railway.com` | create project, link project, attach domain |
| Vercel | `npm i -g vercel`, `vercel login` | `vercel whoami --format json` | `https://mcp.vercel.com` | create project, buy a domain, attach a domain, environment variable, switch team |
| Supabase | Supabase CLI, `supabase login` | `supabase projects list -o json` | `https://mcp.supabase.com/mcp` | create project, link project, list projects |
| Docker | Docker Desktop, `docker login` (optional) | `docker info`, Docker Hub user from the credential helper | `docker mcp gateway run` (MCP Toolkit, stdio) | list and switch contexts |
| Tailscale | Tailscale installer, `tailscale login` | `tailscale status --json` | none for managing a tailnet (Aperture only proxies) | list and switch accounts, TLS certificate, Funnel public URL |
| Nebius Token Factory | token, `NEBIUS_API_KEY`, `4GE:adapter:nebius` (or the key Holodex keeps) | live `GET /v1/models` | model route, not MCP | |
| Hivemind | token, `HIVEMIND_TOKEN` + `HIVEMIND_URL`, `4GE:adapter:hivemind` | live `GET /health` | the user's Hivemind speaks MCP | |

Every adapter has a **Sign in** action that opens the provider's own login in its own window. Interactive actions (sign
in, purchases, secrets) open the CLI in a new console so the provider asks its own questions; 4GE never sees a typed
secret. Actions that spend money (`buy-domain`, `register-domain`) are refused unless the request carries
`confirm: true`, and the provider still asks again. Every parameter must match a strict pattern before it reaches a CLI,
and CLIs run without a shell.

## Docs followed (checked 2026-10-30)

- MCP specification 2026-07-28, transports: https://modelcontextprotocol.io/specification/2026-07-28/basic/transports and
  Streamable HTTP: https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http
- Agent Skills specification: https://agentskills.io/specification
- Claude Code plugin manifest reference: https://code.claude.com/docs/en/plugins/manifest-reference
- OpenAI plugins (formerly Apps SDK and connectors), MCP server and auth: https://developers.openai.com/plugins/build/mcp-server,
  https://developers.openai.com/plugins/build/auth; remote MCP in the Responses API:
  https://developers.openai.com/api/docs/guides/tools-connectors-mcp. 4GE follows the same split: tools come from MCP
  servers, remote servers authorize with OAuth 2.1 in the client, and the server enforces authorization on every call.
- Northflank CLI: https://northflank.com/docs/v1/api/use-the-cli
- Wrangler commands: https://developers.cloudflare.com/workers/wrangler/commands/; Cloudflare MCP servers:
  https://developers.cloudflare.com/agents/model-context-protocol/mcp-servers-for-cloudflare/
- gcloud reference: https://cloud.google.com/sdk/gcloud/reference; gcloud MCP: https://github.com/googleapis/gcloud-mcp;
  Google Cloud MCP overview: https://docs.cloud.google.com/mcp/overview
- Railway CLI: https://docs.railway.com/guides/cli; Railway MCP: https://docs.railway.com/ai/mcp-server
- Vercel CLI: https://vercel.com/docs/cli; Vercel MCP: https://vercel.com/docs/agent-resources/vercel-mcp
- Supabase CLI: https://supabase.com/docs/reference/cli/introduction; Supabase MCP: https://supabase.com/docs/guides/getting-started/mcp
- Docker CLI: https://docs.docker.com/reference/cli/docker/; MCP gateway:
  https://docs.docker.com/reference/cli/docker/mcp/gateway/gateway_run/
- Tailscale CLI: https://tailscale.com/kb/1080/cli; Aperture MCP: https://tailscale.com/docs/aperture/mcp-server
- Nebius Token Factory: https://docs.tokenfactory.nebius.com/

## Reused, not rewritten

- The Windows Credential Manager calls mirror Holodex's `ArkStationClient.TryReadWindowsCredential` and
  `TryWriteWindowsCredential` (CredReadW, CredWriteW, UTF-16 blob, generic type, local machine persistence) and reuse
  Holodex's own Nebius target read-only, so the user does not paste the key twice. Node has no binding and no keyring
  package is installed, so the calls run through PowerShell with the secret on stdin; the compiled helper is cached.
- CLI execution is execa (cross-spawn), already in the workspace, so Windows `.cmd` shims run without a shell.
- Ryozan's default address and its `OMNIWAND_RYOZAN_URL` override come from Holodex's `RealmConnectors.RyozanDefault`;
  the public Hivemind default comes from `HivemindClient.HostedHivemindUrl`.
- Ark's `@ark/keymaster-core` was read and not taken: it describes encrypted backend sync to Google Secret Manager and
  deploy targets, which 4GE does not do on the user's PC.
