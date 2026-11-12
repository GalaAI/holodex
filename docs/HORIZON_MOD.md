# Horizon mod

Horizon is a gamified, autonomous task and project tracker: a calendar and a to-do list that reads the user's
connected data, turns it into missions, verifies them from evidence, and pays out in real unlocks. It is the RPG
of the work the user actually does. Lore is a skin; the product is getting things done.

Status, 2026-11-12. This page is design. What exists in code is listed under "What is built". Everything else
here is intent until it is built and proven.

## What is built

| Piece | State | Where |
|---|---|---|
| Provider interface, both modes | Built, tested | `Horizon.cs`, `HorizonProviders.cs` |
| Day window: calendar, checklist, missions log | Built, not yet seen on a real screen | `HorizonForm.cs` |
| Local log and checklist, per Windows user | Built, tested | `HorizonLocalStore.cs` |
| Google Calendar with the user's own OAuth | Built, sign-in not yet exercised live | `GoogleCalendarSource` |
| Ark calendar through Hivemind | Client built; needs GalaAI/ark#1868 and a PC release | `HorizonArkProvider` |
| Mission model, Ark's XP and quest math, source stubs | Built, tested; no source is wired yet | `HorizonMissions.cs` |
| Quests, verification, rewards, party, Today view | Design and a clickable prototype | this page |

## The UI reference

The owner's mockup is the Arkhive Mission Log (https://claude.ai/artifact/NfPje7gS9L4m6wWvRJ26fN). It is the
reference for Horizon's layout and rules, and it is the page meant to replace the home dashboard in Nexus. Where
this document and the mockup disagree, the mockup wins until the owner rules otherwise.

What the mockup already settles:

- **Layout.** A status HUD (operator level and XP, the tracked mission with its next step, the party of
  agents), the Horizon day strip (a scrubbable day with calendar events, missions due and AInbox deposits), then
  three views: Board, Sessions and AInbox, with a mission inspector beside them.
- **Mission types.** Campaign, Bounty, Gauntlet, Side quest, Lesson (solo or multiplayer). Each has a project,
  a track (development or learning), a priority P0 to P3, an assignee, an optional due or target date, an XP
  reward and an optional unlock.
- **Objectives and evidence.** A mission is a list of objectives. Each objective names the evidence it needs:
  PR merged, gate passed, receipt, owner ruling, or "Justin runs it".
- **The AInbox is the evidence path.** Everything that happened arrives as a deposit (human entry, Watcher
  finding, system feed, courier). A deposit is attached to an objective, then an evaluator accepts it. Only then
  is the objective proven.
- **Rules.** A Watcher finding observes and never closes a step. Only the operator's own entry closes an
  operator gate. The evidence kind must fit the step. A tick alone never grants XP.
- **No day checklist.** Missions are not scoped to a day and nothing carries over. A day shows what is due,
  what is waiting on the operator, and what was deposited.
- **Live data.** Sessions, the gate feed and the agent roster come from Hivemind; the calendar comes from the
  Google Calendar connector as a stand-in for Google Mod.

What this means for the code in this repo:

