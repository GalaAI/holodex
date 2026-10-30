The route pointer: uppercase JetBrains Mono, 11px, 0.16em tracking, patina, underline on hover only (Law 05), 44px touch height. A TextLink moves the operator to another route; a Button performs an action. Never both for one intent.

```jsx
<TextLink href="/agent-runtime">Open agent runtime</TextLink>
```

No variants. Anchor semantics only: it takes href, target and rel, never onClick alone.

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. The route pointer that moves the operator to another route, never performing an action in place.

### Link text (proposed)

- Font: mono, --text-size-btn, --eyebrow-tracking. Casing: uppercase-by-css.
- Limit: none characters, 1 line(s). Overflow: wrap.
- Name the destination route (Open agent runtime).
- Write sentence case; CSS applies the uppercase.
- Never: Name the destination, not the gesture.
- Good: "Open agent runtime", "Edit in Email Foundry"
- Bad: "Click here", "OPEN AGENT RUNTIME"
- Source: inferred from TextLink.prompt.md example and text-link.css .rp-link

### Density and placement

- Never a TextLink and a Button for one intent. (canon; TextLink.prompt.md line 1: Never both for one intent)
- Use for: Pointing a surface at another route
- Use for: Inline beside data it opens
- Not for: Performing an action in place (use Button)
- Not for: onClick without href

### Tokens

- `--color-patina`: Link color and focus outline (shared across the console)
- `--font-mono`: Link face (shared across the console)
- `--text-size-btn`: Link size (shared across the console)
- `--eyebrow-tracking`: Link tracking (shared across the console)
- `--touch-min`: Touch height (shared across the console)
- `--dur-bloom`: Color transition (shared across the console)
- `--radius-badge`: Focus outline radius (shared across the console)
- `--ease-analog`: Color transition easing (shared across the console)
- `--focus-ring`: Focus outline width (shared across the console)
- `--focus-offset`: Focus outline offset (shared across the console)
- `--border-hair`: Underline thickness (shared across the console)

### Motion

- hover: underline appears, no movement, instant; color transition --dur-bloom, --ease-analog (canon)
- reduced motion: no color transition, none (canon)

### Accessibility

- Anchor semantics only: takes href, target and rel, never onClick alone.
- Focus shows a patina outline (text-link.css .rp-link:focus-visible).
- Touch height comes from --touch-min.
- href is required in TextLinkProps, so the anchor always has a link role.
