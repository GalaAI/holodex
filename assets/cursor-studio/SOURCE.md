# Cursor Studio page (temporary copy)

Justin's ruling (2026-11-10): Holodex serves Input Studio's existing cursor
designer page for now, then ports it to native Holodex panels and retires
Input Studio.

Copied unchanged from GalaAI/ark at main `ebd661f5`:

- `index.html` from `apps/input-studio/public/index.html`
- `tokens/src/*.css` from `packages/tokens/src` (the files `tokens.css` imports)

The fonts the CSS names come from Holodex's own `assets/fonts`. Holodex serves
these on 127.0.0.1:8842 at the paths Input Studio used: `/`, `/index.html`,
`/tokens/src/<name>.css` and `/tokens/assets/fonts/<name>.ttf`.

Each section ported to a native panel is removed from this page; the copy goes
when the last one is.
