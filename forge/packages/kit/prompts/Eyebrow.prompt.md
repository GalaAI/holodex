The eyebrow: uppercase JetBrains Mono, 10px, 0.16em tracking, ghost color. Use the // separator for route-like copy.

```jsx
<Eyebrow>BUILD LOG // CROWDFUNDING PRE-LAUNCH</Eyebrow>
```

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. The uppercase mono label that names a region or route.

### Eyebrow text (canon)

- Font: mono, label-eyebrow (--eyebrow-size, --eyebrow-tracking, color-ghost). Casing: as-authored.
- Limit: 40 characters, 1 line(s). Overflow: shorten.
- A noun phrase or a route, not a sentence.
- Route-like copy uses the // separator: ZONE // SECTOR or ARK://ROUTE.
- No verbs telling the operator what to do; that belongs in a heading or a Button.
- Never: An eyebrow is a label; no terminal punctuation.
- Never: No em or en dashes; use // for routes.
- Never: Use // as the separator, not a middle dot.
- Good: "BAZAAR // OMNITEK INVENTORY", "ARK://AIM-LATTICE", "Selected Item"
- Bad: "Click an item to inspect it.", "Colour · tweakable · tuning surface"
- Source: Operator, contract studio, 2026-09-27. Proposed as: Separator: Eyebrow.prompt.md (canon). Length measured from 53 live eyebrows on :5300 (90th percentile 45 characters)

### Density and placement

- One Eyebrow per heading; never stack two. (canon; Operator, contract studio, 2026-09-27)
- Use for: Above a heading
- Use for: Frame-edge labels
- Use for: Section labels inside inspectors
- Not for: Body copy or instructions
- Not for: Anything that must read at 4.5:1 (use color-dim text)

### Tokens

- `--eyebrow-size`: Size (shared across the console)
- `--eyebrow-tracking`: Letter spacing (shared across the console)
- `--color-ghost`: Label color (about 3.7:1, labels only) (shared across the console)

### Accessibility

- color-ghost clears 3:1, not 4.5:1: keep it to small uppercase labels, never essential sentences.
