Multi-line typed entry with the same label, box and hint as TextField. Starts at four rows, resizes vertically only by the operator's hand, never on its own. Border blooms patina only while focused (Law 05).

```jsx
<TextArea
  label="Operator intent"
  value={intent}
  rows={3}
  onValueChange={setIntent}
/>
```

Props: label, value, onValueChange, hint, rows, disabled, placeholder. Controlled only. One primary submit action belongs outside it, in a Button.

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. Multi-line typed entry with the same label, box and hint as TextField.

### Field label (proposed)

- Font: ui, --text-size-data. Casing: sentence.
- Limit: none characters, 1 line(s). Overflow: wrap.
- Name what the operator types (Operator intent).
- The label sits above the box; no trailing colon.
- Never: The label sits above the box; drop the trailing colon.
- Good: "Operator intent", "Message body"
- Bad: "Operator intent:"
- Source: inferred from TextArea.prompt.md example and text-field.css .rp-field__label

### Hint (proposed)

- Font: body, --text-size-data. Casing: sentence.
- Limit: none characters, any line(s). Overflow: wrap.
- Guidance or evidence about the entry, shown under the box.
- Good: "Leave blank to keep the detected name."
- Source: inferred from TextArea.tsx hint doc comment

### Density and placement

- One primary submit action belongs outside it, in a Button. (canon; TextArea.prompt.md: One primary submit action belongs outside it, in a Button)
- Use for: An intent, a note, a message body
- Not for: Single-line values (use TextField)
- Not for: Auto-growing entry

### Tokens

- `--s2`: Label gap and input padding (shared across the console)
- `--s3`: Input inline padding (shared across the console)
- `--font-ui`: Label face (shared across the console)
- `--font-body`: Hint face (shared across the console)
- `--text-size-data`: Label and hint size (shared across the console)
- `--color-dim`: Label color (shared across the console)
- `--color-ghost`: Hint, placeholder and hover border (shared across the console)
- `--color-bone`: Value text (shared across the console)
- `--color-line-2`: Box border (shared across the console)
- `--color-surface-2`: Box fill (shared across the console)
- `--radius-btn`: Box corner (shared across the console)
- `--color-patina`: Focus border (shared across the console)
- `--bloom-patina`: Focus bloom (shared across the console)
- `--touch-min`: Minimum height (doubled for the area) (shared across the console)
- `--dur-bloom`: Border transition (shared across the console)
- `--ease-analog`: Border easing (shared across the console)
- `--border-hair`: Box border width (shared across the console)

### Motion

- focus: border blooms patina, --dur-bloom var(--ease-analog) (canon)
- reduced motion: no border transition, none (canon)

### Accessibility

- The label element wraps the textarea, so the label text is its accessible name.
- Resizes vertically only by the operator's hand (text-field.css .rp-field__input--area).
- Disabled uses the disabled attribute plus 0.4 opacity.
- The hint has a useId id and is linked with aria-describedby, merged with any caller value.
