Highlight Aura: the coral side light on the leading edge, a bloom into the row, and a fade toward the right edge through `--aura-falloff`. It is decoration only and never masks labels or hit targets. Hosts keep at least `--aura-lead` (12px) between the side light and their content, on the leading side only. FluidSelect, DropdownMenu and Accordion already wear it; do not hand-roll another selected or active highlight.

Three ways to wear it: `<HighlightAura />` inside a row that carries `auraHostClassName` (the aura fills the row behind its content), `auraClassName` on a layer the host positions or animates, or `auraBehindClassName` on a host that keeps its content and lights the aura on ::before with `data-aura="lit"`. Set `--sidelight` to change the light, for example rust on destructive rows.

```jsx
<div className={auraHostClassName}>
  <HighlightAura />
  <span>Provider</span>
</div>
```

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. The one global highlight for a lit row, option or header: a leading side light that blooms into the row and fades toward the right edge.

### Density and placement

- One lit aura per list or menu at a time: the active or chosen row. (canon; FluidSelect and DropdownMenu: the lit row is the focus (fluid-select.css, dropdown-menu.css))
- Use for: The active or chosen row in a list, select or menu
- Use for: A disclosure header on hover, focus or while open
- Not for: Status or severity (use Led, Badge or TriageTag)
- Not for: Static emphasis on content that is not selected or active

### Tokens

- `--sidelight`: Side light, the complementary accent (coral against patina) (shared across the console)
- `--aura-falloff`: Trailing fade mask toward the right edge (shared across the console)
- `--aura-lead`: Minimum gap between the side light and the host's content, leading side only (shared across the console)
- `--color-surface-2`: Aura fill (shared across the console)
- `--radius-badge`: Aura corner (shared across the console)

### Motion

- lit, unlit (behind variant): aura fades in and out, 180ms disclosure item beat (--dur-disclosure-item) (canon)
- reduced motion: fade is removed, none (canon)

### Accessibility

- Decoration only: aria-hidden, pointer-events none, never carries meaning on its own.
- Pair it with the real state (aria-selected, aria-expanded, aria-activedescendant or focus).

### Known gaps

- Tabs, trees and nav rows still hand-roll their own selected highlight (apps/web-console and packages/ui follow-up adoption)
