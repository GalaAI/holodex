ActionPill is the one selectable action chip: a mono, uppercase button that names an action, carries its risk tier in data-risk and holds a pressed state through aria-pressed. Set `gated` when Seraphym will hold the action at the operator gate; an ember "gate" marker appears. Set `planned` for an unwired action, which renders dim and cannot be clicked. The pill never runs anything itself; the consumer routes the click through POST /api/dlc/transmute.

```jsx
<ActionPill risk="high" gated pressed={selected}>
  Deploy
</ActionPill>
```

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. The one selectable action chip that names a transmuter action, its risk tier and whether an operator gate waits behind it.

### risk

- `low`: Default tier; rest border tinted toward moss.
- `medium`: Medium tier, mirrors bamfRiskLevelSchema; rest border tinted toward ember.
- `high`: High tier, mirrors bamfRiskLevelSchema; rest border tinted toward rust.
- `critical`: Critical tier; full rust border.

### Tokens

- `--color-surface-2`: Inset fill (shared across the console)
- `--color-line-2`: Rest border (shared across the console)
- `--color-dim`: Rest ink (shared across the console)
- `--color-bone`: Hover and pressed ink (shared across the console)
- `--color-ghost`: Hover border (shared across the console)
- `--color-patina`: Pressed edge and focus ring (shared across the console)
- `--color-rust`: Critical border and high tier tint (shared across the console)
- `--color-moss`: Low tier tint (shared across the console)
- `--touch-min`: Minimum height (shared across the console)
- `--color-ember`: Gate marker and medium tier tint (shared across the console)
- `--font-mono`: Label face (shared across the console)
- `--text-size-data`: Label size (shared across the console)
- `--radius-btn`: Corner radius (shared across the console)
- `--gap-inline`: Label to gate gap (shared across the console)
- `--s3`: Horizontal padding (shared across the console)
- `--u`: Border and focus unit (shared across the console)
- `--dur-bloom`: Hover timing (shared across the console)
- `--ease-analog`: Hover easing (shared across the console)

### Motion

- hover: color and border color, --dur-bloom, --ease-analog (canon)
- press: translateY(u / 4), instant (canon)
- reduced motion: no transition, no press shift, none (canon)

### Accessibility

- Selection is exposed as aria-pressed.
- Focus ring: patina outline at u / 2, offset 0.75u.
- The gate marker is visible text, not color alone.
- A planned pill keeps keyboard focus: it sets aria-disabled only and the click handler blocks activation.
- Minimum height is --touch-min.

### Known gaps

- The risk tier is conveyed by border tint alone; there is no text or shape cue for low, medium or high. (src/actions/action-pill.css .rp-pill[data-risk])
