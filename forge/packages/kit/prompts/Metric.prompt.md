Neutral operational telemetry card. Use for a label, a current value, and an optional source or boundary note.

```jsx
<Metric label="Observed services" value="12" note="Runtime Gateway contract" />
```

Do not use Metric for zone identity or decoration. AccentTile owns that role, and status remains in Badge or Led.

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. A neutral operational telemetry card with a label, a current value and an optional source or boundary note.

### Metric label (proposed)

- Font: mono, --eyebrow-size, --eyebrow-tracking. Casing: uppercase-by-css.
- Limit: none characters, 1 line(s). Overflow: wrap.
- Name what is measured in sentence case; CSS applies the uppercase.
- Good: "Observed services"
- Bad: "OBSERVED SERVICES"
- Source: inferred from metric.css .rp-metric__label

### Source or boundary note (proposed)

- Font: mono, --text-size-data. Casing: as-authored.
- Limit: none characters, any line(s). Overflow: wrap.
- Say where the value came from or what bounds it.
- Good: "Runtime Gateway contract"
- Source: inferred from Metric.prompt.md and Metric.tsx note prop

### Density and placement

- Not for zone identity or decoration; AccentTile owns that role, and status stays in Badge or Led. (canon; Metric.prompt.md)
- One label, one value and at most one note per card. (proposed; inferred from Metric.tsx props)
- Use for: Operational telemetry rows
- Use for: Service and contract counts
- Not for: Zone identity tiles (AccentTile)
- Not for: Status signals (Badge, Led)
- Not for: Decoration

### Tokens

- `--pad-surface`: Card inset (shared across the console)
- `--s2`: Gap between rows (shared across the console)
- `--touch-min`: Minimum height base (times 3) (shared across the console)
- `--font-mono`: Label and note face (shared across the console)
- `--eyebrow-size`: Label size (shared across the console)
- `--eyebrow-tracking`: Label tracking (shared across the console)
- `--color-ghost`: Label color (shared across the console)
- `--font-display`: Value face (shared across the console)
- `--color-bone`: Value color (shared across the console)
- `--text-size-metric`: Value size (shared across the console)
- `--text-size-data`: Note size (shared across the console)
- `--color-dim`: Note color (shared across the console)

### Accessibility

- Label, value and note are read in DOM order; keep the label meaningful without the value.
