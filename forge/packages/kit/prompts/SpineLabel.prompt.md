SpineLabel rides the outside of a card's left panel edge, reading bottom to top like the stage frame label. It is ghost at rest and takes the edge's status tone while the card is in a status state (live, gate, error, ok, pick). It only repeats text the card already shows, so it is aria-hidden, and it hides below 640px. RunCard, GateCard and SpawnFrame render it for you; the host must be position relative with room on the left.

```jsx
<SpineLabel text="Run // 00:42 // Working" tone="live" />
```

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. A vertical mono line down the outside of a card edge that repeats its kind, id and state.

### tone

- `rest`: Ghost.
- `live`: Patina at 85%.
- `gate`: Ember at 85%.
- `error`: Rust at 85%.
- `ok`: Moss at 80%.
- `pick`: Coral at 85%.

### Spine text (canon)

- Font: mono, --font-mono 500 9.5px, 0.18em. Casing: uppercase-by-css.
- Limit: none characters, 1 line(s). Overflow: truncate-ellipsis.
- kind // id // state, joined by //.
- Never: A spine label has no terminal punctuation.
- Good: "Run // 00:42 // Working", "Gate // Needs you"
- Bad: "RUN // WORKING"
- Source: SpineLabel.tsx doc comment; RunCard, GateCard and SpawnFrame defaults

### Density and placement

- Decoration only: it repeats visible text and is aria-hidden. (canon; SpineLabel.tsx doc comment)
- One spine per card. (proposed)
- Use for: Outside the left edge of RunCard, GateCard and SpawnFrame
- Not for: As the only carrier of state
- Not for: Inside a card body

### Tokens

- `--rp-spine-offset`: Offset left of the host card edge, default calc(var(--u) * -5.5); hosts with less room tune it (this component)
- `--color-ghost`: Rest tone (shared across the console)
- `--color-patina`: Live tone (shared across the console)
- `--color-ember`: Gate tone (shared across the console)
- `--color-rust`: Error tone (shared across the console)
- `--color-moss`: Ok tone (shared across the console)
- `--color-coral`: Pick tone (shared across the console)
- `--dur-bloom`: Tone change (shared across the console)

### Motion

- tone change: colour cross-fade, --dur-bloom, --ease-analog (canon)
- reduced motion: transition off, none (canon)

### Accessibility

- aria-hidden; the state it repeats is always visible as text in the card.

### Known gaps

- The name SpineLabel is proposed, not canon. (SpineLabel.tsx)
