import type { AdapterAction, AdapterAuth, AdapterCategory } from "@4ge/contracts";

/**
 * The adapter catalog: one entry per account or service a mod can connect.
 *
 * Every fact here was checked against the provider's own docs on 2026-10-30;
 * the `sources` list on each entry names the pages. Auth always runs through
 * the provider's own CLI login or token flow, so 4GE never holds a password.
 * Tokens a provider hands out go to Windows Credential Manager under a
 * "4GE:" target and nowhere else (see credentials.ts).
 *
 * `actions` are what the Launch Mod (and any mod that includes the adapter)
 * can do with the user's own account. Each action is a real CLI command;
 * interactive ones open the CLI in its own window so the provider's own
 * prompts handle sign-in and purchase confirmation.
 */

export interface AdapterCliDefinition {
  bin: string;
  install: string;
  installUrl: string;
  login: string;
  /** Arguments that exit 0 and say who is signed in. Parsed by `probe` in detect.ts. */
  statusArgs: string[];
}

export interface AdapterDefinition {
  id: string;
  slug: string;
  name: string;
  provider: string;
  category: AdapterCategory;
  description: string;
  auth: AdapterAuth;
  signUpUrl: string;
  docsUrl: string;
  cli?: AdapterCliDefinition;
  /** Augment id of the provider's official MCP server, when one exists. */
  mcpAugment?: string;
  mcpNote: string;
  actions: AdapterAction[];
  /** Pages this entry was checked against. */
  sources: string[];
}

/** Safe parameter shapes. The runner refuses any value that does not match. */
export const PARAM_PATTERNS = {
  domain: "^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$",
  name: "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,62}$",
  envName: "^[A-Z][A-Z0-9_]{0,99}$",
  gcpProjectId: "^[a-z][a-z0-9-]{4,28}[a-z0-9]$",
  port: "^[0-9]{2,5}$",
  ref: "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,99}$",
} as const;

function action(input: Omit<AdapterAction, "spends" | "params"> & Partial<Pick<AdapterAction, "spends" | "params">>): AdapterAction {
  return { spends: false, params: [], ...input };
}

const param = (name: string, label: string, pattern: string, placeholder?: string, required = true) => ({
  name,
  label,
  pattern,
  required,
  ...(placeholder ? { placeholder } : {}),
});

const login = (cli: string, command: string): AdapterAction =>
  action({
    id: "sign-in",
    title: "Sign in",
    description: `Opens ${cli} in its own window and runs its sign-in, so you sign in with your own account in your browser.`,
    kind: "account",
    via: "cli",
    interactive: true,
    command,
  });

