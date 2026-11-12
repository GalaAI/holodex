# Holodex UI Lab

The UI Lab is one page that shows every Holodex surface as it is actually built. It lists the projects ArkStation
knows on an inventory of folders, opens a project onto a pannable board of its renders, and lets you focus one render
and inspect its elements: which component drew each one and which tokens it is built from. Tokens can be tuned in the
page, a tweak is held as a pending change, and a request can be handed to an agent through Hivemind. When the page runs
inside the Claude app on the PC that runs Holodex, it can also drive the running app through `tools/holodex-mcp`.

It is published as a claude.ai artifact: https://claude.ai/artifact/JE79vL8Ad5GHo1qKjaCNkP

This folder is the source of that page. Before it was saved here the source lived only in a cloud session's scratch
folder.

## Layout

| Path | What it is |
| --- | --- |
| `src/body.html` | The page body and the lab's own CSS. |
| `src/app.js` | The lab's behaviour. |
| `src/data.js` | Surface groups, components and the tunable token list. |
| `src/emblem.json` | ArkMark path data used for the header mark. |
| `src/build.py` | The build. |
| `src/prep.py` | Turns a downloaded `ui-renders` artifact into `renders/`, `sizes.json` and `maps.json`. |
| `sizes.json` | Pixel size of each render. |
| `maps.json` | Names of the renders that have an element map. |
| `tokens.json` | Token values the lab shows and tunes. |
| `files.json` | The files published beside the page, by published path (`r/<name>`). |
| `renders/` | The render PNGs and their element maps (`<name>.json`). |
| `template.html` | Build output with the data placeholders left in. |
| `dist/index.html` | Build output: the page that is published. |
| `checks/open-project.js` | Playwright check of the built page. |

## Build

The build needs Python 3 and an Ark checkout, because it copies the `@ark/tokens` and `@ark/ui` CSS unchanged from
`packages/tokens/src` and `packages/ui/src`.

```
python tools/ui-lab/src/build.py
python tools/ui-lab/src/build.py --ark D:\path\to\ark
```

`--ark` defaults to `../ark` beside the holodex checkout. The Ark checkout must be at a revision that has every CSS
file the build lists; current `origin/main` does. `--ark-rev` sets the revision label written into the CSS comments;
by default it is the short HEAD of the Ark checkout. All other paths are relative to the script, so it runs from any
directory.

The build writes `template.html` and `dist/index.html`, then prints the page size, any long dashes found, and the
literal colours and pixel sizes in the lab's own code.

The committed `dist/index.html` was built from Ark `6d833013`. Building against that revision reproduces it byte for
byte.

## Check

```
node tools/ui-lab/checks/open-project.js
```

It loads `dist/index.html`, clicks `[data-open-project=holodex]`, and passes when `.rp-stage` is visible, `#inventory`
is hidden and the page raised no errors, once with motion and once with reduced motion. It needs the `playwright`
package to be resolvable. The renders do not load from `dist/` on disk, because the page asks for them at `r/`; that
does not affect the check.

## Publish

Publish `dist/index.html` to the existing artifact so the link stays the same. The page expects:

- its renders and element maps beside it at `r/<name>.png` and `r/<name>.json`. `files.json` lists every published
  path; each one comes from the file of the same name in `renders/`.
- two artifact capabilities, both of kind `mcp`: the `Hivemind` connector, used to ask an agent and dispatch a
  mission, and `host:holodex`, the local server in `tools/holodex-mcp`, used to drive the running app. The host server
  is listed only inside the Claude app on a PC that has it configured; everywhere else the lab shows the CI renders.

## Where the renders and element maps come from

They come from the `ui-renders` artifact of the "UI renders" workflow (`.github/workflows/ui-renders.yml`). That
workflow builds Holodex, starts it on the runner's desktop and asks it over its control pipe to draw each surface and
write its element map. To refresh them, download the artifact and run:

```
python tools/ui-lab/src/prep.py <downloaded folder> tools/ui-lab/renders tools/ui-lab/sizes.json tools/ui-lab/maps.json
```

Then rebuild. If a render was added or removed, update `files.json` to match.

## Open items

- FolderPocket (CP-36), the folder used on the inventory, is a Concept in the Shell Inventory and is awaiting
  promotion. The lab's copy is built to that entry and should be replaced by the exported part once it is promoted.
- The live paths are unverified. The `host:holodex` bridge and the Hivemind ask and mission calls have not been proven
  end to end against a running Holodex and the deployed Hivemind.
- Five project placements need Justin's confirmation. Only Holodex publishes renders today; the other projects sit on
  blueprint rows assigned from what Hivemind and Railway report, as recorded in `PROJECTS` in `src/app.js`.
