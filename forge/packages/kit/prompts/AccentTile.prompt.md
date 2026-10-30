Warm accent tile (mono label + Archivo value) colored by the zone accent: wrap a region in data-zone="create|distribute|operate" to rotate its color. Decorative energy, never status.

```jsx
<div data-zone="create">
  <AccentTile label="Drafts" value="12" />
</div>
```

Props: label, value, grain (default true).

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. A warm tile colored by the zone accent that carries decorative energy and never status.

### Tile label (proposed)

- Font: mono, --text-size-micro, 0.16em tracking. Casing: uppercase-by-css.
- Limit: none characters, 1 line(s). Overflow: shorten.
- Write the label in sentence case; .rp-tile__label applies text-transform: uppercase.
- Good: "Drafts", "Queued"
- Bad: "DRAFTS"
- Source: inferred from accent-tile.css .rp-tile__label

### Tokens

- `--rp-accent`: Tile fill, follows data-zone (shared across the console)
- `--rp-accent-ink`: Text on the accent fill (shared across the console)
- `--rp-grain-url`: Fractal-noise grain film (shared across the console)
- `--radius-tile`: Corner radius (shared across the console)
- `--pad-tile`: Inset (shared across the console)
- `--font-mono`: Label face (shared across the console)
- `--font-display`: Value face (shared across the console)
- `--u`: Gap between label and value (shared across the console)
- `--text-size-micro`: Label size (shared across the console)
- `--text-size-value`: Value size (shared across the console)

### Accessibility

- The grain layer is aria-hidden.
- Color follows the zone, so the label and value text carry all meaning.
