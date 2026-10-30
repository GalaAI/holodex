StageCanvas hosts previews only, never a page layout. Wheel pans, Shift+wheel zooms at the cursor, right or middle drag (or a left drag on the ground) pans, double-click on the ground fits, and the keys arrows, +, - and 0 do the same from focus. A child marked `data-stage-interactive` keeps its own pointer input, so a live desktop under human control still takes clicks while the board stays pannable. Put the session or preview name in `label` and its frame state (Live, Stale, Disconnected, Recorded) in `readout`.

```jsx
<StageCanvas contentWidth={1280} contentHeight={800} label="Shop session" readout="Live">
  <AethernetLiveDesktop ... />
</StageCanvas>
```

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. The infinite preview board (CP-24): pan, zoom and inspect a live preview, one state or a live session, the way an artboard does.

### Density and placement

- One StageCanvas per preview region; it fills its container. (proposed; Augment inventory CP-24 StageCanvas (In review, 2026-10-15))
- Use for: A live AEthernet session or Eden guest desktop
- Use for: Previewing one component or every state of it side by side
- Not for: Page layout or scrolling documents
- Not for: A surface that needs native text selection across the board

### Tokens

- `--color-ground`: Matte ground under the grid (shared across the console)
- `--color-bone`: Dot grid, at 8% (shared across the console)
- `--color-ghost`: Registration marks and frame-edge labels (shared across the console)
- `--shadow-cast`: Board casts onto the ground (shared across the console)
- `--color-patina`: Focus ring (shared across the console)

### Motion

- pan and zoom: the board and the grid track the pointer 1:1, zoom anchors on the cursor, immediate, no easing (canon)

### Accessibility

- The board is a focusable region with aria-roledescription pannable canvas.
- Arrow keys pan, + and - zoom, 0 fits; the zoom readout is aria-live polite.

### Known gaps

- Inspect mode with the TraceBeam picker (FX-10) is not built (packages/ui StageCanvas follow-up)
- States matrix mode (every state side by side) is not built (packages/ui StageCanvas follow-up)
