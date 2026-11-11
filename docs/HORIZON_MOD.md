# Horizon mod

Horizon is the daily missions log, calendar and checklist. In Holodex it is one mod with two modes. This page is
the design; the code is `Horizon*.cs` at the repo root and `tests/Holodex.Tests/HorizonTests.cs`.

Status, 2026-11-11: the Holodex side is built. The Ark side that the Omnitek mode's calendar needs is a separate
pull request on `GalaAI/ark` (the `horizon_read` Hivemind tool). Ark has no missions log or checklist yet.

## Two modes, one mod

| | Omnitek account | Public Holodex |
|---|---|---|
| Who | A saved Hivemind sign-in | No Hivemind sign-in |
| Provider | `HorizonArkProvider` | `HorizonConnectedProvider` |
| Calendar | Ark's Horizon, through Hivemind | The user's own calendar provider, their own OAuth |
| Missions log | Ark's, when Ark has one | A file per day on this PC |
| Checklist | Ark's, when Ark has one | A file per day on this PC |
| Data lives in | Ark. Holodex keeps no copy | The user's calendar account and this PC |

## How the mode is chosen

`Horizon.ModeFor(HivemindClient.HasSavedSignIn)`. A saved Hivemind sign-in is Ark mode; none is the public mode.
The window asks again each time it reads a day, so signing in or out switches the mode on the next read.

The choice does not depend on whether Hivemind answers. A signed-in account whose Ark is unreachable sees that
state. It does not get a local Horizon, because a second copy would drift from Ark's. The two modes never mix:
the Ark provider has no local store, and the public provider never calls Hivemind.

## The provider interface

`IHorizonProvider` (`Horizon.cs`) is all the window talks to. Every call is for one calendar day:

- `ReadCalendarAsync(day)`
- `ReadLogAsync(day)`, `AppendLogAsync(day, text)`
- `ReadChecklistAsync(day)`, `AddCheckItemAsync`, `SetCheckItemAsync`, `RemoveCheckItemAsync`

Each answers with a `HorizonPart<T>`: a state, a value when ready, and one plain sentence otherwise. The states
are `Ready`, `NeedsSignIn`, `NeedsArkChange` and `Unavailable`. Nothing throws to the window and nothing falls
back to the other mode. The window draws each part on its own, so a calendar that is down still leaves the
checklist and log usable.

## The Ark provider

Holodex is a client of Ark's Horizon. It finds what Ark exposes by asking the signed-in Hivemind which tools it
lists (`HivemindClient.OffersToolAsync`), so parts light up as Ark ships them with no Holodex release.

- **Calendar.** Hivemind tool `horizon_read {day: "YYYY-MM-DD"}`, scope `runtime.read`, which Holodex already
  asks for. It returns Ark's day result (`horizonResultSchema` in `@ark/shared-types`): events, sources,
  `complete`. Holodex shows the events, names the account each came from, and says so when a source did not
  answer. If the tool is not listed, the calendar shows **needs an Ark change**. If Hivemind lists it but the PC
  refuses it (`unknown-runtime-operation` from an older PC node, or the operator bridge refusal from an older
  Ark API), the Ark on that PC has not adopted the release that carries it, and the card says that.
- **Missions log and checklist.** Ark has neither (checked in code, not docs). Both show **needs an Ark
  change**, and the entry box is off. Nothing is written anywhere.

Ark reads a day in its own Horizon time zone (`America/New_York` today). Holodex sends the PC's local date.

## The connected-services provider

- **Calendar.** `IHorizonCalendarSource`, one implementation: `GoogleCalendarSource`. It reads the primary
  calendar for the PC's local day with Google's `events.list`, asking only for id, title, status, link, start
  and end.
- **Missions log and checklist.** `HorizonLocalStore`: `%LOCALAPPDATA%\Holodex\horizon\YYYY-MM-DD.json`, so it
  is per Windows user. Writes replace the day's file in one step. A file that cannot be read is renamed
  `.unreadable`, not overwritten. These files hold the user's own notes and no secrets.

The next calendar provider implements `IHorizonCalendarSource` and nothing else changes.

## Auth

- **Public mode is the user's own OAuth, never a shared key.** Holodex ships no Google client, key or account.
  The user pastes their own Google OAuth client into the Vault (the flow Drive already uses), then connects the
  calendar from Horizon. The sign-in is PKCE S256 with a state check and a loopback redirect
  (`http://127.0.0.1:47613/horizon/google/`).
- **Least scope, separate grant.** The calendar asks for `calendar.events.readonly` only, and its tokens live
  under their own Credential Manager target (`Holodex:Vault:google-calendar`). Connecting the calendar does not
  widen the Drive grant, and Horizon cannot write to a calendar.
- **No secrets in the repo or in env defaults.** Tokens live in Windows Credential Manager. Horizon adds no
  environment variable and no default credential.
- **Ark mode adds no scope.** It uses the existing Hivemind sign-in and `runtime.read`.
- **Shell actions are unchanged.** Horizon adds no shell action and no listener beyond the loopback OAuth
  redirect. It opens only `https` event links, in the browser. Holodex shell actions stay loopback-only and
  token-gated.

## The surface

A button on the drawer's Mods tab opens `HorizonForm`, a window like the Vault. It is native Phosphor, built
from the kit only: `WidgetChrome`, `ThreadCard`, `PhosphorCheckBox`, `PhosphorField`, `PhosphorButton`,
`EyebrowLabel`, the scroll beam. Colors, spacing and type come from `PhosphorTheme` tokens; there are no literal
colors. It follows the Shell Inventory rules that apply to a native panel: bone is primary, ember is for gates
only (the "needs an Ark change" cards), rust is for failures, touch rows are 44px, and there is one scroll
region. The eyebrow ends in `ARK` or `THIS PC`, so the mode is always on screen.

Layout, top to bottom: the day strip (Earlier, Later, the date, Today), one status line, the day (Calendar,
Checklist, Missions log), and one entry line. Enter adds a checklist item; Shift+Enter writes to the log.

## What Ark must add

1. **`horizon_read`** (in the Ark pull request): a read-only Hivemind tool over `GET /api/horizon`, under
   `runtime.read`. It needs the tool in `hivemind-core`, the route on the Desktop Node relay allowlist, and
   `/api/horizon` accepting the PC capability assertion as well as the operator bridge. No new scope or
   credential. The owner's PC must adopt the release before it works end to end.
2. **A missions log and a checklist** (not started, needs a decision): a data model, storage, routes, and
   Hivemind tools for reads (`runtime.read`) and writes (`runtime.execute`). ArkStation's own Horizon panel
   needs the same data, so Ark should define it once and Holodex should follow.

## Not in this version

- A month view. Ark already serves month markers (`horizon_read {month}`); Holodex reads days only.
- Calendar providers other than Google.
- Carrying unticked checklist items to the next day.
