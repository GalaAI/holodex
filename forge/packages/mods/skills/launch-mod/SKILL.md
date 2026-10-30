---
name: launch-mod
description: Takes a finished 4GE build live with the user's own accounts. Use when a build's test flight is green and the user wants it online, needs a domain, a hosting project, credentials or a deploy profile on Northflank, Cloudflare, Google Cloud, Railway, Vercel, Supabase, Docker or Tailscale.
license: Proprietary
compatibility: Runs inside the 4GE server on the user's PC. Needs the provider CLIs the user picks; the server reports which are installed and signed in at GET /api/adapters.
metadata:
  mod: mod:launch
  owner: icarus
---

# Launch runbook

You are helping the user put a build online with **their own** accounts. 4GE never holds their passwords and never
stores a token anywhere but Windows Credential Manager (targets that start with `4GE:`).

## Steps

1. Read the adapters: `GET /api/adapters`. Each one says whether its CLI is installed (`cli.installed`), whether it is
   signed in (`status` is `connected`), and the actions it offers.
2. Pick a host with the user. Plain guide:
   - A static site or a Next.js app: Vercel or Cloudflare Pages.
   - A server with a database: Railway or Northflank, with Supabase for Postgres if they want a managed database.
   - Google Cloud when the user already lives there.
   - Tailscale Funnel only to show a build from this PC for a short while.
3. If the adapter is not connected, offer its `sign-in` action. It opens the provider's own CLI in its own window and
   the user signs in in their browser. If the CLI is missing, give the install command from `cli.install`. If the user
   has no account, give `signUpUrl`.
4. Run the project action (`create-project`, `create-pages-project`, ...) with
   `POST /api/adapters/<id>/actions/<action>` and `params`. Pass `params.buildId` to run it in the build's app folder.
5. Domains: `buy-domain` and `register-domain` spend money. Say the price comes from the provider, ask the user, and
   send `confirm: true` only after they say yes. The provider's CLI asks again in its own window.
6. Credentials: actions such as `add-env` and `put-secret` open the CLI so the user types the value there. Never ask
   the user to paste a secret into the chat.
7. Record what you did in the build's receipt: the adapter, the action, the command shown in the run result, and the
   result message. Never copy CLI output that holds a key.

## Edge cases

- `status: refused` means a parameter failed its pattern or a spending action had no confirm. Fix the input; do not retry blindly.
- A CLI that is installed but not signed in answers `disconnected`. Do not run project actions until it is connected.
- Docker can be installed but stopped: start Docker Desktop before the sandbox or any image build.
