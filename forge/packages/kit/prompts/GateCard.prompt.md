GateCard is raised by a RunCard step at a gate and nothing runs until the operator decides. source ties it back to that step ("Gate // 03 // ARK://4GE // BUILD LOADER SET"); title says what is being approved in plain words; summary says what happens on approve. changes lists touched files with added and removed counts, checks show a small LED each, and risk tags read ember for write and rust for destructive. A unified diff sits behind Show diff. While pending the footer offers Reject, Hold and Approve (one primary, Approve); held offers Reject and Resume review; once decided the footer names who and when through decided. Pass only the handlers you support: missing handlers hide their buttons.

```jsx
<GateCard
  source="Gate // 03 // ARK://4GE"
  title="Write the loader set"
  summary="Writes 4 files. Nothing runs until you approve."
  state="pending"
  onApprove={approve}
  onHold={hold}
  onReject={reject}
/>
```

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. Asks the operator to approve, hold or reject one gated change, showing what changes, the checks and the risk.

### state

- `pending`: Waiting on the operator. Ember edge and halo, ember hatch.
- `held`: Paused by the operator. Grey edge, faint hatch.
- `approved`: Moss edge, unlit, no hatch.
- `rejected`: Rust edge, unlit, rust hatch.

### Gate source (canon)

- Font: mono, --font-mono 10px, 0.16em. Casing: as-authored.
- Limit: none characters, 1 line(s). Overflow: wrap.
- Gate, the step number and the run address, joined by //.
- Good: "Gate // 03 // ARK://4GE // BUILD LOADER SET"
- Source: GateCard.tsx GateCardProps.source

### Gate title (proposed)

- Font: ui, --font-ui 800 16px. Casing: sentence.
- Limit: none characters, 2 line(s). Overflow: wrap.
- What the operator is approving, in plain words.
- Never: State the change; the buttons already ask the question.
- Good: "Write the loader set"
- Bad: "Should I write the loader set?"
- Source: GateCard.tsx GateCardProps.title

### Gate summary (canon)

- Font: ui, 14px / 1.55, max 62ch. Casing: sentence.
- Limit: none characters, 2 line(s). Overflow: wrap.
- One or two sentences: what happens on approve, and that nothing runs until then.
- Good: "Writes 4 files. Nothing runs until you approve."
- Source: GateCard.tsx GateCardProps.summary

### Density and placement

- Only pending glows; held, approved and rejected are unlit. (canon; gate-card.css header)
- Hatch as state: ember while pending, rust when rejected, faint bone while held, none once approved. (canon; gate-card.css header and .rp-gate-card__hatch rules)
- One primary action, Approve. (canon; GateCard.tsx footer)
- Use for: Opened from RunCard Review gate
- Use for: Approval queues
- Not for: Routine confirmations with no change set (use a Button)

### Tokens

- `--color-rust-ink`: Light rust text: removed lines, destructive tag, Reject (shared across the console)
- `--highlight-top`: Panel top highlight (shared across the console)
- `--color-ember`: Pending edge, halo, hatch and write tag (shared across the console)
- `--color-moss`: Approved edge and added lines (shared across the console)
- `--color-rust`: Rejected edge and destructive tag (shared across the console)
- `--color-line-2`: Held edge (shared across the console)
- `--color-bone`: Primary button and held hatch (shared across the console)
- `--bloom-ember`: Pending LED glow (shared across the console)
- `--bloom-bone`: Primary hover (shared across the console)
- `--bloom-rust`: Reject hover (shared across the console)
- `--shadow-cast`: Cast shadow layer (shared across the console)
- `--radius-widget`: Card radius (shared across the console)
- `--dur-bloom`: Edge, halo and diff disclosure (shared across the console)
- `--ease-analog`: Every transition (shared across the console)

### Motion

- state change: edge shadow and halo opacity cross-fade, --dur-bloom, --ease-analog (canon)
- diff-open: diff grows from 0fr to 1fr, chevron turns 90 degrees, --dur-bloom, --ease-analog (canon)
- reduced motion: all transitions off, none (canon)

### Accessibility

- The section is named "Gate: " plus the title; status is role status, aria-live polite.
- Check LEDs are paired with a text result (passed, failed, warning, running).
- Show diff is a button with aria-expanded and aria-controls; the hidden diff is aria-hidden.
