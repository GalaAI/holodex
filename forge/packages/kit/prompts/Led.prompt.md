Status LED with uppercase mono label. live = pulsing patina bloom; warn = ember; err = rust; ok = moss and UNLIT (Law 05).

```jsx
<Led state="live">System online</Led>
```

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. Shows the state of one live feed or service as a light plus a mono tag.

### state

- `live`: Receiving live data. The only state that pulses and blooms.
- `warn`: Degraded, loading, or waiting at a gate. Ember.
- `err`: Down or unreachable. Rust.
- `ok`: Healthy and settled. Moss, unlit (Law 05).

### Status tag (canon)

- Font: mono, label-data (--text-size-data, 0.12em). Casing: as-authored.
- Limit: 20 characters, 1 line(s). Overflow: shorten.
- The state or the live count, nothing else (1841 visible, unreachable).
- For fetched data use wireStateLabel(); never invent a healthy label for a feed you did not read.
- Never: A status tag has no terminal punctuation.
- Never: State what is known; no hedged health.
- Good: "1841 visible", "unreachable", "System online"
- Bad: "Everything looks good!"
- Source: Operator, contract studio, 2026-09-27. Proposed as: Honesty rule: wire-state.ts (canon). Length proposed

### Density and placement

- Only live blooms; ok stays unlit. (canon; Law 05; led.css)
- One Led per feed, placed at the frame edge beside the data it describes. (canon; Operator, contract studio, 2026-09-27. Proposed as: Law 06: data at edges)
- Use for: Panel header telemetry
- Use for: Frame edges
- Use for: Beside a count it qualifies
- Not for: Decoration
- Not for: Inside body copy

### Tokens

- `--dur-pulse`: Live pulse period (shared across the console)
- `--bloom-patina`: Live glow (shared across the console)
- `--bloom-ember`: Warn glow (shared across the console)
- `--bloom-rust`: Err glow (shared across the console)
- `--led-size`: Light diameter (shared across the console)
- `--text-size-data`: Tag size (shared across the console)

### Motion

- live: opacity pulse 1 to .45, --dur-pulse (canon)
- reduced motion: pulse frozen, none (canon)

### Accessibility

- The tag text carries the meaning; color is never the only signal.