export const ADAPTERS: AdapterDefinition[] = [
  {
    id: "adapter:northflank",
    slug: "northflank",
    name: "Northflank",
    provider: "Northflank",
    category: "hosting",
    description: "Run services, jobs and databases on Northflank with your own team. The Northflank CLI signs in with an API token you pick in the browser.",
    auth: {
      method: "cli-login",
      cli: "northflank",
      loginArgs: ["login"],
      statusArgs: ["context", "ls"],
      installUrl: "https://northflank.com/docs/v1/api/use-the-cli",
    },
    signUpUrl: "https://app.northflank.com/signup",
    docsUrl: "https://northflank.com/docs/v1/api/use-the-cli",
    cli: {
      bin: "northflank",
      install: "npm i -g @northflank/cli",
      installUrl: "https://northflank.com/docs/v1/api/use-the-cli",
      login: "northflank login",
      statusArgs: ["context", "ls"],
    },
    mcpNote:
      "Northflank does not publish an official MCP server. Third-party hosted ones exist; 4GE uses the official CLI instead.",
    actions: [
      login("the Northflank CLI", "northflank login"),
      action({
        id: "create-project",
        title: "Create a project",
        description: "Starts the Northflank CLI's guided project creation in its own window.",
        kind: "project",
        via: "cli",
        interactive: true,
        command: "northflank create project",
      }),
      action({
        id: "create-secret",
        title: "Make a secret group",
        description: "Starts the CLI's guided secret group creation; you type the values into the CLI, never into 4GE.",
        kind: "credentials",
        via: "cli",
        interactive: true,
        command: "northflank create secret",
      }),
      action({
        id: "switch-context",
        title: "Switch team context",
        description: "Pick which signed-in Northflank team the CLI acts on.",
        kind: "profile",
        via: "cli",
        interactive: true,
        command: "northflank context use",
      }),
    ],
    sources: [
      "https://northflank.com/docs/v1/api/use-the-cli",
      "https://northflank.com/docs/v1/application/secure/manage-api-tokens",
    ],
  },
  {
    id: "adapter:cloudflare",
    slug: "cloudflare",
    name: "Cloudflare",
    provider: "Cloudflare",
    category: "network",
    description: "Pages, Workers, KV, R2 and D1 on your Cloudflare account, through Wrangler, Cloudflare's own CLI.",
    auth: {
      method: "cli-login",
      cli: "wrangler",
      loginArgs: ["login"],
      statusArgs: ["whoami", "--json"],
      installUrl: "https://developers.cloudflare.com/workers/wrangler/install-and-update/",
    },
    signUpUrl: "https://dash.cloudflare.com/sign-up",
    docsUrl: "https://developers.cloudflare.com/workers/wrangler/commands/",
    cli: {
      bin: "wrangler",
      install: "npm i -g wrangler",
      installUrl: "https://developers.cloudflare.com/workers/wrangler/install-and-update/",
      login: "wrangler login",
      statusArgs: ["whoami", "--json"],
    },
    mcpAugment: "augment:cloudflare-mcp",
    mcpNote: "Cloudflare's official API MCP server at https://mcp.cloudflare.com/mcp (streamable HTTP, OAuth).",
    actions: [
      login("Wrangler", "wrangler login"),
      action({
        id: "create-pages-project",
        title: "Create a Pages project",
        description: "Creates a Cloudflare Pages project with main as the production branch.",
        kind: "project",
        via: "cli",
        interactive: false,
        params: [param("name", "Project name", PARAM_PATTERNS.name, "my-site")],
        command: "wrangler pages project create {name} --production-branch main",
      }),
      action({
        id: "create-kv",
        title: "Create a KV namespace",
        description: "Creates a Workers KV namespace.",
        kind: "project",
        via: "cli",
        interactive: false,
        params: [param("name", "Namespace name", PARAM_PATTERNS.name, "SESSIONS")],
        command: "wrangler kv namespace create {name}",
      }),
      action({
        id: "create-r2",
        title: "Create an R2 bucket",
        description: "Creates an R2 storage bucket.",
        kind: "project",
        via: "cli",
        interactive: false,
        params: [param("name", "Bucket name", PARAM_PATTERNS.name, "uploads")],
        command: "wrangler r2 bucket create {name}",
      }),
      action({
        id: "create-d1",
        title: "Create a D1 database",
        description: "Creates a D1 SQLite database.",
        kind: "project",
        via: "cli",
        interactive: false,
        params: [param("name", "Database name", PARAM_PATTERNS.name, "app-db")],
        command: "wrangler d1 create {name}",
      }),
      action({
        id: "put-secret",
        title: "Make a Worker secret",
        description: "Wrangler asks for the value in its own window; 4GE never sees it. Run it from a build to target that build's Worker.",
        kind: "credentials",
        via: "cli",
        interactive: true,
        params: [param("name", "Secret name", PARAM_PATTERNS.envName, "API_KEY")],
        command: "wrangler secret put {name}",
      }),
    ],
    sources: [
      "https://developers.cloudflare.com/workers/wrangler/commands/",
      "https://developers.cloudflare.com/agents/model-context-protocol/mcp-servers-for-cloudflare/",
    ],
  },
  {
    id: "adapter:gcp",
    slug: "gcp",
    name: "Google Cloud",
    provider: "Google Cloud",
    category: "cloud",
    description: "Projects, service accounts, configurations and Cloud Domains on your Google Cloud account, through the gcloud CLI.",
    auth: {
      method: "cli-login",
      cli: "gcloud",
      loginArgs: ["auth", "login"],
      statusArgs: ["auth", "list", "--filter=status:ACTIVE", "--format=value(account)"],
      installUrl: "https://cloud.google.com/sdk/docs/install",
    },
    signUpUrl: "https://console.cloud.google.com/freetrial",
    docsUrl: "https://cloud.google.com/sdk/gcloud/reference",
    cli: {
      bin: "gcloud",
      install: "Google Cloud SDK installer (see install page)",
      installUrl: "https://cloud.google.com/sdk/docs/install",
      login: "gcloud auth login",
      statusArgs: ["auth", "list", "--filter=status:ACTIVE", "--format=value(account)"],
    },
    mcpAugment: "augment:gcloud-mcp",
    mcpNote:
      "Google's official gcloud MCP server (googleapis/gcloud-mcp) runs locally over stdio with your gcloud sign-in; Google also hosts per-product remote MCP endpoints.",
    actions: [
      login("gcloud", "gcloud auth login"),
      action({
        id: "create-project",
        title: "Create a project",
        description: "Creates a Google Cloud project with the id you choose.",
        kind: "project",
        via: "cli",
        interactive: false,
        params: [param("projectId", "Project id", PARAM_PATTERNS.gcpProjectId, "my-app-4ge")],
        command: "gcloud projects create {projectId}",
      }),
      action({
        id: "create-service-account",
        title: "Make a service account",
        description: "Creates a service account identity in a project. No key file is made.",
        kind: "credentials",
        via: "cli",
        interactive: false,
        params: [
          param("name", "Account name", PARAM_PATTERNS.gcpProjectId, "deployer"),
          param("projectId", "Project id", PARAM_PATTERNS.gcpProjectId, "my-app-4ge"),
        ],
        command: "gcloud iam service-accounts create {name} --project={projectId}",
      }),
      action({
        id: "create-configuration",
        title: "Set up a profile",
        description: "Creates and activates a named gcloud configuration, so each app can use its own account and project.",
        kind: "profile",
        via: "cli",
        interactive: false,
        params: [param("name", "Profile name", PARAM_PATTERNS.gcpProjectId, "my-app")],
        command: "gcloud config configurations create {name}",
      }),
      action({
        id: "register-domain",
        title: "Buy a domain",
        description: "Registers a domain with Cloud Domains. gcloud asks for contacts, price and confirmation in its own window.",
        kind: "domain",
        via: "cli",
        interactive: true,
        spends: true,
        params: [param("domain", "Domain", PARAM_PATTERNS.domain, "example.com")],
        command: "gcloud domains registrations register {domain}",
      }),
    ],
    sources: [
      "https://cloud.google.com/sdk/gcloud/reference",
      "https://github.com/googleapis/gcloud-mcp",
      "https://docs.cloud.google.com/mcp/overview",
    ],
  },
  {
    id: "adapter:railway",
    slug: "railway",
    name: "Railway",
    provider: "Railway",
    category: "hosting",
    description: "Projects, services, variables and domains on your Railway account, through the Railway CLI.",
    auth: {
      method: "cli-login",
      cli: "railway",
      loginArgs: ["login"],
      statusArgs: ["whoami", "--json"],
      installUrl: "https://docs.railway.com/guides/cli",
    },
    signUpUrl: "https://railway.com/login",
    docsUrl: "https://docs.railway.com/guides/cli",
    cli: {
      bin: "railway",
      install: "npm i -g @railway/cli",
      installUrl: "https://docs.railway.com/guides/cli",
      login: "railway login",
      statusArgs: ["whoami", "--json"],
    },
    mcpAugment: "augment:railway-mcp",
    mcpNote:
      "Railway's official MCP ships in the Railway CLI (railway mcp, stdio, uses your CLI sign-in); a hosted one is at mcp.railway.com with OAuth.",
    actions: [
      login("the Railway CLI", "railway login"),
      action({
        id: "create-project",
        title: "Create a project",
        description: "Creates a Railway project and links it to the folder it runs in. Run it from a build to link that build.",
        kind: "project",
        via: "cli",
        interactive: true,
        params: [param("name", "Project name", PARAM_PATTERNS.name, "my-app")],
        command: "railway init --name {name}",
      }),
      action({
        id: "link-project",
        title: "Link an existing project",
        description: "Pick one of your Railway projects to link to a build.",
        kind: "profile",
        via: "cli",
        interactive: true,
        command: "railway link",
      }),
      action({
        id: "attach-domain",
        title: "Attach a domain",
        description: "Adds your own domain to the linked service and shows the DNS records to set. Leave it empty for a free railway.app domain.",
        kind: "domain",
        via: "cli",
        interactive: true,
        params: [param("domain", "Domain", PARAM_PATTERNS.domain, "app.example.com", false)],
        command: "railway domain {domain}",
      }),
    ],
    sources: ["https://docs.railway.com/guides/cli", "https://docs.railway.com/ai/mcp-server"],
  },
  {
    id: "adapter:vercel",
    slug: "vercel",
    name: "Vercel",
    provider: "Vercel",
    category: "hosting",
    description: "Projects, domains, environment variables and teams on your Vercel account, through the Vercel CLI.",
    auth: {
      method: "cli-login",
      cli: "vercel",
      loginArgs: ["login"],
      statusArgs: ["whoami", "--format", "json"],
      installUrl: "https://vercel.com/docs/cli",
    },
    signUpUrl: "https://vercel.com/signup",
    docsUrl: "https://vercel.com/docs/cli",
    cli: {
      bin: "vercel",
      install: "npm i -g vercel",
      installUrl: "https://vercel.com/docs/cli",
      login: "vercel login",
      statusArgs: ["whoami", "--format", "json"],
    },
    mcpAugment: "augment:vercel-mcp",
    mcpNote: "Vercel's official MCP server at https://mcp.vercel.com (streamable HTTP, OAuth, reviewed clients only).",
    actions: [
      login("the Vercel CLI", "vercel login"),
      action({
        id: "create-project",
        title: "Create a project",
        description: "Creates a Vercel project.",
        kind: "project",
        via: "cli",
        interactive: false,
        params: [param("name", "Project name", PARAM_PATTERNS.name, "my-site")],
        command: "vercel project add {name}",
      }),
      action({
        id: "buy-domain",
        title: "Buy a domain",
        description: "Vercel shows the price and asks you to confirm in its own window before it buys.",
        kind: "domain",
        via: "cli",
        interactive: true,
        spends: true,
        params: [param("domain", "Domain", PARAM_PATTERNS.domain, "example.com")],
        command: "vercel domains buy {domain}",
      }),
      action({
        id: "attach-domain",
        title: "Attach a domain",
        description: "Adds a domain you own to a Vercel project.",
        kind: "domain",
        via: "cli",
        interactive: false,
        params: [
          param("domain", "Domain", PARAM_PATTERNS.domain, "example.com"),
          param("project", "Project name", PARAM_PATTERNS.name, "my-site"),
        ],
        command: "vercel domains add {domain} {project}",
      }),
      action({
        id: "add-env",
        title: "Make an environment variable",
        description: "The CLI asks for the value in its own window; 4GE never sees it. Run it from a build linked to a project.",
        kind: "credentials",
        via: "cli",
        interactive: true,
        params: [param("name", "Variable name", PARAM_PATTERNS.envName, "DATABASE_URL")],
        command: "vercel env add {name}",
      }),
      action({
        id: "switch-team",
        title: "Switch team",
        description: "Pick which of your Vercel teams the CLI acts on.",
        kind: "profile",
        via: "cli",
        interactive: true,
        command: "vercel switch",
      }),
    ],
    sources: ["https://vercel.com/docs/cli", "https://vercel.com/docs/agent-resources/vercel-mcp"],
  },
  {
    id: "adapter:supabase",
    slug: "supabase",
    name: "Supabase",
    provider: "Supabase",
    category: "database",
    description: "Postgres projects, auth and storage on your Supabase account, through the Supabase CLI.",
    auth: {
      method: "cli-login",
      cli: "supabase",
      loginArgs: ["login"],
      statusArgs: ["projects", "list", "-o", "json"],
      installUrl: "https://supabase.com/docs/guides/local-development/cli/getting-started",
    },
    signUpUrl: "https://supabase.com/dashboard/sign-up",
    docsUrl: "https://supabase.com/docs/reference/cli/introduction",
    cli: {
      bin: "supabase",
      install: "scoop install supabase (Windows) or npx supabase",
      installUrl: "https://supabase.com/docs/guides/local-development/cli/getting-started",
      login: "supabase login",
      statusArgs: ["projects", "list", "-o", "json"],
    },
    mcpAugment: "augment:supabase-mcp",
    mcpNote: "Supabase's official hosted MCP server at https://mcp.supabase.com/mcp (streamable HTTP, OAuth; read_only and features query options).",
    actions: [
      login("the Supabase CLI", "supabase login"),
      action({
        id: "create-project",
        title: "Create a project",
        description: "The CLI asks for the organization, region and database password in its own window.",
        kind: "project",
        via: "cli",
        interactive: true,
        params: [param("name", "Project name", PARAM_PATTERNS.name, "my-app")],
        command: "supabase projects create {name}",
      }),
      action({
        id: "link-project",
        title: "Link a project",
        description: "Links a build to one of your Supabase projects by its reference id.",
        kind: "profile",
        via: "cli",
        interactive: true,
        params: [param("ref", "Project reference", PARAM_PATTERNS.ref, "abcdefghijklmnop")],
        command: "supabase link --project-ref {ref}",
      }),
      action({
        id: "list-projects",
        title: "List projects",
        description: "Lists the Supabase projects your account can reach.",
        kind: "project",
        via: "cli",
        interactive: false,
        command: "supabase projects list",
      }),
    ],
    sources: [
      "https://supabase.com/docs/reference/cli/introduction",
      "https://supabase.com/docs/guides/getting-started/mcp",
    ],
  },
  {
    id: "adapter:docker",
    slug: "docker",
    name: "Docker",
    provider: "Docker",
    category: "container",
    description: "Docker Desktop on this PC: runs the 4GE sandbox and builds images. Signing in to Docker Hub is optional.",
    auth: {
      method: "cli-login",
      cli: "docker",
      loginArgs: ["login"],
      statusArgs: ["info", "--format", "{{.ServerVersion}}"],
      installUrl: "https://docs.docker.com/desktop/setup/install/windows-install/",
    },
    signUpUrl: "https://app.docker.com/signup",
    docsUrl: "https://docs.docker.com/reference/cli/docker/",
    cli: {
      bin: "docker",
      install: "Docker Desktop installer (see install page)",
      installUrl: "https://docs.docker.com/desktop/setup/install/windows-install/",
      login: "docker login",
      statusArgs: ["info", "--format", "{{.ServerVersion}}"],
    },
    mcpAugment: "augment:docker-mcp-gateway",
    mcpNote: "Docker's official MCP Toolkit gateway (docker mcp gateway run, stdio) serves the MCP servers you enable in Docker Desktop.",
    actions: [
      login("the Docker CLI", "docker login"),
      action({
        id: "list-contexts",
        title: "List contexts",
        description: "Shows the Docker contexts (local engine and any remote ones) and which is active.",
        kind: "profile",
        via: "cli",
        interactive: false,
        command: "docker context ls",
      }),
      action({
        id: "use-context",
        title: "Switch context",
        description: "Makes a Docker context the active one.",
        kind: "profile",
        via: "cli",
        interactive: false,
        params: [param("name", "Context name", PARAM_PATTERNS.ref, "default")],
        command: "docker context use {name}",
      }),
    ],
    sources: [
      "https://docs.docker.com/reference/cli/docker/",
      "https://docs.docker.com/reference/cli/docker/mcp/gateway/gateway_run/",
    ],
  },
  {
    id: "adapter:tailscale",
    slug: "tailscale",
    name: "Tailscale",
    provider: "Tailscale",
    category: "network",
    description: "Your tailnet: private links between your devices, TLS certificates and Funnel public URLs, through the Tailscale CLI.",
    auth: {
      method: "cli-login",
      cli: "tailscale",
      loginArgs: ["login"],
      statusArgs: ["status", "--json"],
      installUrl: "https://tailscale.com/download/windows",
    },
    signUpUrl: "https://login.tailscale.com/start",
    docsUrl: "https://tailscale.com/kb/1080/cli",
    cli: {
      bin: "tailscale",
      install: "Tailscale installer (see download page)",
      installUrl: "https://tailscale.com/download/windows",
      login: "tailscale login",
      statusArgs: ["status", "--json"],
    },
    mcpNote:
      "Tailscale does not publish an MCP server for managing a tailnet (its Aperture MCP endpoint only proxies other servers); 4GE uses the official CLI.",
    actions: [
      login("the Tailscale CLI", "tailscale login"),
      action({
        id: "list-accounts",
        title: "List accounts",
        description: "Shows the tailnet accounts signed in on this PC.",
        kind: "profile",
        via: "cli",
        interactive: false,
        command: "tailscale switch --list",
      }),
      action({
        id: "switch-account",
        title: "Switch account",
        description: "Makes another signed-in tailnet account the active one.",
        kind: "profile",
        via: "cli",
        interactive: false,
        params: [param("account", "Account or tailnet", PARAM_PATTERNS.ref, "example.github")],
        command: "tailscale switch {account}",
      }),
      action({
        id: "make-cert",
        title: "Make a TLS certificate",
        description: "Gets a certificate for this PC's ts.net name. HTTPS must be on in the tailnet's DNS settings.",
        kind: "credentials",
        via: "cli",
        interactive: false,
        params: [param("domain", "ts.net name", PARAM_PATTERNS.domain, "gala.tailnet-name.ts.net")],
        command: "tailscale cert {domain}",
      }),
      action({
        id: "funnel",
        title: "Share on a public URL",
        description: "Publishes a local port on this PC's ts.net address with Funnel. Anyone with the link can reach it.",
        kind: "domain",
        via: "cli",
        interactive: true,
        params: [param("port", "Local port", PARAM_PATTERNS.port, "17410")],
        command: "tailscale funnel {port}",
      }),
    ],
    sources: ["https://tailscale.com/kb/1080/cli", "https://tailscale.com/docs/aperture/mcp-server"],
  },
  {
    id: "adapter:nebius",
    slug: "nebius",
    name: "Nebius Token Factory",
    provider: "Nebius",
    category: "model",
    description: "Your Nebius Token Factory key: the first model route for Prometheus, Smith and the orchestra (OpenAI-compatible).",
    auth: {
      method: "token",
      env: "NEBIUS_API_KEY",
      tokenUrl: "https://tokenfactory.nebius.com/",
      credentialTarget: "4GE:adapter:nebius",
    },
    signUpUrl: "https://tokenfactory.nebius.com/",
    docsUrl: "https://docs.tokenfactory.nebius.com/",
    mcpNote: "A model route, not an MCP server: 4GE calls its OpenAI-compatible API at https://api.tokenfactory.nebius.com/v1.",
    actions: [],
    sources: ["https://docs.tokenfactory.nebius.com/"],
  },
  {
    id: "adapter:hivemind",
    slug: "hivemind",
    name: "Hivemind",
    provider: "Hivemind",
    category: "model",
    description: "Your own Hivemind: the second model route (model_invoke) when no Nebius key is set.",
    auth: {
      method: "token",
      env: "HIVEMIND_TOKEN",
      tokenUrl: "https://hivemind-public-production.up.railway.app/",
      credentialTarget: "4GE:adapter:hivemind",
    },
    signUpUrl: "https://hivemind-public-production.up.railway.app/",
    docsUrl: "https://hivemind-public-production.up.railway.app/",
    mcpNote: "Hivemind speaks MCP over streamable HTTP at the address in HIVEMIND_URL; 4GE uses its model_invoke tool as a model route.",
    actions: [],
    sources: [],
  },
];

export function findAdapter(id: string): AdapterDefinition | undefined {
  return ADAPTERS.find((entry) => entry.id === id || entry.slug === id);
}
