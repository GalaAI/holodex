import type { Augment } from "@4ge/contracts";

/**
 * Built-in augments: the providers' own MCP servers, the 4GE skills shipped
 * in packages/mods/skills (Agent Skills format, SKILL.md), and 4GE's own
 * detection tool pack.
 *
 * MCP server configs follow the MCP specification's two standard transports
 * (stdio and Streamable HTTP, revision 2026-07-28) and export to the
 * `.mcp.json` shape Claude Code and other clients read (see manifest.ts).
 * Remote servers here all use OAuth through the MCP client, so they carry no
 * header keys and 4GE stores nothing for them.
 */
export type AugmentDefinition = Omit<Augment, "status" | "statusMessage">;

export const BUILT_IN_AUGMENTS: AugmentDefinition[] = [
  {
    id: "augment:vercel-mcp",
    name: "Vercel MCP",
    kind: "mcp-server",
    description: "Vercel's official MCP server: docs search, projects, deployments, logs and Web Analytics on your account.",
    mcp: { transport: "streamable-http", url: "https://mcp.vercel.com", headerKeys: [] },
    requires: ["adapter:vercel"],
    sourceUrl: "https://vercel.com/docs/agent-resources/vercel-mcp",
    enabled: true,
  },
  {
    id: "augment:cloudflare-mcp",
    name: "Cloudflare API MCP",
    kind: "mcp-server",
    description: "Cloudflare's official MCP server covering the full Cloudflare API on your account.",
    mcp: { transport: "streamable-http", url: "https://mcp.cloudflare.com/mcp", headerKeys: [] },
    requires: ["adapter:cloudflare"],
    sourceUrl: "https://developers.cloudflare.com/agents/model-context-protocol/mcp-servers-for-cloudflare/",
    enabled: true,
  },
  {
    id: "augment:supabase-mcp",
    name: "Supabase MCP",
    kind: "mcp-server",
    description: "Supabase's official hosted MCP server: projects, SQL, migrations, edge functions and docs search.",
    mcp: { transport: "streamable-http", url: "https://mcp.supabase.com/mcp", headerKeys: [] },
    requires: ["adapter:supabase"],
    sourceUrl: "https://supabase.com/docs/guides/getting-started/mcp",
    enabled: true,
  },
  {
    id: "augment:railway-mcp",
    name: "Railway MCP",
    kind: "mcp-server",
    description: "Railway's official MCP server, bundled in the Railway CLI and signed in with your CLI login.",
    mcp: { transport: "stdio", command: "railway", args: ["mcp"], envKeys: [] },
    requires: ["adapter:railway"],
    sourceUrl: "https://docs.railway.com/ai/mcp-server",
    enabled: true,
  },
  {
    id: "augment:gcloud-mcp",
    name: "gcloud MCP",
    kind: "mcp-server",
    description: "Google's official gcloud MCP server: runs gcloud commands with your own gcloud sign-in.",
    mcp: { transport: "stdio", command: "npx", args: ["-y", "@google-cloud/gcloud-mcp"], envKeys: [] },
    requires: ["adapter:gcp"],
    sourceUrl: "https://github.com/googleapis/gcloud-mcp",
    enabled: true,
  },
  {
    id: "augment:docker-mcp-gateway",
    name: "Docker MCP gateway",
    kind: "mcp-server",
    description: "Docker's MCP Toolkit gateway: serves the MCP servers you enable in Docker Desktop, each in its own container.",
    mcp: { transport: "stdio", command: "docker", args: ["mcp", "gateway", "run"], envKeys: [] },
    requires: ["adapter:docker"],
    sourceUrl: "https://docs.docker.com/reference/cli/docker/mcp/gateway/gateway_run/",
    enabled: true,
  },
  {
    id: "augment:launch-mod-skill",
    name: "Launch runbook",
    kind: "skill",
    description: "Agent Skill: how Icarus and Smith take a finished build live with the user's own accounts.",
    skillPath: "skills/launch-mod",
    requires: [],
    enabled: true,
  },
  {
    id: "augment:dev-mod-skill",
    name: "Dev loop",
    kind: "skill",
    description: "Agent Skill: how the orchestra runs, tests and sandboxes a build on this PC.",
    skillPath: "skills/dev-mod",
    requires: [],
    enabled: true,
  },
  {
    id: "augment:pc-mod-skill",
    name: "PC check",
    kind: "skill",
    description: "Agent Skill: what to check on this PC before a build (CLIs, Docker, ports, credential store).",
    skillPath: "skills/pc-mod",
    requires: [],
    enabled: true,
  },
  {
    id: "augment:pc-detect",
    name: "PC detection",
    kind: "tool-pack",
    description: "4GE's own tools: which provider CLIs are installed and signed in, and whether Docker is up. Served at GET /api/adapters.",
    requires: [],
    enabled: true,
  },
];
