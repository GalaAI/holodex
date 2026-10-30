// Copied from GalaAI/ark packages/ui/src/status/Cadence.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import { useId, type CSSProperties } from "react";
import { ARK_GLOBE } from "./cadence-globe";

/**
 * Cadence family: the Phosphor loaders. One easing (ease-analog), every motion
 * has a reduced-motion still frame. Tone rule: patina only while an agent is
 * live; cream (bone) for system work; nothing glows when nothing is alive.
 */

export type CadenceDotsSize = "sm" | "md" | "lg";
export type CadenceDotsTone = "patina" | "cream";

export interface CadenceDotsProps {
  size?: CadenceDotsSize;
  tone?: CadenceDotsTone;
  label?: string;
}

/** Agent thinking. Patina only while an agent is live; cream for system work. */
export function CadenceDots({
  size = "md",
  tone = "patina",
  label = "Thinking",
}: CadenceDotsProps) {
  return (
    <span
      className={[
        "rp-cadence-dots",
        size !== "md" ? `rp-cadence-dots--${size}` : "",
        tone === "cream" ? "rp-cadence-dots--cream" : "",
      ]
        .filter(Boolean)
        .join(" ")}
      role="status"
      aria-label={label}
    >
      <i />
      <i />
      <i />
    </span>
  );
}

export type CadenceArcTone = "bone" | "patina";

export interface CadenceArcProps {
  /** Square size in px. Default 16. */
  size?: number;
  tone?: CadenceArcTone;
  label?: string;
}

/** Inline busy mark for buttons and chips. */
export function CadenceArc({
  size = 16,
  tone = "bone",
  label = "Working",
}: CadenceArcProps) {
  return (
    <svg
      className={
        tone === "patina"
          ? "rp-cadence-arc rp-cadence-arc--patina"
          : "rp-cadence-arc"
      }
      style={{ "--rp-cadence-arc-size": size + "px" } as CSSProperties}
      viewBox="0 0 24 24"
      role="img"
      aria-label={label}
    >
      <circle cx="12" cy="12" r="9" />
    </svg>
  );
}

/** Token streaming caret. Put it at the end of the streaming text. */
export function StreamCaret() {
  return <span className="rp-stream-caret" aria-hidden="true" />;
}

const widthStyle = (width: number | string | undefined) =>
  width != null
    ? ({
        "--rp-cadence-width": typeof width === "number" ? width + "px" : width,
      } as CSSProperties)
    : undefined;

export interface SweepTrackProps {
  /** Track width, px when a number. Default 100%. */
  width?: number | string;
  tone?: "bone" | "patina";
  label?: string;
}

/** Indeterminate. One per view. */
export function SweepTrack({
  width,
  tone = "bone",
  label = "Loading",
}: SweepTrackProps) {
  return (
    <div
      className={
        tone === "patina"
          ? "rp-sweep-track rp-sweep-track--patina"
          : "rp-sweep-track"
      }
      style={widthStyle(width)}
      role="progressbar"
      aria-label={label}
    >
      <i />
    </div>
  );
}

export interface SeamBarProps {
  /** Measured progress, 0 to 100. Rounded and clamped. */
  value: number;
  /** Bar width, px when a number. Default 100%. */
  width?: number | string;
  label?: string;
}

/** Determinate, 0 to 100. */
export function SeamBar({ value, width, label = "Progress" }: SeamBarProps) {
  const v = Math.max(0, Math.min(100, Math.round(value)));
  return (
    <div
      className="rp-seam-bar"
      style={widthStyle(width)}
      role="progressbar"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={v}
    >
      <i style={{ width: v + "%" }} />
    </div>
  );
}

export interface SkeletonProps {
  /** Width of each line as a percentage. Default [40, 100, 80, 60]. */
  lines?: number[];
}

/** A spawned card before data lands. Widths are percentages per line. */
export function Skeleton({ lines = [40, 100, 80, 60] }: SkeletonProps) {
  return (
    <div className="rp-skeleton" aria-hidden="true">
      {lines.map((w, i) => (
        <i key={i} style={{ width: w + "%" }} />
      ))}
    </div>
  );
}

