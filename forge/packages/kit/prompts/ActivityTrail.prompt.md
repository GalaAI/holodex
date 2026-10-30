Compact evidence trail for recent reads, selections, or submitted actions. It renders application-supplied receipts and does not create or persist events.

```jsx
<ActivityTrail items={[{ id: receipt.id, label: receipt.summary }]} />
```

Use newest-first when recency matters. Do not present invented events as live evidence.

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. A compact evidence trail that renders application-supplied receipts for recent reads, selections or submitted actions.

### live

- `polite`: Default; new receipts are announced when idle.
- `assertive`: New receipts interrupt the screen reader.
- `off`: No live announcement.

### Density and placement

- Every item is a supplied receipt; never present invented events as live evidence. (canon; ActivityTrail.prompt.md: Do not present invented events as live evidence)
- Use for: Recent reads, selections or submitted actions beside the surface they came from
- Not for: Logging or persisting events
- Not for: Showing placeholder or demo events as live

### Tokens

- `--color-patina`: Item rule (shared across the console)
- `--color-dim`: Item ink (shared across the console)
- `--font-mono`: Item face (shared across the console)
- `--text-size-data`: Item size (shared across the console)
- `--s2`: Item gap and vertical padding (shared across the console)
- `--s3`: Item left padding (shared across the console)

### Accessibility

- Rendered as an ordered list.
- aria-live defaults to polite so new receipts are announced.
