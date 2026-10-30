Matte dark panel: the default container: casts a shadow onto the field and carries an inner edge vignette; never dissolves into the background.

```jsx
<Panel style={{ padding: "var(--pad-surface)" }}>content</Panel>
```

No variants. Compose Eyebrow + content inside; keep telemetry at the edges. The panel owns surface only; the consumer sets the inset, default --pad-surface (the spacing rule).

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. The default matte container; owns surface only, the consumer sets the inset.

### Density and placement

- Inset comes from the consumer, default --pad-surface. (canon; Panel.prompt.md; spacing.css THE SPACING RULE)
- Telemetry (Led, counts, refresh) sits at the edges; the center stays content. (canon; Law 06: data at edges)
- No Panel directly inside another Panel; split the region or use an inset cell (--color-surface-2). (canon; Operator, contract studio, 2026-09-27. Proposed as: Law 03: refactor, don't layer)
- Open with an Eyebrow and one heading; at most one Eyebrow per Panel header. (canon; Operator, contract studio, 2026-09-27)
- Use for: Any bounded region of a workspace
- Use for: Inspectors
- Use for: Sector content
- Not for: The one light object (Plate)
- Not for: Zone identity tiles (AccentTile)

### Tokens

- `--pad-surface`: Consumer inset (shared across the console)
- `--radius-widget`: Corner radius (shared across the console)
- `--shadow-cast`: Cast onto the field (shared across the console)
- `--edge-vignette`: Lit top, settled bottom (shared across the console)
- `--color-surface`: Fill (shared across the console)
- `--color-line`: 1px border (shared across the console)

### Motion

- stage enter: translateY 6px to 0, no fade, 0.28s literal + --ease-analog (canon)

### Accessibility

- Give a Panel that is a landmark an aria-label.
