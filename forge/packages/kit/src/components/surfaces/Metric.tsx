// Copied from GalaAI/ark packages/ui/src/surfaces/Metric.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import type { HTMLAttributes, ReactNode } from "react";
import { Panel } from "./Panel";

/**
 * Net-new justification: the donor preview and Build Atlas each implemented
 * the same telemetry card locally, while AccentTile is reserved for zone
 * identity. Metric gives neutral operational telemetry one shared owner.
 */
export interface MetricProps extends HTMLAttributes<HTMLDivElement> {
  label: ReactNode;
  value: ReactNode;
  note?: ReactNode;
}

export function Metric({
  label,
  value,
  note,
  className,
  ...rest
}: MetricProps) {
  const classes = ["rp-metric", className ?? ""].filter(Boolean).join(" ");

  return (
    <Panel className={classes} {...rest}>
      <span className="rp-metric__label">{label}</span>
      <strong className="rp-metric__value">{value}</strong>
      {note ? <span className="rp-metric__note">{note}</span> : null}
    </Panel>
  );
}
