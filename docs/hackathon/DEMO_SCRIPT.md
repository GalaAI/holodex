# Holodex demo video (Nebius x NVIDIA Global AI Hackathon)

Target: 2:50, public YouTube, 1920x1080, voice on every beat. Track: Best Apps and Agents.
Deadline: Friday 2026-10-30, 10:00 AM PT. Holodex is one of three separate entries (with Hivemind and
Ryozan); this video shows only Holodex.

Judges asked for: the aha in the first 30 seconds, which model does what, a little onboarding,
limitations stated plainly, and Nebius Token Factory plus Nemotron named out loud.

## The family line (say it the same way in all three videos)

ArkStation is Justin's personal orchestration software, built over the past couple of months. To show
what it can do, Justin used ArkStation itself to build these apps end to end and publish them to
GitHub: Gala plans the work, dispatches missions to Ark agents, and each step leaves a receipt. Each
entry takes one part of ArkStation and stands on its own:

- **Hivemind** is the brain that connects AI together, under the orchestration of Gala, running on
  NVIDIA models.
- **Ryozan** is one unified interface for managing your business operations.
- **Holodex** is the tool for every job.

Each video names its own app first and uses the family line once, briefly, so the three entries stay
clearly different.

## Model routing to say on camera

| Where | Model on Token Factory | Why this size |
|---|---|---|
| HUD text questions, Holodex chat, AInbox re-ranking | Nemotron 3 Nano 30B A3B | fast, cheap, every keystroke-adjacent call |
| Anything Holodex has to see (HUD with the screen, Lasso, Pick) | Nemotron 3.5 Super VL | the vision model; text Nemotrons reject images |
| Wiz agent steps (tool choice, plans) | Nemotron 3 Super 120B A12B | multi-step reasoning and tool calls |

The UI names the model in every case: the model chip, "ANSWERED BY ..." on the HUD, "RANKED BY NEMOTRON 3
NANO" on AInbox cards, and the model on each Wiz step.

## Before you record

- Fresh start of `dist\Omniwand.exe`; the Holodex connection line reads NEBIUS KEY LINKED // NEMOTRON READY.
- Close mail, chat and anything private. Plain wallpaper.
- Props, all public:
  - a browser tab on a GitHub pull request with a visible diff
  - a terminal showing a short error
  - a small CSV (`bookings-sample.csv`, fictional rows)
  - a text file containing a FAKE token such as `ghp_demo0000000000000000000000` (never a real one)
- Check the AInbox footer before recording. If it says QUEUED because the AInbox service is still
  rejecting saves, either fix the service first or say "queued for AInbox" on camera; do not claim it
  delivered.
- OBS: display capture 1080p60, mic at -12 dB, cursor capture on (the Aperture cursor is part of the show).

## Shot list

| Time | On screen | Say |
|---|---|---|
| 0:00 to 0:15 | The Aperture cursor moving over the desktop, morphing over a link and a text field | "Your AI lives in a browser tab. Your work lives everywhere else. Holodex is the tool for every job: it puts NVIDIA Nemotron at the tip of your cursor, on any Windows app, through Nebius Token Factory." |
| 0:15 to 0:45 | Alt+Space over the PR diff. Ask "why would this hold the PR?" Enter. Status reads ANSWERED BY NEMOTRON 3.5 SUPER VL | "Point and ask. The screen around the cursor goes with the question, so Nemotron 3.5 Super VL reads the pixels, and Holodex tells you which model answered." |
| 0:45 to 1:05 | Alt+S, lasso the error in the terminal, draw an arrow, Ask Nemotron | "Lasso anything, mark it up, and ask about exactly that region." |
| 1:05 to 1:50 | Alt+I. Drop the CSV, Ctrl+V the fake-token file, press Link with the PR URL copied, then Pick the diff. The stack groups into A (the repo) and B (exports). Cards rank intents; after a beat they read RANKED BY NEMOTRON 3 NANO with a WHY and a synthesis. The Aegis card says a GitHub token was found and will be redacted | "AInbox takes everything at once. Every capture records where it came from, so they stack by context. Omnitek ranks what each group should become, and Nemotron Nano re-ranks it and explains why. Aegis caught a token and redacts it before anything leaves." |
| 1:50 to 2:15 | Click Fix it in a Pod on A, open Review: fields tagged PREDICTED, FROM STACK A, NEMOTRON. Click the barcode; toast says seal copied. Send A, the 5 second undo, the footer status | "The Handoff Scroll adapts to the intent, shows where each field came from, and is sealed: that barcode is the SHA-256 of what the agent will act on. Five seconds to undo, then it goes to the pod's inbox." |
| 2:15 to 2:40 | Alt+W. "Schedule a summary of my open PRs every Friday." The Spell chip flips to Ritual. Send to Wiz. The thread shows Nemotron 3 Super steps and an action row asking to approve a Codex save; approve | "Wiz is the agent. It picked a Ritual from the word schedule, plans with Nemotron 3 Super, and asks before it changes anything." (Reshoot this beat after the Wiz agent build lands.) |
| 2:40 to 2:50 | README limitations section | "Windows only. Images are not scanned for secrets yet, and Record and Whisper are planned." |
| 2:50 to 2:58 | ArkStation mark, then the repo URL | "I've been building ArkStation, my personal orchestration software, for the past couple of months, and I used it to build and ship this. Hivemind is the brain, Ryozan runs the business, and Holodex is the tool for every job. It's Apache 2.0 on GitHub." |

## After recording

- Cut to 2:58 or less (the limit is 3:00). Captions on.
- Upload public on YouTube; title "Holodex: Nemotron at your cursor (Nebius x NVIDIA hackathon)".
- Paste the URL in the Devpost submission.

## 45 to 60-second cut: ArkStation and its three apps (social, optional)

| Time | Picture | Line |
|---|---|---|
| 0 to 8s | ArkStation running: Gala dispatching a mission, the receipt landing in a Hivemind session | "I tattoo for a living. For the past couple of months I've been building ArkStation, my personal orchestration software, and to show what it can do I used it to build and publish these three apps." |
| 8 to 20s | Hivemind: sessions and receipts with several agents in one thread | "Hivemind is the brain. It connects AI together under the orchestration of Gala, running on NVIDIA models." |
| 20 to 32s | Ryozan: bookings, client messages and the studio portal in one screen (only once Ryozan is built) | "Ryozan is one unified interface for managing your business operations." |
| 32 to 50s | Holodex: Alt+Space ask and answer, a lasso, a drop into AInbox with the ranked cards | "And Holodex is the tool for every job: NVIDIA Nemotron at your cursor, on any app, and it shows which model did what." |
| 50 to 58s | ArkStation mark, then the three repo names | "All three run on NVIDIA Nemotron through Nebius Token Factory." |
