Lifecycle badge (mono, 9.5px). Unstated = ghost outline; validated = patina outline; proven = filled patina (the ONLY filled badge); blocked = rust outline.

```jsx
<Badge state="proven">Proven</Badge>
```

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. A small outlined mono tag for lifecycle, quantity, or evidence state.

### state

- `unstated`: No state asserted. Ghost outline (omit the prop).
- `validated`: Checked against a source. Patina outline.
- `proven`: Proven by a receipt. The only filled badge.
- `blocked`: Blocked or depleted. Rust outline.

### Badge text (canon)

- Font: mono, label-badge (--text-size-badge, 0.12em). Casing: as-authored.
- Limit: 16 characters, 1 line(s). Overflow: shorten.
- One or two words, or a quantity (x3, Depleted, validated).
- A badge states a fact about the record; actions are Buttons.
- Never: A badge has no terminal punctuation.
- Never: A badge is not a control; use a Button or TextLink.
- Good: "proven", "x3", "Depleted", "Source degraded"
- Bad: "Click to verify", "This record was validated."
- Source: Operator, contract studio, 2026-09-27. Proposed as: Length measured from live badges on :5300 (longest 15 characters)

### Density and placement

- proven is the only filled badge. (canon; Badge.prompt.md; laws.ts One filled badge)
- At most three badges in one row; a tag list shows up to ten, then a +N more count. (canon; Operator, contract studio, 2026-09-27)
- Use for: Row trailing evidence
- Use for: Inspector facts
- Use for: Quantity on consumables
- Not for: Primary status of a live feed (Led)
- Not for: Clickable filters (ActionPill)

### Tokens

- `--text-size-badge`: Text size (shared across the console)
- `--radius-badge`: Corner radius (shared across the console)
- `--pad-chip`: Padding (shared across the console)
- `--border-hair`: Border width (shared across the console)

### Accessibility

- Never rely on the fill alone; the text says the state.
