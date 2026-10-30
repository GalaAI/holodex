Responsive focused-workspace composition with one action canvas and one contextual inspector. It owns layout only; the application owns panels, routes, actions, and data.

```jsx
<WorkspaceFrame inspector={<BoundaryInspector />}>
  {" "}
  <ActionCanvas />{" "}
</WorkspaceFrame>
```

Keep the primary task in the canvas. Put source, authority, access, receipts, and next action in the inspector.

Use `layout="editor"` for a bounded full-height editor, with the optional `tools` slot and a controlled `inspectorHidden` state. The app supplies an accessible inspector toggle and close action. Below 55rem, the inspector overlays the canvas so the workspace keeps its width; it never becomes an extra fixed-height page below the drawing.

<!-- Contract section generated from src/**/*.contract.ts. Do not edit below; edit the contract and run generate-prompts. -->

## Contract

Status: shipping. Responsive focused-workspace composition with one action canvas and one contextual inspector.

### layout

- `flow`: Canvas and inspector side by side; stacks below 55rem.
- `editor`: Bounded full-height editor with optional tools rail; the inspector overlays the canvas below 55rem.

### Density and placement

- One action canvas and one contextual inspector; it owns layout only. (canon; WorkspaceFrame.prompt.md)
- Keep the primary task in the canvas; put source, authority, access, receipts and next action in the inspector. (canon; WorkspaceFrame.prompt.md)
- Use for: Focused task views with a context inspector
- Use for: Bounded editors with a tools rail
- Not for: Owning panels, routes, actions or data

### Tokens

- `--s3`: Flow gap between canvas and inspector (shared across the console)
- `--s2`: Gap inside the inspector (shared across the console)
- `--station-rail-collapsed-width`: Editor tools column (shared across the console)
- `--station-context-width`: Editor inspector column and flow inspector minimum (shared across the console)
- `--color-surface`: Overlay inspector fill (shared across the console)
- `--shadow-cast`: Overlay inspector shadow (shared across the console)

### Accessibility

- The inspector is an aside labeled by inspectorLabel (default Workspace context).
- inspectorHidden sets the hidden attribute, so the inspector leaves the accessibility tree.
- The app supplies an accessible inspector toggle and close action.
