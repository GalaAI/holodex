# Mod manifest proposal

A plug-and-play Mod is one file that says what the Mod brings, what it needs, how rare it is and how it is
earned. This is a proposal. It builds on Ark's `integrationRecordSchema` (`packages/mod-builder-core`, exported
as `ModManifest`) and lists what that schema cannot say today. Nothing here is built.

An example is in `docs/horizon/mod-manifest.example.json`.

## What a Mod declares

| Part | Meaning | In Ark's schema today |
|---|---|---|
| Identity | id, display name, role, lore alias (display only) | Yes: `id`, `displayAlias`, `technicalRole`, `registryProfile.loreAlias` |
| Tools | What it can do, each read, write, execute or listen | Yes: `capabilities[]` |
| MCPs and plugins | The servers and plugin interfaces it brings | Partly: `registryProfile.composition.interfaces`, `runtime.type` |
| Extensions | Host extensions it installs (a browser extension, an editor extension) | No |
| Logic | Skills, prompts and flows it ships | No. Only as free `metadata` |
| Required connections | The accounts it needs, and the scopes on each | Partly: one `auth` block and `vaultRefs`; not a list of connections |
| Policy | Which actions are gated and which are fast path | Yes: `aegisPolicy` |
| Distribution | Where it can be installed and who can see it | Yes: `distribution` |
| Tier | How proven it is: planned through runtime-proven | No. `omnitekItemTier` exists in `packages/lexicon`, not on the manifest |
| Rarity | common through mythic | No. Loot rarity math exists in `progression.ts`; nothing derives it for a Mod |
| Unlock condition | What must be true before it is visible and installable | No. A quest has `unlockRefs`, but nothing reads them |

## Proposed additions

Four blocks, added beside the existing fields. None of them grants anything.

```jsonc
{
  "connections": [
    { "id": "github", "provider": "github", "required": true,
      "scopes": ["repo:read"], "purpose": "Read review requests and merged pull requests" }
  ],
  "logic": [
    { "id": "review-brief", "kind": "skill", "ref": "skills/review-brief/SKILL.md" },
    { "id": "morning-brief", "kind": "flow", "ref": "flows/morning-brief.bamf" }
  ],
  "extensions": [
    { "id": "holodex-panel", "host": "holodex", "kind": "panel", "ref": "panels/review-queue" }
  ],
  "progression": {
    "tier": "validated",
    "rarity": { "tier": "rare", "derivedFrom": "loot-rarity-1", "score": 0.61 },
    "unlock": {
      "starter": false,
      "anyOf": [
        { "kind": "quest-completed", "questRef": "quest:horizon.first-boss" },
        { "kind": "talent-level", "talentRef": "talent:code-review", "minLevel": 10 }
      ],
      "bypass": ["owner", "developer"]
    }
  }
}
```

- **`connections`** is a list. Each entry names a provider, the narrowest scopes, and why. It never holds a key.
  This is what makes a Mod plug in: Horizon can show "needs GitHub" and offer the sign-in.
- **`logic`** names the skills, prompts and flows by path. They are files in the Mod's package, not inline text.
- **`extensions`** names what the Mod adds to a host: a panel, a tray item, a browser or editor extension.
- **`progression.tier`** uses Ark's item tiers: planned, scaffolded, implemented, validated, integrated,
  runtime-proven. It is earned from receipts, not typed in. Until Ark derives it, the manifest carries the last
  value a gate computed.
- **`progression.rarity`** is Ark's loot rarity tier, with the score and the policy that derived it.
- **`progression.unlock`** says when the Mod becomes visible and installable. `starter: true` puts it in the
  fixed starter inventory. `bypass` lists who may unlock it for testing; a bypassed unlock is marked and mints
  no XP.

## The rule that does not move

Unlocking is not permission. An unlocked Mod is visible and installable. Every action it takes at runtime still
goes through its connections' scopes, its `aegisPolicy` gates and owner approval. `progression` is never read by
anything that grants access. This is Ark's hard rule in `progression.ts`, and the manifest keeps it by putting
progression in its own block that the permission path does not read.

## Gaps in Ark to close before this is real

1. `integrationRecordSchema` has no `connections`, `logic`, `extensions` or `progression`. Today they would have
   to sit in free `metadata`, which nothing validates.
2. Tier is a stored field with no derivation from receipts. The Omnitek panel keeps its own tier ladder.
3. Rarity is marked "derived" in `packages/lexicon` but no function derives it.
4. `unlockRefs` on a quest is a list of ids that nothing interprets. There is no unlock ledger.
5. `auth` is single. A Mod that needs two providers cannot say so.
6. No JSON manifests are checked in; records are generated from seed scripts. A plug-and-play Mod needs a file
   format and a loader.
7. The Omnitek item type list differs between the panel and `packages/lexicon`.

## Starter inventory

A fixed set of the owner's mods, each with `unlock.starter: true`. The list is the owner's to choose. Candidates
already in Holodex: Vault and adapters, Mouse Mod Station, Augment Inventory, Spells, AInbox, Horizon.
