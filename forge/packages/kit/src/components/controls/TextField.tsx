// Copied from GalaAI/ark packages/ui/src/controls/TextField.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import {
  Children,
  Fragment,
  isValidElement,
  useId,
  type ChangeEventHandler,
  type InputHTMLAttributes,
  type ReactElement,
  type ReactNode,
  type Ref,
  type SelectHTMLAttributes,
} from "react";
import { FluidSelect, type FluidSelectOption } from "./FluidSelect";

/**
 * Single-line typed entry: a name, a URI, a vault reference, an id.
 *
 * The label sits above the box so a column of fields reads as a form, the
 * hint sits below it so validation guidance never crowds the value. `mono`
 * switches the value face to JetBrains Mono for route-shaped strings
 * (references, ids, env names) where character alignment carries meaning.
 */
export interface TextFieldProps extends Omit<
  InputHTMLAttributes<HTMLInputElement>,
  "value" | "onChange"
> {
  label: string;
  value: string;
  onValueChange: (value: string) => void;
  /** Guidance or evidence rendered under the box. */
  hint?: ReactNode;
  /** Mono value face for references, ids and env names. */
  mono?: boolean;
}

export function TextField({
  label,
  value,
  onValueChange,
  hint,
  mono,
  className,
  disabled,
  "aria-describedby": describedBy,
  ...rest
}: TextFieldProps) {
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
        <input
          className={
            mono ? "rp-field__input rp-field__input--mono" : "rp-field__input"
          }
          type="text"
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

/** Joins aria id references, dropping empty ones. */
export function joinIds(...ids: Array<string | undefined>): string | undefined {
  const joined = ids.filter(Boolean).join(" ");
  return joined || undefined;
}

// TextField owns the shared labeled-field layout; model pickers need the same
// contract with native options and optgroups. Extend this owner instead of
// keeping app-local selects or flattening long model lists into button rows.
// SelectField keeps its native-option API but renders FluidSelect (operator
// law 2026-09-27): option and optgroup children map to FluidSelect options,
// and a hidden input carries `name` so native form submission still works.
export interface SelectFieldProps extends Omit<
  SelectHTMLAttributes<HTMLSelectElement>,
  "value" | "onChange"
> {
  label: string;
  value: string;
  onValueChange: (value: string) => void;
  hint?: ReactNode;
}

type OptionElementProps = {
  value?: string | number | readonly string[];
  disabled?: boolean;
  label?: string;
  children?: ReactNode;
};

function textOf(node: ReactNode): string {
  if (node === null || node === undefined || typeof node === "boolean")
    return "";
  if (typeof node === "string" || typeof node === "number") return `${node}`;
  if (Array.isArray(node)) return node.map(textOf).join("");
  if (isValidElement<{ children?: ReactNode }>(node))
    return textOf(node.props.children);
  return "";
}

/** Maps native option and optgroup children onto FluidSelect options. */
function selectChildrenToOptions(
  children: ReactNode,
  group?: string,
  groupDisabled = false,
): FluidSelectOption[] {
  const out: FluidSelectOption[] = [];
  for (const child of Children.toArray(children)) {
    if (!isValidElement(child)) continue;
    const element = child as ReactElement<OptionElementProps>;
    const props = element.props;
    if (element.type === Fragment) {
      out.push(
        ...selectChildrenToOptions(props.children, group, groupDisabled),
      );
    } else if (element.type === "optgroup") {
      out.push(
        ...selectChildrenToOptions(
          props.children,
          props.label,
          groupDisabled || Boolean(props.disabled),
        ),
      );
    } else if (element.type === "option") {
      const text = textOf(props.children) || props.label || "";
      const id = props.value === undefined ? text : `${props.value}`;
      out.push({
        id,
        label: text,
        disabled: groupDisabled || Boolean(props.disabled) || undefined,
        group,
      });
    }
  }
  return out;
}

export function SelectField({
  label,
  value,
  onValueChange,
  hint,
  className,
  children,
  id,
  name,
  form,
  disabled,
  required,
  title,
  "aria-label": ariaLabel,
  "aria-describedby": describedBy,
}: SelectFieldProps) {
  const hintId = `rp-field-hint-${useId()}`;
  return (
    <div className={["rp-field", className ?? ""].filter(Boolean).join(" ")}>
      <FluidSelect
        label={label}
        id={id}
        name={name}
        form={form}
        disabled={disabled}
        required={required}
        title={title}
        aria-label={ariaLabel}
        value={value}
        onValueChange={onValueChange}
        options={selectChildrenToOptions(children)}
        aria-describedby={joinIds(describedBy, hint ? hintId : undefined)}
      />
      {hint ? (
        <span id={hintId} className="rp-field__hint">
          {hint}
        </span>
      ) : null}
    </div>
  );
}

// TextField owns labeled native entry, but its value callback cannot carry
// File objects. Extend this owner for file selection instead of duplicating
// upload controls in each app; transport and storage remain with the consumer.
export interface FileFieldProps extends Omit<
  InputHTMLAttributes<HTMLInputElement>,
  "type" | "value" | "onChange"
> {
  label: string;
  onFilesChange: (files: File[]) => void;
  /** Existing toolbar actions may open the same native picker through this ref. */
  inputRef?: Ref<HTMLInputElement>;
}
export function FileField({
  label,
  onFilesChange,
  inputRef,
  hidden,
  className,
  ...rest
}: FileFieldProps) {
  const onChange: ChangeEventHandler<HTMLInputElement> = (event) => {
    const files = Array.from(event.currentTarget.files ?? []);
    event.currentTarget.value = "";
    if (files.length) onFilesChange(files);
  };
  // A hidden picker keeps toolbar activation and native file handling inside
  // the shared owner without adding an invisible field wrapper to app layouts.
  if (hidden)
    return (
      <input
        ref={inputRef}
        type="file"
        aria-label={label}
        hidden
        {...rest}
        onChange={onChange}
      />
    );
  return (
    <label className={["rp-field", className ?? ""].filter(Boolean).join(" ")}>
      <span className="rp-field__label">{label}</span>
      <span className="rp-field__box">
        <input
          ref={inputRef}
          className="rp-field__input"
          type="file"
          {...rest}
          onChange={onChange}
        />
      </span>
    </label>
  );
}

// DropdownMenuCheckboxItem owns menu commands, not persistent form settings.
// Extend this labeled-field owner for boolean values so editor settings keep
// native checkbox semantics without an app-local input or a second form system.
export interface CheckboxFieldProps extends Omit<
  InputHTMLAttributes<HTMLInputElement>,
  "type" | "value" | "checked" | "defaultChecked" | "onChange"
> {
  label: string;
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
}

export function CheckboxField({
  label,
  checked,
  onCheckedChange,
  className,
  ...rest
}: CheckboxFieldProps) {
  return (
    <label
      className={["rp-field", "rp-field--checkbox", className ?? ""]
        .filter(Boolean)
        .join(" ")}
    >
      <input
        className="rp-field__checkbox"
        type="checkbox"
        checked={checked}
        onChange={(event) => onCheckedChange(event.currentTarget.checked)}
        {...rest}
      />
      <span className="rp-field__label">{label}</span>
    </label>
  );
}
