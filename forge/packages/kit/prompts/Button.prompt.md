Uppercase Inter-800 button. ONE primary per region: filled neutral bone (NEVER ember; ember is reserved for warnings/gates). Danger is rust; accent is the warm zone pill (mono, follows --rp-accent) and carries a persistent hue-matched bloom: the one always-lit element. Bloom appears on hover only (Law 05): the primary blooms bone (--bloom-bone).

```jsx
<Button variant="primary">Deploy · gate req</Button>
<Button>Cancel</Button>
```

Variants: default, primary, danger, accent. 44px min touch target.

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. Performs one action in place; never moves the operator to another route.

### variant

- `default`: Any action that is not the region's main action.
- `primary`: The one main action in a region. Filled bone, never ember.
- `danger`: Destructive or irreversible action. Rust outline; still passes the operator gate (Law 08).
- `accent`: The zone pill. The one always-lit element; reads --rp-accent.

### Button label (canon)

- Font: ui, label-button (--text-size-btn, 800, 0.16em). Casing: uppercase-by-css.
- Limit: 28 characters, 1 line(s). Overflow: shorten.
- Verb first: say what happens (Open Vault, Retry, Deploy).
- One to four words. A gate suffix may follow a middle dot (Deploy · gate req).
- Product names keep their casing (ArkStation, GrimX, 4GE).
- Moving to another route is a TextLink, not a Button.
- Never: Name the action, not the gesture (no "Click" or "Tap").
- Never: Drop "please"; the interface is not asking a favor.
- Never: No terminal punctuation on a label.
- Never: No em or en dashes.
- Never: Name what happens, not a generic confirmation.
- Good: "Refresh", "Open Elixir recipes", "Deploy · gate req", "Retry"
- Bad: "Click here", "Please submit.", "OK"
- Source: Operator, contract studio, 2026-09-27. Proposed as: Limits measured from 46 live labels on :5300 (longest real label 27 characters). Wording follows TextLink.prompt.md: a Button performs an action

### Density and placement

- One primary Button per region. (canon; Button.prompt.md: ONE primary per region; laws.ts PRESENCE_LAWS)
- At most three Buttons side by side in one action row; move the rest into the inspector or a TextLink. (canon; Operator, contract studio, 2026-09-27)
- Danger never sits next to primary without a gap of --s4 or more. (canon; Operator, contract studio, 2026-09-27)
- Use for: Action rows under a heading
- Use for: Panel header telemetry corner (Refresh)
- Use for: Inspector footers
- Use for: Empty states that offer the next step
- Not for: Navigation between routes (TextLink)
- Not for: Choosing a mode or filter (ContextSwitcher, ActionPill)
- Not for: Icon-only actions without a label (IconButton)

### Tokens

- `--pad-control-x`: Horizontal padding (shared across the console)
- `--radius-btn`: Corner radius (shared across the console)
- `--touch-min`: Minimum height (shared across the console)
- `--text-size-btn`: Label size (shared across the console)
- `--dur-bloom`: Hover and focus timing (shared across the console)
- `--bloom-bone`: Primary hover glow (shared across the console)
- `--bloom-rust`: Danger hover glow (shared across the console)

### Motion

- hover, focus: border color and bloom, --dur-bloom (canon)
- press: translateY(1px), 120ms (no token yet) (canon)
- accent hover: lift -2px, stronger bloom, --dur-bloom (canon)

### Accessibility

- 44px minimum on both axes via --touch-min.
- Focus ring: 2px patina outline, 3px offset, always visible.
- Disabled is 40% opacity; prefer an enabled button that explains why it cannot run.
