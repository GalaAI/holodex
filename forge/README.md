# 4GE (Forge)

4GE is the builder inside Holodex. You brainstorm an idea with Prometheus in Imagistation, get a Seed Scroll, and
build it in a full-screen IDE with Smith while the orchestra (Hephaestus, Athena, Metis, Icarus) builds the layers in
parallel. Read [SPEC.md](SPEC.md) first: it is the contract.

## Commands

Run these from `forge/` (or add `-C forge` from the repo root). Node 24 and pnpm 11.

| Command | What it does |
|---|---|
| `pnpm install` | Install the workspace |
| `pnpm build` | Build every package, then the studio and the server |
| `pnpm typecheck` | `tsc --noEmit` in every package |
| `pnpm test` | Vitest across the workspace (source, no build needed) |
| `pnpm dev` | Server on 17400 (tsx watch) and studio dev server on 17401 |
| `pnpm start` | The built server on 17400, serving the built studio |
| `pnpm e2e` | Playwright against the built server (run `pnpm build` first) |
| `pnpm bamf <validate\|plan\|run> <file>` | The .bamf CLI (after `pnpm build`) |

## Ports

Server `17400`, studio dev `17401`, sandbox app `17410`, sandbox CDP `17411`, sandbox viewer `17412`. 4GE never binds
5300, 8788, 4200, 15300, 18788 or 14200; `assertForgePort` in `@4ge/contracts` refuses them.

## Layout

```
packages/contracts  zod schemas, types, PORTS, the API route table, the typed client; dataRoot() at @4ge/contracts/node
packages/bamf       .bamf envelope (copied from @ark/bamf-schema), Blueprint planner, bamf CLI
packages/gate       stage gate and receipts (copied from @ark/shared-types stage-gate, scripts/4ge-stage-gate.mjs)
packages/kit        Phosphor tokens and React components (copied from @ark/tokens and @ark/ui), prompts/*.prompt.md
apps/server         Hono on @hono/node-server, SSE, JSON errors, layer route slots, serves apps/studio/dist
apps/studio         Vite + React 19 shell with lazy feature folders, API client hook, SSE hook
e2e                 Playwright config and the shell smoke test
```

## Conventions for builders

- **Contracts first.** Every shape that crosses a layer lives in `@4ge/contracts`. If you need a field, add it there
  (the foundation builder owns it; ask the coordinator) rather than redefining a type locally.
- **Source resolution.** Every package exports `"@4ge/source": "./src/index.ts"` beside its `dist` entry. Vite,
  Vitest, tsx (`--conditions=@4ge/source`) and `tsc` (`customConditions`) all read source, so nothing needs a build
  during development. Node packages use `NodeNext` and `.js` extensions in relative imports.
- **A new package** goes in `packages/<name>` with `package.json` (`"name": "@4ge/<name>"`, `"type": "module"`, the
  exports block above, `build` and `typecheck` scripts), a `tsconfig.json` that extends `../../tsconfig.base.json`, and
  tests as `src/**/*.test.ts`. Every dependency the layers need is already in the store and in the root
  `package.json`, so run `pnpm install --offline` once to link the new package.
- **Server routes.** Each layer owns `apps/server/src/routes/<layer>.ts` and fills `register(app, ctx)` with
  `implement(app, ctx, "<route name>", handler)` and `implementStream(...)` from `apps/server/src/implement.ts`. The
  helper takes method and path from the route table, parses the body and query, and checks the response against the
  contract. Routes no layer has implemented answer `501 not_implemented` in plain words. Publish live events with
  `ctx.events.publish(...)`; they stream on `/api/sessions/:id/stream` and `/api/builds/:id/stream`.
- **Data.** Ask `dataPaths()` from `@4ge/contracts/node` for every path. `FORGE_DATA_ROOT` overrides the root for tests.
- **Studio features.** Each feature owns `apps/studio/src/features/<id>/` and default-exports one component from
  `index.tsx`. Call the server with `useRoute(name, key, args)` or `api.call(name, args)` from `src/lib/api.ts`, and
  live events with `useRouteStream(...)` from `src/lib/sse.ts`. Compose from `@4ge/kit`; read the component's
  `prompts/<Name>.prompt.md` first. No raw hex, no em or en dashes, plain words.
- **Secrets.** Keys come from the environment Holodex starts the server with, or the OS credential store. Never log,
  return or commit one; `redact()` in the server strips anything that looks like a credential from error messages.

## API

All routes sit under `/api` (plus `/health` for Holodex's probe). The table, with request and response schemas, is
`ROUTES` in `packages/contracts/src/routes.ts`.
