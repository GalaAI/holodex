---
name: dev-mod
description: The 4GE build loop on the user's PC. Use when Smith or a worker needs to know which model route is live, how to run and test a build locally, or how to bring it up in the Docker sandbox for a test flight.
license: Proprietary
compatibility: Runs inside the 4GE server on the user's PC (ports 17400 to 17412). Needs Docker Desktop for the sandbox.
metadata:
  mod: mod:dev
  owner: smith
---

# Dev loop

## Model routes

1. `GET /api/models` names the live route. Order: the user's Nebius Token Factory key (`NEBIUS_API_KEY`), then their
   Hivemind (`HIVEMIND_URL` and `HIVEMIND_TOKEN`), then none.
2. With no route, say so plainly and point the user to the Nebius adapter's connect action. Never invent output.

## Run and test a build

1. The build's app lives under the data root at `builds/<buildId>/app`. Work only inside it.
2. Use the starter's own scripts (`pnpm install`, `pnpm build`, `pnpm test`). Report failures with the command and the
   first failing line.
3. Bring the build up in the sandbox with `POST /api/sandbox/start`, then run a test flight with
   `POST /api/sandbox/test-flight`. Bots play the staff user and the customer user.

## Ports

4GE uses 17400 (server), 17401 (studio dev), 17410 to 17412 (sandbox). Never bind 5300, 8788, 4200, 15300, 18788 or
14200.
