/**
 * @4ge/kit: the Phosphor design tokens and React components 4GE builds from.
 *
 * Copied from GalaAI/ark packages/tokens and packages/ui at 0f4b55c6 and
 * stripped of Ark-only imports (none of the carried components needed
 * anvl-core, pc-mod or lexicon). Read prompts/<Component>.prompt.md before
 * composing a component. Styles are not auto-injected: import
 * "@4ge/kit/tokens.css" then "@4ge/kit/kit.css" once in the app shell.
 */

// Actions
export { Button, IconButton } from "./components/actions/Button.js";
export type {
  ButtonProps,
  ButtonVariant,
  IconButtonIcon,
  IconButtonProps,
} from "./components/actions/Button.js";
export { ActionPill, ACTION_RISKS } from "./components/actions/ActionPill.js";
export type { ActionPillProps, ActionRisk } from "./components/actions/ActionPill.js";
export { TextLink } from "./components/actions/TextLink.js";
export type { TextLinkProps } from "./components/actions/TextLink.js";

// Brand
export { Eyebrow } from "./components/brand/Eyebrow.js";
export type { EyebrowProps } from "./components/brand/Eyebrow.js";
export { SpineLabel } from "./components/brand/SpineLabel.js";
export type { SpineLabelProps, SpineTone } from "./components/brand/SpineLabel.js";

// Controls
export {
  TextField,
  SelectField,
  FileField,
  CheckboxField,
  joinIds,
} from "./components/controls/TextField.js";
export type {
  TextFieldProps,
  SelectFieldProps,
  FileFieldProps,
  CheckboxFieldProps,
} from "./components/controls/TextField.js";
export { TextArea } from "./components/controls/TextArea.js";
export type { TextAreaProps } from "./components/controls/TextArea.js";
export { FluidSelect } from "./components/controls/FluidSelect.js";
export type {
  FluidSelectOption,
  FluidSelectProps,
  FluidSelectTone,
} from "./components/controls/FluidSelect.js";

// Status
export { Led } from "./components/status/Led.js";
export type { LedProps, LedState } from "./components/status/Led.js";
export { Badge } from "./components/status/Badge.js";
export type { BadgeProps, BadgeState } from "./components/status/Badge.js";
export {
  CadenceArc,
  CadenceDots,
  CadenceMark,
  SeamBar,
  Skeleton,
  StreamCaret,
  SweepTrack,
} from "./components/status/Cadence.js";
export type {
  CadenceArcProps,
  CadenceArcTone,
  CadenceDotsProps,
  CadenceDotsSize,
  CadenceDotsTone,
  CadenceMarkProps,
  CadenceMarkState,
  SeamBarProps,
  SkeletonProps,
  SweepTrackProps,
} from "./components/status/Cadence.js";

// Surfaces
export { Panel } from "./components/surfaces/Panel.js";
export type { PanelProps } from "./components/surfaces/Panel.js";
export { Plate } from "./components/surfaces/Plate.js";
export type { PlateProps } from "./components/surfaces/Plate.js";
export {
  HighlightAura,
  auraBehindClassName,
  auraClassName,
  auraHostClassName,
} from "./components/surfaces/HighlightAura.js";
export type { HighlightAuraProps } from "./components/surfaces/HighlightAura.js";
export { FocalCard } from "./components/surfaces/FocalCard.js";
export type { FocalCardProps, FocalReason } from "./components/surfaces/FocalCard.js";
export {
  StageCanvas,
  fitStageView,
  zoomStageView,
} from "./components/surfaces/StageCanvas.js";
export type { StageCanvasProps, StageView } from "./components/surfaces/StageCanvas.js";
export { Metric } from "./components/surfaces/Metric.js";
export type { MetricProps } from "./components/surfaces/Metric.js";
export { AccentTile } from "./components/surfaces/AccentTile.js";
export type { AccentTileProps } from "./components/surfaces/AccentTile.js";
export { ThinkingBlock } from "./components/surfaces/ThinkingBlock.js";
export type { ThinkingBlockProps, ThinkingState } from "./components/surfaces/ThinkingBlock.js";
export { GateCard } from "./components/surfaces/GateCard.js";
export type {
  CheckResult,
  GateCardProps,
  GateCheck,
  GateFile,
  GateState,
} from "./components/surfaces/GateCard.js";
export { IntakeComposer } from "./components/surfaces/IntakeComposer.js";
export type { IntakeComposerProps } from "./components/surfaces/IntakeComposer.js";

// Layout and data
export { WorkspaceFrame } from "./components/layout/WorkspaceFrame.js";
export type { WorkspaceFrameProps } from "./components/layout/WorkspaceFrame.js";
export { ActivityTrail } from "./components/data/ActivityTrail.js";
export type {
  ActivityTrailItem,
  ActivityTrailProps,
} from "./components/data/ActivityTrail.js";

// Tokens mirrored for JavaScript (canvas paint, inline style). Prefer CSS vars.
export * from "./tokens/index.js";
