Cream "plate" surface: the ONE light object per view, used for headers/hero artifacts. Accent text on it uses --color-plate-rust.

```jsx
<Plate style={{ padding: "var(--pad-surface)" }}>
  <h1>ARK OMNITEK</h1>
</Plate>
```

Use at most one plate per region. The plate owns surface only; the consumer sets the inset, default --pad-surface (the spacing rule).

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. The one cream surface per view, used for headers and hero artifacts.

### Density and placement

- At most one Plate per region; it is the single light object against the matte field. (canon; Plate.prompt.md; Plate.tsx doc comment; plate.css header comment)
- Inset comes from the consumer, default --pad-surface. (canon; Plate.prompt.md (the spacing rule))
- Accent text on a Plate uses --color-plate-rust. (canon; Plate.prompt.md; plate.css header comment)
- Use for: View headers
- Use for: Hero artifacts
- Not for: A second light object in the same region
- Not for: General containers (Panel)

### Tokens

- `--color-plate`: Cream fill (shared across the console)
- `--color-plate-ink`: Text on the plate (shared across the console)
- `--radius-widget`: Corner radius (shared across the console)
- `--shadow-cast`: Cast onto the field (shared across the console)

### Accessibility

- Plate is a plain div with no role; give it a heading inside, and a role plus aria-label if it must be a landmark.
