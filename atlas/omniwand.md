# Omniwand build entry

- **Item:** augment > component (Omniwand.exe); augment > extension > desktop-extension, host Logi Options+ (Logi MX Master 4 plugin)
- **Label:** Omniwand, kept by operator ruling 2026-10-15 and re-admitted in canon as the label of the Windows Wand Component
- **Capability expressed:** wand (the targeting capability; Omniwand is its native Windows surface, the Holodex Wand view is its web surface)
- **Shell worn:** Phosphor
- **Home:** `E:\ArkStation\augments\omniwand` (Omnitek rows in `INVENTORY.json`)
- **Classified:** 2026-10-15 from AGENTS.md canon rows `augment`, `component`, `extension`, `wand`, `machina`

## Why this classification

- Human-operated inside a host, never consumed by agents through Ark: Augment, not Mod.
- Hosts no Host or Automaton work; it dispatches to Input Studio, the Model Gateway and PC Modz: not a Machina.
- One executable mounting an overlay cursor, orb, ribbon and summoned widgets, equippable alone: Component, not Extension (no third-party host app), not Shell (it wears Phosphor), not Element or Artifact.
- The Logi plugin installs into Logi Options+ and is operated from inside it: Extension, archetype host-extension, sub-kind `desktop-extension` (the existing lexicon sub-kind for desktop host applications).

## Wand action mapping

| Wand action | Omniwand surface |
|---|---|
| point | `Overlay.Wand` |
| capture | `AInbox.CaptureButton`, `Wiz.AttachScreenToggle` |
| prompt | `Hud.PromptBox` |
| palette | `Overlay.FlowOrb` (eight GrimX Flow slots) |
| architect | Wiz (`WizCraftingWidgetForm.cs`) |
| annotate | none yet |

## Lifecycle

| Date | Tier | Evidence |
|---|---|---|
| 2026-10-14 | scaffolded | Source created under the augments home; CLAUDE.md written. |
| 2026-10-15 | implemented | `dist\Omniwand.exe` published 00:42 and running as PID 56412; `dist\ArkStationOmniwand.lplug4` packed; plugin folder present under LogiPluginService. |
| 2026-10-15 | implemented | Operator ruling: the native Options+ Actions Ring is the primary MX Master 4 path. Plugin made universal (`HasNoApplication => true`), duplicate code-side events removed, filler `.lp5` profiles deleted, lplug4 repacked; service log shows a clean load with 13 actions. Widgets rebuilt on PhosphorKit (token fonts, approved ArkMark geometry, SpecCard ticket for the handoff scroll); Esc, a real close button and `widgets:close` on the pipe close every widget. |
| 2026-10-16 | implemented | Ring path proven from physical presses: thumb button opens the native Options+ Actions Ring, slot presses log `Triggered haptic=... cmd=slot:spell`, `cmd=slot:ritual` and `cmd=ainbox:summon` in the plugin log, and the AInbox widget opened. Until then the actions did not appear in Global settings; three stale `@_arkstationomniwand` app-profile folders from the old app-bound build were the suspected cause, but they were never removed and still exist, so what made the actions appear is not established. |

Not validated: no automated tests; Input Studio may ignore the `model` field. The Actions Ring path is runtime-proven from physical presses (2026-10-16). Lifecycle never exceeds the evidence.

## Open

- Lexicon: add canon term `omniwand` (parent component, surface of wand) and retire the two "superseded by Wand" doc rows; in the registration PR.
- DRIVE_TOPOLOGY.md: list `augments\omniwand` beside `augments\extensions`; in the registration PR.
- Archetype: `custom` until a desktop-overlay archetype is ruled.
- Operator 2026-10-15: logo treatment and composer await the incoming component library; current ones are holding fixes.
