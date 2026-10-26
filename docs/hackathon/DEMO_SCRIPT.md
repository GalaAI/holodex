# Omniwand demo video (Nebius x NVIDIA Global AI Hackathon)

Target: 2:50, public YouTube, 1920x1080, voice on every beat. Track: Best Apps and Agents.
Deadline: Friday 2026-10-30, 10:00 AM PT.

Judges asked for: the aha in the first 30 seconds, which model does what, a little onboarding,
limitations stated plainly, and Nebius Token Factory plus Nemotron named out loud.

## Before you record

- Fresh boot of Omniwand from `dist\Omniwand.exe`; Nebius key linked (Holodex badge reads
  "Nemotron ready").
- Close Discord, mail and anything with private data. Desktop wallpaper plain.
- Open three props: a browser tab with a busy chart or dashboard, a short error message in a
  terminal, and a PDF (any public report) on the desktop.
- OBS: display capture 1080p60, mic at -12 dB, cursor capture on (the Aperture cursor is part of
  the show).
- Model routing to say out loud: Nemotron 3 Nano for fast text answers (HUD without the screen,
  Wiz, Holodex), Nemotron 3.5 Super VL for anything it has to see (Lasso, HUD with the screen
  attached). The HUD status line names the model that answered.

## Shot list

| Time | On screen | Say |
|---|---|---|
| 0:00 to 0:20 | Cursor moving over the desktop; the Aperture cursor morphs over a link and a text field | "Your AI lives in a browser tab. Your work lives everywhere else. Omniwand puts NVIDIA Nemotron at the tip of your cursor, on any Windows app, through Nebius Token Factory." |
| 0:20 to 0:50 | Alt+Space over the chart. Type "what is the trend here and what should I check?" Enter. Answer appears; the status line reads ANSWERED BY NEMOTRON 3.5 SUPER VL | "Point and ask. The screen around the cursor goes with the question, so Nemotron 3.5 Super VL answers, and Omniwand tells you which model did." |
| 0:50 to 1:25 | Alt+S, lasso the error text, draw an arrow, Ask Nemotron | "Lasso anything, mark it up, and Nemotron 3.5 Super VL reads the pixels, not a guess from text." |
| 1:25 to 1:55 | Alt+W, type "schedule a summary of my open PRs every Friday". Spell chip flips to Ritual. Craft | "Wiz turns a goal into a Spell. It read 'schedule' and picked a Ritual, a recurring chain, on its own." |
| 1:55 to 2:30 | Drag the PDF onto Alt+I. The well spreads, the TraceBeam runs. Tab takes the suggestion. Open Review: the barcode. Send, the 5 second undo, the receipt | "AInbox hands work to agents. Drop a file; Omnitek classifies it and picks the inbox. The Scroll is sealed: that barcode is the SHA-256 of what the agent will act on. Five seconds to undo, then it's sent." |
| 2:30 to 2:45 | Architecture card (one slide): Omniwand (.NET 10) to Token Factory chat completions to Nemotron Nano, Super, Super VL; key in Windows Credential Manager | "Native .NET 10. One OpenAI-compatible API, Token Factory. Keys stay in Windows Credential Manager, never in a file." |
| 2:45 to 2:55 | README limitations section, repo URL | "Windows only, one capture per handoff today, and the receiving side does not verify the seal yet. It's Apache 2.0: github.com/GalaAI/omniwand." |

## After recording

- Cut to 2:55 or less. Captions on.
- Upload public on YouTube; title "Omniwand: Nemotron at your cursor (Nebius x NVIDIA hackathon)".
- Paste the URL in the Devpost submission.
