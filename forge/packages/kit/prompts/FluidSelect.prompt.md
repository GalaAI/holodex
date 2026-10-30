The one single-choice dropdown in ArkStation: pick one value from a short list. Every dropdown that picks a value is FluidSelect; every dropdown of commands is DropdownMenu (operator law, 2026-09-27). The motion is the operator's fluid dropdown with disclosure cadence updated 2026-10-01: a spring height reveal, staggered options, a highlight that glides between rows, the chevron turning over, and an icon that lifts and takes its tone on hover. The look is Relic Phosphor: the TextField box for the trigger, a surface panel for the drop, dim text lit to bone, and the active row lit from its leading edge by a side light in the complementary accent.

```jsx
<FluidSelect
  label="Category"
  value={category}
  onValueChange={setCategory}
  options={[
    { id: "all", label: "All", icon: Layers, separatorAfter: true },
    { id: "augment", label: "Augment", icon: Cpu, tone: "patina" },
    { id: "gear", label: "Gear", icon: Wrench, tone: "coral" },
  ]}
/>
```

Tones are accent tokens only: accent, patina, coral, seal, moss, army, bone. Ember and rust are never tones; they mean warning and error. It replaces SelectField and every raw select.

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. The one single-choice dropdown in ArkStation: pick one value from a short list.

### Field label (canon)

- Font: ui, --text-size-data, 600 (rp-field__label). Casing: sentence.
- Limit: 32 characters, 1 line(s). Overflow: shorten.
- Name what is being chosen, as a noun (Category, Model, Destination).
- Never: The control already says choose; name the thing ("Model", not "Select a model").
- Never: No terminal punctuation on a label.
- Never: No em or en dashes.
- Good: "Category", "Model", "Destination"
- Bad: "Select a category:"
- Source: Operator, contract studio, 2026-09-27. Proposed as: Matches TextField labels; length proposed

### Option label (canon)

- Font: ui, --text-size-body, 600. Casing: sentence.
- Limit: 24 characters, 1 line(s). Overflow: truncate-ellipsis.
- One or two words; the value, not a sentence about it.
- Use the canon noun from @ark/lexicon (Omnitek, Augment, Gear, Loadout).
- Never: No terminal punctuation on an option.
- Never: No em or en dashes.
- Never: Omnikit is legacy; the canon term is Omnitek.
- Good: "All", "Augment", "Gear"
- Bad: "Show me every Omnikit item."
- Source: Operator, contract studio, 2026-09-27. Proposed as: Length proposed with the law

### Density and placement

- Phosphor disclosure law: leading-edge aura fades to transparent before the right edge on dropdown rows and accordion headers; labels and hit targets are never masked. Open and glide use a 240ms beat, items settle in 180ms with 12ms steps capped at 60ms total delay, and reduced motion removes animation. (canon; Operator, chat, 2026-10-01)
- Operator law, 2026-09-27: all single-choice dropdowns use this component, in Relic Phosphor text and color. (canon; Operator, chat, 2026-09-27)
- Motion is the law's: spring reveal, staggered options, gliding highlight, turning chevron, lifting icon. The shared disclosure cadence and directional aura apply. (canon; Operator, chat, 2026-09-27)
- Tones are accent tokens only; ember and rust are never option tones. (canon; colors.css: ember is warning and gates, rust is error)
- Two to twelve options. Past twelve, the list needs a search field instead. (canon; Operator, contract studio, 2026-09-27)
- At most one separator, after an All option. (canon; Operator, contract studio, 2026-09-27)
- Use for: Any form field that picks one value
- Use for: Filters that pick one category or scope
- Not for: A list of commands (DropdownMenu)
- Not for: Switching the mode of a workspace (ContextSwitcher)
- Not for: Long lists that need search

### Tokens

- `--dur-bloom`: Trigger hover and focus (shared across the console)
- `--touch-min`: Trigger and option height (shared across the console)
- `--radius-menu-closed`: Trigger corner at rest (shared across the console)
- `--radius-menu-open`: Trigger corner while open and panel corner (shared across the console)
- `--dur-disclosure`: Corner morph duration (shared across the console)
- `--ease-disclosure`: Corner morph easing (shared across the console)
- `--radius-badge`: Highlight corner (shared across the console)
- `--color-surface-2`: Trigger and highlight fill (shared across the console)
- `--color-patina`: Trigger focus bloom (shared across the console)
- `--sidelight`: Active row side light, the complementary accent (coral against patina) (shared across the console)
- `--shadow-cast`: Panel lift (shared across the console)

### Motion

- open, close: height springs from 0 to auto, 240ms spring, bounce 0 (canon)
- options: fade and drop in from --s2 above, bounded stagger, 180ms entry, 12ms steps capped at 60ms; cubic-bezier(0.2, 0, 0, 1) (canon)
- active row: highlight glides to the row under the pointer or keyboard, 240ms spring, bounce 0 (canon)
- chevron, icon: chevron turns 180 on open; hovered icon scales 1.2 and fades into its tone, 120ms chevron, 180ms ease-out tone (canon)
- open, close: trigger corner morphs from --radius-menu-closed to --radius-menu-open; the panel wears --radius-menu-open, --dur-disclosure, --ease-disclosure (canon)
- reduced motion: springs and fades off (MotionConfig reducedMotion user); CSS trigger and option transitions off, none (canon)

### Accessibility

- Trigger is a button with aria-haspopup listbox and aria-expanded.
- Listbox with options; aria-activedescendant tracks the active row.
- ArrowUp and ArrowDown, Home, End, Enter, Space, Escape and Tab all work; focus returns to the trigger.
- Trigger and options are 44px tall; the trigger blooms patina on focus and the active row carries the side light.
- aria-describedby is forwarded to the trigger so hint text is announced with the field.
- With name set, a hidden input submits the chosen id with a native form.

### Known gaps

- 25 raw select elements across 13 files in apps/web-console. (apps/web-console (outside packages/ui); migrate each to FluidSelect or SelectField)
- Group headings (option.group) are presentational spans, not role group, so the group name is not announced. (src/controls/FluidSelect.tsx rp-fluid__group)
- The hidden input carries name and value only; required is not enforced, since a hidden input does not take part in constraint validation. (src/controls/FluidSelect.tsx hidden input)
