FocalCard is the one focal object in a loop; telemetry stays in the periphery (Law 06). Give it an eyebrow, a title, the plain sentence of what happened, the technical facts beside it (never instead of it), an optional outcome line after an action, and the actions that resolve it. The left edge color says why it interrupted: gate is ember (a human decides), stop is rust (blocked), live is patina. It renders as a Panel; only the outcome line is a live region, with role="status" and aria-live="polite".

```jsx
<FocalCard
  reason="gate"
  eyebrow="Gate pending // Watcher"
  title="Approve deploy"
  plain="The deploy is waiting for your approval."
  technical="run 42, main"
  actions={<Button>Approve</Button>}
/>
```

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. The single interruption card, shown only when a human must decide, with a plain sentence, its technical facts and the actions that resolve it.

### reason

- `gate`: A human decides. Ember edge and eyebrow. The default.
- `stop`: Blocked. Rust edge and eyebrow.
- `live`: Live. Patina edge and eyebrow.

### Eyebrow (proposed)

- Font: mono, --eyebrow-size, --eyebrow-tracking. Casing: uppercase-by-css.
- Limit: none characters, 1 line(s). Overflow: wrap.
- Name the reason and the source, as in Gate pending // Watcher.
- Good: "Gate pending // Watcher"
- Bad: "GATE PENDING"
- Source: inferred from FocalCard.tsx eyebrow doc comment and focal-card.css .rp-focal__eyebrow

### Plain sentence (proposed)

- Font: body, calc(var(--u) * 3.25). Casing: sentence.
- Limit: none characters, any line(s). Overflow: wrap.
- What happened, in plain words, as one complete sentence.
- Technical detail goes in the technical slot, beside this sentence.
- Never: The plain sentence ends with terminal punctuation.
- Good: "The deploy is waiting for your approval."
- Bad: "deploy waiting"
- Source: inferred from FocalCard.tsx plain doc comment

### Density and placement

- Exactly one FocalCard in the loop, shown only when a human must decide. (canon; FocalCard.tsx doc comment (Net-new justification))
- Technical facts sit beside the plain sentence, never instead of it. (canon; FocalCard.tsx doc comment; technical prop comment)
- Use for: Gate decisions in the loop
- Use for: Blocked work that needs a human
- Not for: Telemetry (keep it at the edges, Law 06)
- Not for: More than one per view

### Tokens

- `--pad-surface`: Card inset (shared across the console)
- `--s2`: Row and action gap, entry offset (shared across the console)
- `--u`: Edge width and action offset (shared across the console)
- `--text-size-lead`: Title size (shared across the console)
- `--text-size-body`: Plain sentence size (shared across the console)
- `--text-size-badge`: Technical facts size (shared across the console)
- `--text-size-data`: Outcome size (shared across the console)
- `--color-ember`: Gate edge and eyebrow (shared across the console)
- `--color-rust`: Stop edge and eyebrow (shared across the console)
- `--color-patina`: Live edge and eyebrow (shared across the console)
- `--dur-bloom`: Entry duration (shared across the console)
- `--ease-analog`: Entry easing (shared across the console)
- `--font-mono`: Eyebrow and technical face (shared across the console)
- `--eyebrow-size`: Eyebrow size (shared across the console)
- `--eyebrow-tracking`: Eyebrow tracking (shared across the console)
- `--font-ui`: Title face (shared across the console)
- `--font-body`: Plain and outcome face (shared across the console)
- `--color-bone`: Title and outcome color (shared across the console)
- `--color-dim`: Plain sentence color (shared across the console)
- `--color-ghost`: Technical facts color (shared across the console)

### Motion

- enter: translateY from minus --s2 to 0, no fade (Panel stage enter rule), --dur-bloom + --ease-analog (canon)
- reduced motion: no entry animation, none (canon)

### Accessibility

- Only the outcome line is a polite live region (role status); it stays mounted so a new receipt or refusal is announced without re-reading the card or its actions.
- Edge color is never the only signal; the eyebrow names the reason.
