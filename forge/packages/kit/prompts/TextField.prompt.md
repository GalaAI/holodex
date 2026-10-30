Single-line typed entry. Label above in Inter 600 dim, value in the host's type inside a surface-2 box with a line-2 border, hint below in ghost. Border blooms patina only while focused (Law 05). 44px touch height. `mono` switches the value to JetBrains Mono for references, ids and env names.

```jsx
<TextField
  label="Vault reference"
  value={ref}
  mono
  onValueChange={setRef}
  hint="Secret fields store references only."
/>
```

Props: label, value, onValueChange, hint, mono, disabled, placeholder. Controlled only. Native input attributes such as `type="number"`, `min`, `max` and `step` retain this field layout. Use TextArea for anything that wraps.

`CheckboxField` extends this same owner for boolean entry through `checked` and `onCheckedChange`. Its label owns the touch target, and its input retains native keyboard and accessibility behavior. `FileField` returns selected files through `onFilesChange`; an optional `inputRef` with `hidden` lets an existing shared Button open the chooser without a second visible field.

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. Single-line typed entry with the label above, the hint below and a patina border only while focused.

### mono

- `false`: Value in the host's type.
- `true`: Value in JetBrains Mono for references, ids and env names.

### Field label (proposed)

- Font: ui, --text-size-data, weight 600. Casing: as-authored.
- Limit: 48 characters, 1 line(s). Overflow: wrap.
- Name the value being entered (Vault reference), not an instruction.
- No trailing colon; the layout already separates label and box.
- Never: A field label has no trailing colon.
- Good: "Vault reference", "Usage period"
- Bad: "Vault reference:"
- Source: inferred from TextField.tsx rp-field__label

### Density and placement

- The border blooms patina only while the field is focused (Law 05). (canon; TextField.prompt.md; text-field.css .rp-field__box:focus-within)
- Use TextArea for anything that wraps. (canon; TextField.prompt.md)
- Controlled only: value and onValueChange are required. (canon; TextField.prompt.md Props line)
- Use for: Forms of single-line values
- Use for: References, ids and env names (mono)
- Not for: Multi-line text (TextArea)
- Not for: Picking one value from a list (FluidSelect)

### Tokens

- `--s2`: Label, box and hint gap; input block inset (shared across the console)
- `--s3`: Input inline inset (shared across the console)
- `--s4`: Checkbox size (shared across the console)
- `--u`: Checkbox focus outline (shared across the console)
- `--touch-min`: Input and checkbox row height (shared across the console)
- `--radius-btn`: Box corner radius (shared across the console)
- `--font-ui`: Label face (shared across the console)
- `--font-mono`: Mono value face (shared across the console)
- `--font-body`: Hint face (shared across the console)
- `--text-size-data`: Label, hint and mono value size (shared across the console)
- `--color-dim`: Label text (shared across the console)
- `--color-bone`: Value text and checkbox accent (shared across the console)
- `--color-ghost`: Hint, placeholder and hover border (shared across the console)
- `--color-line-2`: Box border at rest (shared across the console)
- `--color-surface-2`: Box fill (shared across the console)
- `--color-patina`: Focus border (shared across the console)
- `--bloom-patina`: Focus glow (shared across the console)
- `--dur-bloom`: Focus transition (shared across the console)
- `--ease-analog`: Focus easing (shared across the console)
- `--border-hair`: Box border width (shared across the console)

### Motion

- focus: border colour and bloom fade in, --dur-bloom + --ease-analog (canon)
- reduced motion: no border or bloom transition, none (canon)

### Accessibility

- The label element wraps the input, so the label is its accessible name.
- Native input attributes such as type, min, max and step keep native keyboard behavior.
- A hidden FileField takes its accessible name from aria-label={label}.
- The hint has a useId id and is linked with aria-describedby (merged with any caller value) on TextField and SelectField.
- SelectField renders FluidSelect; a hidden input carries name and value so native form submission still works.

### Known gaps

- SelectField forwards only id, name, form, disabled and aria-describedby to FluidSelect; other native select attributes (required, multiple, size, autoFocus, event handlers, style) are accepted by the type but have no effect. (src/controls/TextField.tsx SelectField)
- SelectField optgroup labels render as presentational headings inside the listbox, not as role group, so assistive tech does not announce the group. (src/controls/FluidSelect.tsx rp-fluid__group)
