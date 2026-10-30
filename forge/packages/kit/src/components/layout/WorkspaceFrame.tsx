// Copied from GalaAI/ark packages/ui/src/layout/WorkspaceFrame.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import type { HTMLAttributes, ReactNode } from "react";

/**
 * Net-new justification: focused canvas plus contextual inspector layouts
 * recur across ArkStation, but their domain contracts must remain app-owned.
 * WorkspaceFrame owns only the responsive relationship between those slots.
 */
export interface WorkspaceFrameProps extends HTMLAttributes<HTMLElement> {
  children: ReactNode;
  inspector: ReactNode;
  inspectorLabel?: string;
  tools?: ReactNode;
  layout?: "flow" | "editor";
  inspectorHidden?: boolean;
}

export function WorkspaceFrame({
  children,
  inspector,
  inspectorLabel = "Workspace context",
  tools,
  layout = "flow",
  inspectorHidden = false,
  className,
  ...rest
}: WorkspaceFrameProps) {
  const classes = ["rp-workspace-frame", className ?? ""]
    .filter(Boolean)
    .join(" ");

  return (
    <section
      className={classes}
      data-layout={layout}
      data-has-tools={Boolean(tools)}
      data-inspector-hidden={inspectorHidden}
      {...rest}
    >
      {tools ? <div className="rp-workspace-frame__tools">{tools}</div> : null}
      <div className="rp-workspace-frame__canvas">{children}</div>
      <aside
        className="rp-workspace-frame__inspector"
        aria-label={inspectorLabel}
        hidden={inspectorHidden}
      >
        {inspector}
      </aside>
    </section>
  );
}