| Built here | Against the mockup |
|---|---|
| Day window with a free checklist and a missions log (`HorizonForm`) | The mockup has no checklist. This window is a stopgap and should be rebuilt as the HUD, day strip and Board |
| A self-reported tick pays 0.25 confidence (`HorizonXp.SelfReportConfidence`) | The mockup says a tick alone never grants XP; an operator entry is a deposit, not a tick. Needs a ruling |
| Level curve from Ark's `progression.ts` (`floor(sqrt(xp / 10)) + 1`) | The mockup uses 400 XP per level, labelled "proposed scoring". Needs a ruling |
| Mission sources propose and verify (`IHorizonMissionSource`) | Matches: a source's verification is a deposit with an evidence kind |
| Daily, boss and raid kinds | Not in the mockup. Proposed additions from the design pillars |
| Public mode (the user's own connections, state on this PC) | Not in the mockup, which is the owner's Ark view. Still required for public Holodex |

## The loop

Capture, plan, do, reward, reflect.

1. **Capture.** One tap from anything: a line typed into Horizon, a grab from the screen, a shared link. Horizon
   also proposes quests from connected data. The user accepts or dismisses each proposal; nothing is added
   silently.
2. **Plan.** A big task breaks into a questline. Steps are fitted into free calendar slots. The Today view shows
   at most 3 quests plus the dailies (`HorizonQuests.TodayLimit`).
3. **Do.** The user works, or hands the quest to an agent in their party.
4. **Reward.** Completion is verified from evidence, XP is minted with Ark's formula, and XP feeds unlock tracks.
5. **Reflect.** One morning brief, one evening recap, and a narrator agent that writes the day as a short journal
   chapter. No other notifications except real deadlines.

## Structures

| Name | What it is in plain words | Example |
|---|---|---|
| Campaign | A multi-step project that spans PRs or repos | Ship the Horizon mod |
| Bounty | One bounded fix with a clear finish line | A review request, a failing gate |
| Gauntlet | An assessed learning session; grants XP when passed | A graded task with a new tool |
| Side quest | Optional, low stakes, picked up when idle | Renew the domain |
| Lesson | Guild curriculum, solo or multiplayer | How a PR reaches main |
| Daily (proposed) | A habit | Inbox to zero |
| Boss (proposed) | A hard deadline or launch | Release day |
| Raid (proposed) | A shared goal with other people | A Huddl with a client |
| Party | The user's agents | Hand a quest to Smith; a PR comes back |

Quest titles are always plain ("Review PR 22"). The lore name is a label the user can turn down to nothing.

## Build on Ark, do not fork it

Horizon uses Ark's progression contract as it is (`@ark/shared-types` `progression.ts`). Holodex mirrors the
math in `HorizonXp` and `HorizonQuests` and tests it against Ark's numbers.

- **Quest states** are Ark's: offered, accepted, in_progress, submitted, evaluator_review, completed, failed,
  abandoned, with Ark's transitions. Nothing jumps to completed without the evaluator gate.
- **XP** is Ark's: `100 x difficulty (1 to 5) x quality (0 to 1) x evidence confidence (0 to 1) x novelty
  (0.25 to 1)`, at most 500, policy `HAXES-XP-1.0.0`. Quality uses Ark's five weights.
- **Levels** are Ark's: `floor(sqrt(xp / 10)) + 1`, capped at 100.
- **Rarity** is Ark's loot rarity score and tiers: common, uncommon, rare, epic, legendary, mythic.
- **The hard rule holds.** Progression is verified competence, never authority. An unlock makes a mod visible and
  installable. What that mod may do at runtime still goes through its connector permissions and owner approval.
  No level, tier or rarity is ever read as a permission.

Ark gaps this design depends on (checked in code on main, 2026-11-12):

- Ark's quest and XP routes exist but have no caller authentication and no Hivemind tool. They rely on the API
  binding to loopback.
- A new quest can be inserted directly in any state, including completed.
- Ark's `humanGrowthCurveNode` is the only schema for a person's XP; awards are otherwise per agent and Talent.
- Rarity is not derived anywhere for Omnitek items; only the loot math exists.
- The GitHub webhook does not keep `merged` or `merged_by`, so "merged" cannot be told from "closed".

## Mission sources

A mission source is a pluggable provider over the user's connected data (`IHorizonMissionSource`). It does two
things: propose quests, and verify that a quest's work happened.

Each source has a ready configuration stub (`HorizonSourceConfigs.Stubs`, example in
`docs/horizon/sources.example.json`). Every stub is off. A stub names a connection and never holds a key; a
file with anything that looks like a key in its settings is refused. Turning a source on is: sign in to the
connection, then set `enabled` in `%LOCALAPPDATA%\Holodex\horizon\sources.json`.

| Source | Mode | Proposes | Verifies |
|---|---|---|---|
| `capture` | both | captured notes | self-report only |
| `calendar` (Google) | public | events that need prep, deadlines | the event ended |
| `github` | public | review requests, assigned issues | PR merged, issue closed, review submitted |
| `ark-calendar` | Ark | events that need prep, deadlines | the event ended |
| `ark-agent-log` | Ark | none | an agent log entry with a PR or commit ref |
| `ark-missions` | Ark | open work orders | mission completed, PR opened |
| `ark-quests` | Ark | offered quests | evaluator accepted (needs an Ark change) |

Ark mode reaches all of these through Hivemind: Ark's own APIs plus the third-party adapters, services,
subscriptions and clients the owner has connected there. Public mode has only what the user's own connections
give it.

## How progress is verified

- **Evidence first.** Where a connected source can prove the work, it decides: a merged PR, an event that took
  place, a sent email, a confirmed booking, an agent log entry, a mission receipt. The user cannot tick those
  quests done by hand.
- **Self-report is a fallback and pays less.** A quest no source can see (a chore, a phone call) can be ticked.
  Its evidence confidence is capped at 0.25 (`HorizonXp.SelfReportConfidence`). A tick never raises the
  confidence of connected evidence.
- **No proof, no XP.** Confidence 0 mints 0.
- **Evaluator gate.** In Ark mode the evaluator is Ark (an `xpAwardRecord` accepted by an evaluator). In public
  mode the evaluator is the source's own answer, recorded with the quest.
- **Agent work counts as party work.** A quest handed to an agent completes on the mission receipt and the PR it
  delivered, and the award records the agent, not the user, as the one who did it.

## Rewards

- **Unlocks with real value.** Mod blueprints by tier and rarity, agent abilities, Holodex cosmetics, and "time
  back" automations that take a recurring chore off the list.
- **One starter inventory.** A fixed set of the owner's mods is there from day one. Rarer, more capable
  blueprints unlock through harder missions. Crafting your own mod unlocks after the premade ones.
- **Loot only on bosses and milestones.** Ordinary quests pay XP and nothing else, so loot keeps its meaning.
- **Forgiving streaks.** A streak bends before it breaks: a missed day spends a rest day instead of resetting.
- **Difficulty tuned to about 80 percent success.** If the user clears everything, quests get harder; if they
  clear half, quests get smaller.
- **Owner and developer bypass.** The owner can unlock anything for testing. A bypassed unlock is marked as one
  and mints no XP.

## Where state lives

| | Ark mode (signed in to Hivemind) | Public mode |
|---|---|---|
| Quests and XP | Ark (`ark_quest_records`, `ark_xp_awards`), once Ark exposes them | `%LOCALAPPDATA%\Holodex\horizon\` on this PC |
| Calendar | Ark's Horizon, through `horizon_read` | The user's calendar provider |
| Log and checklist today | This PC, labelled "ON THIS PC", until Ark has Horizon missions | This PC |
| Source sign-ins | The owner's Ark | Windows Credential Manager |
| Source configuration | `horizon\sources.json` on this PC | same |

The mode is `Horizon.ModeFor(HivemindClient.HasSavedSignIn)`, asked each time a day is read.

Stopgap, owner ruling 2026-11-12: a signed-in account keeps its log and checklist on this PC, and the window
says so, until Ark has Horizon missions. Both modes use the one local store, so there is one copy on the PC.
When Ark exposes missions, the local days are imported once and Holodex stops keeping them.

## Privacy

- A user's RAG data stays theirs. Sources read with the user's own access and results stay on this PC or, in Ark
  mode, in the owner's own Ark. Nothing is pooled, shared with other users, or used to train anything.
- Public mode makes no call to Omnitek or Hivemind.
- A source asks for the narrowest read scope its provider offers. Horizon never writes to a source without an
  explicit action by the user.
- Proposals are generated from titles and metadata first. Reading bodies (an email's text, a document) is a
  separate, per-source opt-in.
- Raids share only the quest and its state with the other people in it, never the evidence behind it.

## Anti-gaming

- Evidence beats self-report wherever evidence exists, and self-report is capped.
- XP is minted once per award id. A duplicate is refused.
- Novelty falls for repeated work of the same shape, down to Ark's floor of 0.25, so farming a trivial quest
  pays less each time.
- The actor on the evidence must be the user or an agent in their party. Someone else's merged PR is not proof.
- Evidence must fall inside the quest's window. An old PR does not complete a new quest.
- Splitting one task into many small quests does not raise the total: difficulty is set per questline.
- Quests the user writes for themselves cannot be bosses and drop no loot.
- Bypass and test unlocks are marked and mint nothing.

## Auth

- Public mode is the user's own OAuth, never a shared key. Holodex ships no provider client, key or account.
- Google Calendar: the user's own Google client from the Vault, PKCE S256, state check, loopback redirect,
  `calendar.events.readonly`, a grant separate from Drive.
- No secrets in the repo or in environment defaults. Tokens live in Windows Credential Manager.
- Ark mode adds no Hivemind scope: `horizon_read` rides on `runtime.read`.
- Horizon adds no shell action. Holodex shell actions stay loopback-only and token-gated.

## The surface

Native Phosphor, from the kit and tokens only, following the Shell Inventory rules that apply to a native
panel: bone is primary, ember is for gates, rust is for failures, touch rows are 44px, one scroll region. The
first slice is prototyped as a web page in the Holodex UI Lab look before any more native work: the Today view,
one-tap capture, auto-verified completions, XP feeding one unlock track, and agents as a party.

## What Ark must add

1. `horizon_read` (GalaAI/ark#1868): the calendar for an owner's client. Built, draft.
2. Horizon missions for an owner's client: authenticated quest reads and writes and XP reads as Hivemind tools,
   with a person as the principal. This replaces the home dashboard in Nexus with Horizon, per the owner's
   prototype and specs.
3. Evidence feeds Horizon can trust: `merged` on the GitHub webhook, and the agent log and mission receipts
   as they already are.
4. Caller authentication on the progression and legacy routes, and a guard on inserting a quest in a late state.

## Open decisions

- The order of public calendar providers after Google. Proposed: an ICS feed (no sign-in), then Microsoft.
- The starter mod list.
- Whether a tick alone can ever pay XP in public mode, where there is no operator deposit path yet.
- Which level curve is canon: Ark's `progression.ts` or the mockup's 400 XP per level.
- Whether daily, boss and raid join the five mission types.
- Which agent narrates the journal chapter.
