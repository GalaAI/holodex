# Holodex live walkthrough (Nebius x NVIDIA Global AI Hackathon)

What to say while you drive the working build for someone live: a judge call, a Builders and Brews
table, a Discord screen share. This is not the video (that is `DEMO_SCRIPT.md`). Target 5 minutes,
with the aha inside the first 30 seconds and the rest cut to fit whatever time you get.

The repo, folder and exe still say Omniwand (the codename); if anyone asks, say
"Omniwand is the codename, the product is Holodex."

Built on the "Will It Ship?" judges' advice (Devpost email, 2026-10-21): get to the aha fast, put the
action first with only a little onboarding, show which model is doing what, and say the limitations
out loud.

## The working demo URL

Holodex is a native Windows app, so the demo URL is the download:
`https://github.com/GalaAI/omniwand/releases` (the `Omniwand-win-x64.zip` asset).

Two things must be true before anyone can use that link:

- The repo is still **private**. Flip GalaAI/omniwand to public.
- There is **no Release yet**. Publish one with the `dist` zip.

Until both are done, run the walkthrough from your own machine and give the link at the end.

## Before you start (2 minutes)

- Fresh start of `Omniwand.exe`. The Holodex connection line reads NEBIUS KEY LINKED // NEMOTRON READY.
- Close mail, chat and anything private. Plain wallpaper.
- Have these open, all public or fake:
  - a browser tab on a GitHub pull request with a visible diff
  - a terminal showing a short error
  - `bookings-sample.csv` (fictional rows)
  - a text file holding a FAKE token such as `ghp_demo0000000000000000000000`, never a real one
- Copy the PR URL to the clipboard (you paste it into AInbox later).
- Check the AInbox footer. If it says QUEUED, say "queued for AInbox" when you get there; do not
  claim it delivered.

## The walkthrough

### 0:00 to 0:30, the aha (do it, then explain it)

Put the cursor on the PR diff. Press **Alt+Space**, type *why would this hold the PR?*, press Enter.
Wait for the answer; the status line reads ANSWERED BY NEMOTRON 3.5 SUPER VL.

> "I pointed at a pull request and asked about it. No copy, no paste, no browser tab. The screen
> around my cursor went with the question, so NVIDIA's vision model read the pixels and answered.
> That line at the bottom says which model answered, every time."

### 0:30 to 0:50, what it is (the only onboarding)

> "This is Holodex: NVIDIA Nemotron at the tip of your cursor, on any Windows app, served by Nebius
> Token Factory. You download a zip, paste your Token Factory key once, and it lives in Windows
> Credential Manager, never in a file. From then on, every app on your PC has an AI that can see it."

### 0:50 to 1:30, which model does what

Keep the HUD answer on screen while you say this. Point at the model chip as you name each one.

> "It uses three Nemotron models, each sized to its job.
> Nemotron 3.5 Super VL whenever Holodex has to see, like that answer. The text models reject images,
> so anything with a picture goes to the vision model.
> Nemotron 3 Nano for anything that has to feel instant: quick text questions, chat, and ranking.
> Nemotron 3 Super for the agent, where it plans several steps and calls tools.
> One key, one API, and the app picks the size per task."

### 1:30 to 2:00, Lasso

Press **Alt+S**, lasso the terminal error, draw an arrow on it, click **Ask** (the button names the model).

> "When the whole screen is too much, I lasso exactly what I mean and mark it up. Only that region
> goes to the model."

### 2:00 to 3:15, AInbox

Press **Alt+I**. Drop the CSV, press **Ctrl+V** on the fake-token file, press **Link** (the PR URL
is on the clipboard), then **Pick** the diff. Wait for the cards to change from RANKED BY OMNITEK
to RANKED BY NEMOTRON 3 NANO.

> "AInbox takes everything at once: files, links, pieces of the screen. Every capture remembers where
> it came from, so they stack by context. Stack A is the repo, stack B is the export.
> First a fast local pass guesses what each stack should become. Then Nemotron Nano re-ranks it and
> writes down why. You can see the label flip to Nemotron.
> And look at this card: it found a GitHub token in that file. That is a fake one, but the gate is
> real. Secrets are redacted before anything leaves this machine."

Click **Fix it in a Pod** on stack A, open **Review**, then click the barcode.

> "This is the Handoff Scroll, what an agent will act on. Each field says where it came from:
> predicted, from the stack, or from Nemotron. The barcode is the SHA-256 seal of the contents;
> click it and the hash is copied. Send gives you five seconds to undo."

Press **Send**. Read the footer as it is: SENT if delivered, QUEUED if not.

### 3:15 to 4:00, Wiz

Press **Alt+W** and type *Schedule a summary of my open PRs every Friday.* The Spell chip flips to
Ritual. Send it.

> "Wiz is the agent. It picked a Ritual from the word schedule. It plans with Nemotron 3 Super, each
> step names its model, and when it wants to change something it stops and asks me first."

Approve the action row only if it appears. If the Wiz step does not run cleanly, skip to the next
section and say "Wiz is the newest part, I'll show it in the video."

### 4:00 to 4:30, limitations (say them, judges count this as rigor)

> "What it doesn't do yet. It's Windows only. Only text is scanned for secrets, not images or
> recordings. Record and Whisper are designed but not built; you can see them marked planned in the
> UI. Handoffs go to a local AInbox service, and without one they wait on disk and retry. And the
> first ranking pass is keyword rules; Nemotron is what makes it smart."

### 4:30 to 5:00, close

> "I tattoo for a living. For the past couple of months I've been building ArkStation, my own
> orchestration software, and I used it to build and ship this. Holodex is the tool for every job.
> It's Apache 2.0 on GitHub, and the download is on the Releases page."

Put the Releases URL on screen.

## If you only get 60 seconds

Do 0:00 to 0:30 exactly as written, then say:

> "Three Nemotron models on Nebius Token Factory: Super VL when it has to see, Nano when it has to be
> instant, Super for the agent. Every answer says which one did it. Windows only for now, and it's on
> GitHub."

## Likely questions

| Question | Answer |
|---|---|
| Why not just use a browser chat? | "The work isn't in the browser. Holodex sends what's on screen with the question, so there's no copy, switch, paste, explain." |
| Does my screen go to the cloud? | "Only when you ask, only the region around the cursor or what you lassoed, and text is checked for secrets first. It goes to Nebius Token Factory and nowhere else." |
| Did you fine-tune? | "No. Out of the box with prompt engineering: strict JSON contracts that the app validates, routing by modality, and tool schemas for the agent." |
| Why three models instead of one? | "Latency and cost. Nano answers while you're still building a stack, Super VL is the one that can see, and Super is worth the wait when the agent has to reason." |
| What happens with no key? | "It still launches and the cursor still works; asking a question shows a toast telling you how to add the key." |
| Mac or Linux? | "Not yet. It uses Win32 for the cursor and screen capture, so a port is a rewrite of that layer." |

## If something breaks live

- **The answer is slow:** "That's the vision model reading a full screen region; Nano answers text
  questions much faster." Then ask a text-only question to show it.
- **No answer, or a key error:** open Holodex (**Alt+H**) and show the connection line, then say what
  it reads. Do not fake an answer.
- **AInbox says QUEUED:** "The local AInbox service isn't running here, so it's queued on disk and
  will retry. That's the designed fallback."
