# 4GE is parked

Status: parked on 2026-10-30 by the owner's decision ("less is more"). This branch (`feat/4ge`) is not merged into
Holodex master and is not part of any release. Holodex master and v0.4.5 stay as they are.

## What is here

A working MVP of 4GE inside Holodex: Imagistation with Prometheus, Seed Scrolls, Smith and the orchestra in nested
sessions, the audit-board StageCanvas with Smith threads, tweak and annotation tools, mods and the Library grouped
by origin, and an Eden AEthernet lite sandbox with staff and customer bots. Unit and end-to-end tests pass with a
scripted model; real-model builds were not yet reliable (1 green of 4).

## How it comes back

When 4GE returns, it is rebuilt more cleanly, not resumed from this branch as is:

1. **Shared Ark packages, not copies.** Per the Reuse-First Law, 4GE consumes the shared packages instead of the
   copies in `forge/packages`: `@ark/bamf-schema` for .bamf envelopes, the shared stage gate
   (`@ark/shared-types` stage-gate) for receipts, and `@ark/tokens` and `@ark/ui` for design.
2. **Builds run through Ark** once Ark is integrated, instead of a separate local orchestra and sandbox.

This branch stays as a reference for the flow, the UX and the tests.