export type CadenceMarkState = "live" | "done" | "stopped";

export interface CadenceMarkProps {
  state: CadenceMarkState;
  /** Accessible name. Defaults to Thinking, Done thinking or Stopped. */
  label?: string;
}

/**
 * The thinking mark: dots shuffle (alternating swaps) while live, shapeshift
 * into the line-only Ark globe when done (merge, open into a ring, settle,
 * meridians draw on), or a rust stop square when stopped.
 */
export function CadenceMark({ state, label }: CadenceMarkProps) {
  const goo = "rp-cadence-mark-goo-" + useId().replace(/[^a-zA-Z0-9_-]/g, "");
  return (
    <svg
      className="rp-cadence-mark"
      data-s={state}
      viewBox="0 0 28 16"
      role="img"
      aria-label={
        label ??
        (state === "live"
          ? "Thinking"
          : state === "done"
            ? "Done thinking"
            : "Stopped")
      }
    >
      <defs>
        <filter id={goo} x="-60%" y="-60%" width="220%" height="220%">
          <feGaussianBlur in="SourceGraphic" stdDeviation="1.1" />
          <feColorMatrix values="1 0 0 0 0  0 1 0 0 0  0 0 1 0 0  0 0 0 16 -7" />
        </filter>
      </defs>
      {/* the goo filter only runs during the done shapeshift */}
      <g
        className="rp-cadence-mark__goo"
        style={state === "done" ? { filter: `url(#${goo})` } : undefined}
      >
        <g className="rp-cadence-mark__dot rp-cadence-mark__dot--1">
          <g className="rp-cadence-mark__dx">
            <circle className="rp-cadence-mark__dy" cx="14" cy="8" r="2" />
          </g>
        </g>
        <g className="rp-cadence-mark__dot rp-cadence-mark__dot--2">
          <g className="rp-cadence-mark__dx">
            <circle className="rp-cadence-mark__dy" cx="14" cy="8" r="2" />
          </g>
        </g>
        <g className="rp-cadence-mark__dot rp-cadence-mark__dot--3">
          <g className="rp-cadence-mark__dx">
            <circle className="rp-cadence-mark__dy" cx="14" cy="8" r="2" />
          </g>
        </g>
      </g>
      {/* the merged point opens into this ring, which becomes the globe rim */}
      <circle className="rp-cadence-mark__blob" cx="14" cy="8" r="6.5" />
      <g className="rp-cadence-mark__globe">
        <circle
          className="rp-cadence-mark__globe-ball"
          cx="14"
          cy="8"
          r="6.5"
        />
        <circle
          className="rp-cadence-mark__globe-rim"
          pathLength={1}
          cx="14"
          cy="8"
          r="6.5"
        />
        {/* the Ark emblem globe: latitudes fixed, meridians orbit east (one 60 deg step per loop, seamless) */}
        <path
          className="rp-cadence-mark__globe-line rp-cadence-mark__globe-lat"
          pathLength={1}
          d={ARK_GLOBE.lats}
        />
        <g className="rp-cadence-mark__globe-orbit">
          {ARK_GLOBE.mers.map((values, i) => (
            <path
              key={i}
              className="rp-cadence-mark__globe-line rp-cadence-mark__globe-meridian"
              pathLength={1}
              d={ARK_GLOBE.first[i]}
            >
              <animate
                attributeName="d"
                values={values}
                dur={ARK_GLOBE.dur}
                begin={ARK_GLOBE.begins[i]}
                repeatCount="indefinite"
              />
              <animate
                attributeName="stroke-opacity"
                values={ARK_GLOBE.fades[i]}
                dur={ARK_GLOBE.dur}
                begin={ARK_GLOBE.begins[i]}
                repeatCount="indefinite"
              />
            </path>
          ))}
        </g>
        <path
          className="rp-cadence-mark__globe-line rp-cadence-mark__globe-still"
          pathLength={1}
          d={ARK_GLOBE.still}
        />
      </g>
      <g className="rp-cadence-mark__stop">
        <rect x="10.5" y="4.5" width="7" height="7" rx="1" />
      </g>
    </svg>
  );
}
