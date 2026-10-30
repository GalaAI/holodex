CadenceMark is the 28 by 16 thinking mark that leads ThinkingBlock. live: three patina dots shuffle like a shell game; done: they melt into one point that opens into the line-only Ark globe in cream, unlit; stopped: they gather and a rust stop square takes their place. It is one element across states, so the change reads as a morph. The rest of the Cadence family shares its tone rule (patina only while an agent is live, cream for system work, nothing glows when nothing is alive): CadenceDots for agent thinking in a line of text, CadenceArc as an inline busy mark in buttons and chips, StreamCaret at the end of streaming text, and SweepTrack for indeterminate work, one per view.

```jsx
<CadenceMark state="live" />
<CadenceDots size="sm" tone="cream" label="Indexing" />
<Button disabled><CadenceArc /> Saving</Button>
```

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. A small thinking mark whose dots shuffle while live, become the Ark globe when done, or a stop square when stopped.

### state

- `live`: Patina dots shuffle with a soft glow.
- `done`: Shapeshifts into the cream Ark globe, unlit.
- `stopped`: Dots gather into a rust stop square.

### Density and placement

- Patina only when an agent is live; cream for system work; nothing glows when nothing is alive. (canon; cadence.css header)
- One SweepTrack per view. (canon; Cadence.tsx SweepTrack)
- Use for: ThinkingBlock header
- Use for: Compact agent status beside a label
- Not for: Measured progress (use SeamBar)
- Not for: Large loading stages (use NodeGlobeLoader)

### Tokens

- `--color-patina`: Live dots and arc (shared across the console)
- `--color-cream`: Done globe and system dots (shared across the console)
- `--color-bone`: Caret, arc and sweep (shared across the console)
- `--bloom-patina`: Live dot glow (shared across the console)
- `--color-line`: Sweep track (shared across the console)
- `--ease-analog`: Every eased motion (shared across the console)
- `--color-rust-ink`: Stop square stroke (shared across the console)
- `--dur-shuffle`: Live shuffle loop (shared across the console)
- `--dur-cadence-dots`: CadenceDots loop (shared across the console)
- `--dur-cadence-arc`: CadenceArc rotation (shared across the console)
- `--dur-caret`: StreamCaret blink (shared across the console)
- `--dur-loader-sweep`: SweepTrack loop (shared across the console)

### Motion

- live: alternating swaps of the left and right pairs; the dot moving right arcs over, --dur-shuffle loop, x and arc on separate layers (canon)
- done: merge, swell into a ring, settle as the rim, meridians draw on and orbit, about 1.2s --ease-analog, then a 6s SMIL orbit (canon)
- stopped: dots gather, stop square scales in, 240ms --ease-analog (canon)
- reduced motion: still poses; the still emblem globe replaces the orbit, none (canon)

### Accessibility

- CadenceMark and CadenceArc are role img with a label; CadenceDots is role status with a label.
- StreamCaret is aria-hidden.
