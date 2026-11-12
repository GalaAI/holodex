# holodex-mcp

A local MCP server that lets the Holodex UI Lab drive the Holodex that is running on this PC, instead of reading
screenshots from CI. It speaks MCP over stdio to the Claude desktop app and one line at a time to Holodex's control
pipe, `\\.\pipe\ArkStation.Holodex.Control`.

It runs only on the PC Holodex runs on (Windows). It opens no port and has no network access of its own.

## What it needs

- Node 20 or newer.
- Holodex running with the developer pipe on: set `HOLODEX_DEV_PIPE=1` before starting it. A public install
  refuses every `ui:` command, so without the variable each tool answers
  `error:pipe:developer command; set HOLODEX_DEV_PIPE=1 to allow it`.

```powershell
$env:HOLODEX_DEV_PIPE = "1"
.\bin\Release\net10.0-windows\Omniwand.exe
```

## Install

```powershell
cd tools\holodex-mcp
npm ci
```

The one dependency, `@modelcontextprotocol/sdk`, is pinned to an exact version in `package.json` and
`package-lock.json`.

## Add it to the Claude desktop app

Open Settings, Developer, Edit Config (the file is `%APPDATA%\Claude\claude_desktop_config.json`) and add a server
named `holodex`, with the full path to `index.mjs` in your checkout:

```json
{
  "mcpServers": {
    "holodex": {
      "command": "node",
      "args": ["C:\\path\\to\\holodex\\tools\\holodex-mcp\\index.mjs"]
    }
  }
}
```

Restart the Claude desktop app. The server must be named `holodex`: the UI Lab artifact calls it as `host:holodex`.

## Tools

| Tool | Input | Output | Changes anything |
| --- | --- | --- | --- |
| `list_surfaces` | none | JSON array of surface names | no |
| `render_surface` | `{ "name": "holodex-mods" }` | the PNG as image content, then `{surface, width, height, map}` as text; `map` is the element map, or null for a surface that is painted and has no controls | no |
| `element` | `{ "surface": "holodex-mods", "id": "<element id>" }` | that control's live properties as JSON | no |
| `tune_get` | none | `{ "<token>": { "value": "240ms", "tuned": false } }` | no |
| `tune_set` | `{ "values": { "dur-disclosure": "320ms" } }` | `{ refused: [names], tuning: { ... } }` | yes, the tuning overlay |
| `tune_reset` | none | the tuning overlay after the reset | yes, clears the overlay |

The first four are annotated `readOnlyHint: true`. `tune_set` and `tune_reset` write
`%LOCALAPPDATA%\Holodex\tuning.json` through Holodex, which repaints at once; `tune_set` applies only the names
`tune_get` lists and a `null` value clears one override.

### Element map

`render_surface` returns the same map the as-built render writes beside each PNG (`<name>.json`):

```json
{
  "surface": "holodex-mods",
  "width": 466,
  "height": 680,
  "elements": [
    {
      "id": "Holodex/Holodex.Tabs/Holodex.Tab.mods",
      "name": "Holodex.Tab.mods",
      "type": "PhosphorButton",
      "x": 312, "y": 56, "w": 96, "h": 44,
      "depth": 2,
      "parent": "Holodex/Holodex.Tabs",
      "text": "Mods",
      "component": "button",
      "tokens": ["radius-btn", "touch-min", "text-size-btn", "color-bone", "color-line-2"]
    }
  ]
}
```

The ids and numbers above are an illustration, not a capture. Boxes are PNG pixels from the top-left of the render.
`id` is a path of control Names, with `Type#index` where a control has no Name. `text` is cut to 80 characters and is
empty for masked boxes and for every text box in the Vault.

`element` adds what the live control holds: `enabled`, `backColor` and `foreColor` as hex (`#rrggbb`, or `#rrggbbaa`
when not opaque), `font` (`family`, `size`, `unit`, `style`), `padding` (`left`, `top`, `right`, `bottom`) and
`tokenValues`, the current value of each token in `tokens`.

## What it can and cannot do

- Every tool is one of six fixed pipe commands: `ui:surfaces`, `ui:render`, `ui:element` and `holodex:tune:get`,
  `set`, `reset`. There is no tool that sends an arbitrary pipe command, runs a program or opens a shell.
- It reads two files per render, `<name>.png` and `<name>.json`, from `%TEMP%\Holodex\ui-lab`, at the paths it
  asked Holodex to write. Holodex refuses a `ui:render` path outside that folder, and the surface name is limited to
  lowercase letters, digits and dashes, so no tool input can name another file.
- Rendering draws offscreen. It does not open, focus or move a window, and each surface is put back in the state it
  was in.
- A pipe command is at most 1000 bytes, the most Holodex reads. A larger `tune_set` is refused; send fewer values.

## Check it by hand

With Holodex running, the same commands work from PowerShell without this server:

```powershell
$c = New-Object System.IO.Pipes.NamedPipeClientStream(".", "ArkStation.Holodex.Control", "InOut")
$c.Connect(3000)
$w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $w.WriteLine("ui:surfaces")
(New-Object System.IO.StreamReader($c)).ReadLine()
$c.Dispose()
```
