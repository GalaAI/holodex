// Copied from GalaAI/ark packages/ui/src/controls/TextArea.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import { type ReactNode, type TextareaHTMLAttributes, useId } from "react";
import { joinIds } from "./TextField";

/**
 * Multi-line typed entry: an intent, a note, a message body.
 *
 * Same box, label and hint as TextField. Grows only when the operator drags
 * it (vertical resize), never on its own, so a form does not shift under a
 * hand that is still typing.
 */
export interface TextAreaProps extends Omit<
  TextareaHTMLAttributes<HTMLTextAreaElement>,
  "value" | "onChange"
> {
  label: string;
  value: string;
  onValueChange: (value: string) => void;
  /** Guidance or evidence rendered under the box. */
  hint?: ReactNode;
}

export function TextArea({
  label,
  value,
  onValueChange,
  hint,
  rows = 4,
  className,
  disabled,
  "aria-describedby": describedBy,
  ...rest
}: TextAreaProps) {
  const fieldId = useId();
  const hintId = `rp-field-hint-${fieldId}`;
  // The hint sits inside the <label>, so name the control by its label text alone (the hint is its description).
  const labelId = `rp-field-label-${fieldId}`;
  return (
    <label className={["rp-field", className ?? ""].filter(Boolean).join(" ")}>
      <span id={labelId} className="rp-field__label">
        {label}
      </span>
      <span className="rp-field__box">
        <textarea
          className="rp-field__input rp-field__input--area"
          rows={rows}
          value={value}
          disabled={disabled}
          onChange={(event) => onValueChange(event.currentTarget.value)}
          aria-labelledby={labelId}
          aria-describedby={joinIds(describedBy, hint ? hintId : undefined)}
          {...rest}
        />
      </span>
      {hint ? (
        <span id={hintId} className="rp-field__hint">
          {hint}
        </span>
      ) : null}
    </label>
  );
}
