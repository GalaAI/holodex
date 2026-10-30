import { LAUNCH_MOD_ADAPTERS, type ModKind } from "@4ge/contracts";

/**
 * The three mods every user starts with (SPEC "Mods, adapters, augments").
 * A mod is agent-run: clicking it opens a Smith session about it.
 */
export interface ModDefinition {
  id: string;
  name: string;
  kind: ModKind;
  description: string;
  adapters: string[];
  augments: string[];
}

export const BUILT_IN_MOD_DEFINITIONS: ModDefinition[] = [
  {
    id: "mod:dev",
    name: "Dev Mod",
    kind: "dev",
    description:
      "Your build loop on this PC: the model routes the orchestra thinks with (Nebius, then Hivemind), Docker for the sandbox, and the dev loop skill.",
    adapters: ["adapter:nebius", "adapter:hivemind", "adapter:docker"],
    augments: ["augment:dev-mod-skill", "augment:docker-mcp-gateway"],
  },
  {
    id: "mod:pc",
    name: "PC Mod",
    kind: "pc",
    description:
      "This PC as 4GE sees it: which provider CLIs are installed and signed in, Docker, your tailnet, and the Windows Credential Manager store.",
    adapters: ["adapter:docker", "adapter:tailscale"],
    augments: ["augment:pc-detect", "augment:pc-mod-skill"],
  },
  {
    id: "mod:launch",
    name: "Launch Mod",
    kind: "launch",
    description:
      "Takes builds live with your own accounts: domains, projects, credentials and profiles on Northflank, Cloudflare, Google Cloud, Railway, Vercel, Supabase, Docker and Tailscale.",
    adapters: [...LAUNCH_MOD_ADAPTERS],
    augments: [
      "augment:launch-mod-skill",
      "augment:vercel-mcp",
      "augment:cloudflare-mcp",
      "augment:supabase-mcp",
      "augment:railway-mcp",
      "augment:gcloud-mcp",
      "augment:docker-mcp-gateway",
    ],
  },
];
