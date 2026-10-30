ThinkingBlock sits in the agent thread above the answer it produced. The caller owns the clock: pass seconds and tick it; the block only formats it (01:12 while live, "Thought for 12s" once done). thinking has no text yet; reasoning streams summary paragraphs, opens on its own and puts the StreamCaret on the last line; done folds to one quiet line with a grey edge because it is history, not a result; stopped keeps a rust edge and reads "Stopped after 8s". Leave open unset for that default, or control it with open and onOpenChange. The header leads with CadenceMark, so do not add another loader beside it.

```jsx
<ThinkingBlock
  state="reasoning"
  seconds={72}
  hint="reading 14 files"
  summary={["Read the loader.", "Checking tokens"]}
/>
```

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. Shows an agent's thinking as one disclosure line that streams a reasoning summary and folds to its duration when done.

### state

- `thinking`: No reasoning text yet. Patina edge with halo, live timer.
- `reasoning`: Summary lines stream with the caret; the block opens on its own.
- `done`: Folds to one line with the duration. Grey edge, no fill.
- `stopped`: Interrupted or cancelled. Rust edge with halo.

### Live hint (proposed)

- Font: mono, --font-mono --text-size-caption (11px). Casing: as-authored.
- Limit: none characters, 1 line(s). Overflow: truncate-ellipsis.
- What the agent is doing right now, verb first.
- Shown only while thinking or reasoning.
- Never: A live hint has no terminal punctuation or ellipsis.
- Good: "reading 14 files", "running typecheck"
- Bad: "Reading files..."
- Source: ThinkingBlock.tsx ThinkingBlockProps.hint

### Reasoning summary paragraph (canon)

- Font: body, --font-body --text-size-body (13px) / 1.6. Casing: sentence.
- Limit: none characters, any line(s). Overflow: wrap.
- One paragraph per entry; the last entry streams with the caret.
- The body scrolls past 220px instead of growing.
- Good: "Read the loader and its tokens."
- Source: ThinkingBlock.tsx ThinkingBlockProps.summary; thinking.css .rp-thinking__body

### Density and placement

- Glow means alive: the halo shows while thinking, reasoning or stopped, never once done. (canon; thinking.css header and data-state rules)
- One ThinkingBlock per agent turn. (proposed)
- Use for: Agent thread, directly above the answer it produced
- Not for: System work with no agent behind it (use CadenceDots in cream)
- Not for: Showing results or receipts (use RunCard)

### Tokens

- `--color-rust-ink`: Stopped label (shared across the console)
- `--highlight-top`: Panel top highlight (shared across the console)
- `--color-patina`: Live edge and halo (shared across the console)
- `--color-rust`: Stopped edge and halo (shared across the console)
- `--color-line-2`: Resting and done edge (shared across the console)
- `--color-surface`: Panel fill (shared across the console)
- `--color-bone`: Label and newest line (shared across the console)
- `--color-dim`: Done label and older lines (shared across the console)
- `--dur-bloom`: Edge, chevron and disclosure (shared across the console)
- `--ease-analog`: Every transition (shared across the console)

### Motion

- state change: edge colour and halo strength cross-fade (registered custom properties), --dur-bloom, --ease-analog (canon)
- open: body height grows from 0fr to 1fr, chevron turns 90 degrees, --dur-bloom, --ease-analog (canon)
- reduced motion: all transitions off, none (canon)

### Accessibility

- The header is a button with aria-expanded and aria-controls when a summary exists; with no summary and not live it is disabled.
- The label is aria-live polite and the clock uses role timer.
- The folded body is aria-hidden.

### Known gaps

- Panel radius 12 and edge ear radius 3 CSS pixels stay on the --u calc grid: no radius token matches them (12 equals --radius-menu-open, which is the menu morph value, not a panel radius). (thinking.css .rp-thinking__panel, .rp-thinking__halo)
