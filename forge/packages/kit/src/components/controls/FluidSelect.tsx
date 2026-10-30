// Copied from GalaAI/ark packages/ui/src/controls/FluidSelect.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
"use client";

/**
 * FluidSelect: the ArkStation single-choice dropdown (operator law, 2026-09-27).
 *
 * Every dropdown that picks one value is this component; every dropdown of
 * actions is DropdownMenu. The behavior is the operator's fluid dropdown: a
 * spring height reveal, staggered options, a highlight that glides between
 * rows, the chevron turning over, and an icon that lifts and takes its tone on
 * hover. The look is Relic Phosphor, and tones are limited to the accent
 * tokens (never ember or rust: those mean warning and error).
 *
 * It is a listbox: arrow keys, Home, End, Enter, Space and Escape all work,
 * and the active row is announced through aria-activedescendant.
 */
import { ChevronDown, type LucideIcon } from "lucide-react";
import {
  AnimatePresence,
  MotionConfig,
  motion,
  useReducedMotion,
} from "motion/react";
import * as React from "react";
import { auraClassName } from "../surfaces/HighlightAura";

const cx = (...parts: Array<string | false | null | undefined>) =>
  parts.filter(Boolean).join(" ");

/** Accent tokens an option may glow in. Ember and rust are reserved. */
export type FluidSelectTone =
  "accent" | "patina" | "coral" | "seal" | "moss" | "army" | "bone";

const TONE_VAR: Record<FluidSelectTone, string> = {
  accent: "var(--rp-accent)",
  patina: "var(--color-patina)",
  coral: "var(--color-coral)",
  seal: "var(--color-seal)",
  moss: "var(--color-moss)",
  army: "var(--color-army)",
  bone: "var(--color-bone)",
};

export interface FluidSelectOption {
  id: string;
  label: string;
  icon?: LucideIcon;
  tone?: FluidSelectTone;
  /** Draw a rule under this option (the operator's "All" divider). */
  separatorAfter?: boolean;
  disabled?: boolean;
  /**
   * Group heading shown above the first option of a run that shares it
   * (SelectField maps native optgroup labels here).
   */
  group?: string;
}

export interface FluidSelectProps {
  /** Visible field label; also the listbox name. */
  label: string;
  /** Hide the label visually but keep it for assistive tech. */
  hideLabel?: boolean;
  options: readonly FluidSelectOption[];
  value?: string;
  defaultValue?: string;
  onValueChange?: (id: string) => void;
  placeholder?: string;
  disabled?: boolean;
  className?: string;
  id?: string;
  /**
   * Form field name. When set, a hidden input carries the chosen id so the
   * value submits with a native form, as a select would.
   */
  name?: string;
  /** Form owner id for the hidden input, as on a native select. */
  form?: string;
  /** Ids of hint or error text, linked to the trigger. */
  "aria-describedby"?: string;
  /** Accessible name override for the trigger, as on a native select. */
  "aria-label"?: string;
  /** Marks the field required for assistive tech and form checks. */
  required?: boolean;
  /** Native tooltip on the trigger. */
  title?: string;
}

/* Disclosure law: synchronized 240ms panel/highlight, 180ms item entry.
   CSS owns the bounded 12ms stagger and reduced-motion item presentation. */
const PANEL_SPRING = { type: "spring", duration: 0.24, bounce: 0 } as const;
const HIGHLIGHT_SPRING = { type: "spring", duration: 0.24, bounce: 0 } as const;

function ToneIcon({
  icon: Icon,
  lit,
  tone,
}: {
  icon: LucideIcon;
  lit: boolean;
  tone: FluidSelectTone;
}) {
  const reducedMotion = useReducedMotion();
  return (
    <motion.span
      className="rp-fluid__icon"
      initial={false}
      animate={{ scale: lit ? 1.2 : 1 }}
      aria-hidden="true"
    >
      <Icon />
      <AnimatePresence>
        {lit && (
          <motion.span
            className="rp-fluid__icon-glow"
            style={{ color: TONE_VAR[tone] }}
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            exit={{ opacity: 0 }}
            transition={{ duration: reducedMotion ? 0 : 0.18, ease: "easeOut" }}
          >
            <Icon strokeWidth={2} />
          </motion.span>
        )}
      </AnimatePresence>
    </motion.span>
  );
}

export function FluidSelect({
  label,
  hideLabel = false,
  options,
  value: valueProp,
  defaultValue,
  onValueChange,
  placeholder = "Choose one",
  disabled = false,
  className,
  id: idProp,
  name,
  form,
  "aria-describedby": describedBy,
  "aria-label": ariaLabel,
  required,
  title,
}: FluidSelectProps) {
  const reducedMotion = useReducedMotion();
  const panelTransition = reducedMotion ? { duration: 0 } : PANEL_SPRING;
  const autoId = React.useId();
  const baseId = idProp ?? `rp-fluid-${autoId}`;
  const labelId = `${baseId}-label`;
  const listId = `${baseId}-list`;
  const optionId = (optionKey: string) => `${baseId}-opt-${optionKey}`;

  const [uncontrolled, setUncontrolled] = React.useState(
    defaultValue ?? options[0]?.id ?? "",
  );
  const value = valueProp ?? uncontrolled;
  const selected = options.find((o) => o.id === value);

  const [open, setOpen] = React.useState(false);
  const [activeId, setActiveId] = React.useState<string | null>(null);
  const [highlight, setHighlight] = React.useState<{
    y: number;
    height: number;
  } | null>(null);

  const rootRef = React.useRef<HTMLDivElement | null>(null);
  const triggerRef = React.useRef<HTMLButtonElement | null>(null);
  const listRef = React.useRef<HTMLDivElement | null>(null);
  const optionRefs = React.useRef(new Map<string, HTMLButtonElement>());

  const enabled = options.filter((o) => !o.disabled);
  const shownId = activeId ?? selected?.id ?? null;

  const choose = (next: FluidSelectOption) => {
    if (next.disabled) return;
    if (valueProp === undefined) setUncontrolled(next.id);
    onValueChange?.(next.id);
    setOpen(false);
    triggerRef.current?.focus();
  };

  const openList = () => {
    if (disabled) return;
    setActiveId(selected?.id ?? enabled[0]?.id ?? null);
    setOpen(true);
  };

  // Click away closes, as in the operator's component.
  React.useEffect(() => {
    if (!open) return;
    const listener = (event: MouseEvent | TouchEvent) => {
      const root = rootRef.current;
      if (root && !root.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", listener);
    document.addEventListener("touchstart", listener);
    return () => {
      document.removeEventListener("mousedown", listener);
      document.removeEventListener("touchstart", listener);
    };
  }, [open]);

  // Focus moves into the listbox on open.
  React.useEffect(() => {
    if (open) listRef.current?.focus();
  }, [open]);

  // The highlight is measured from the real row, so rows and rules may
  // change height without the math drifting.
  React.useLayoutEffect(() => {
    if (!open || !shownId) return;
    const row = optionRefs.current.get(shownId);
    if (row) setHighlight({ y: row.offsetTop, height: row.offsetHeight });
  }, [open, shownId]);

  const move = (step: 1 | -1 | "first" | "last") => {
    if (!enabled.length) return;
    const at = enabled.findIndex((o) => o.id === shownId);
    let next = 0;
    if (step === "first") next = 0;
    else if (step === "last") next = enabled.length - 1;
    else next = (at + step + enabled.length) % enabled.length;
    const target = enabled[next];
    if (!target) return;
    setActiveId(target.id);
    optionRefs.current.get(target.id)?.scrollIntoView({ block: "nearest" });
  };

  const onTriggerKeyDown = (e: React.KeyboardEvent<HTMLButtonElement>) => {
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      openList();
    }
  };

  const onListKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    switch (e.key) {
      case "ArrowDown":
        e.preventDefault();
        move(1);
        break;
      case "ArrowUp":
        e.preventDefault();
        move(-1);
        break;
      case "Home":
        e.preventDefault();
        move("first");
        break;
      case "End":
        e.preventDefault();
        move("last");
        break;
      case "Enter":
      case " ": {
        e.preventDefault();
        const target = options.find((o) => o.id === shownId);
        if (target) choose(target);
        break;
      }
      case "Escape":
        e.preventDefault();
        setOpen(false);
        triggerRef.current?.focus();
        break;
      case "Tab":
        setOpen(false);
        break;
      default:
    }
  };

  return (
    <MotionConfig reducedMotion="user">
      <div
        ref={rootRef}
        className={cx(
          "rp-fluid",
          open && "rp-fluid--open",
          disabled && "rp-fluid--disabled",
          className,
        )}
      >
        <span
          id={labelId}
          className={cx("rp-field__label", hideLabel && "rp-fluid__sr")}
        >
          {label}
        </span>
        <button
          ref={triggerRef}
          type="button"
          className="rp-fluid__trigger"
          aria-haspopup="listbox"
          aria-expanded={open}
          aria-controls={open ? listId : undefined}
          aria-label={ariaLabel}
          aria-labelledby={ariaLabel ? undefined : `${labelId} ${baseId}-value`}
          aria-describedby={describedBy}
          aria-required={required || undefined}
          title={title}
          disabled={disabled}
          onClick={() => (open ? setOpen(false) : openList())}
          onKeyDown={onTriggerKeyDown}
        >
          <span id={`${baseId}-value`} className="rp-fluid__value">
            {selected?.icon && (
              <ToneIcon
                icon={selected.icon}
                lit={false}
                tone={selected.tone ?? "accent"}
              />
            )}
            <span className="rp-fluid__text">
              {selected?.label ?? placeholder}
            </span>
          </span>
          <motion.span
            className="rp-fluid__chevron"
            animate={{ rotate: open ? 180 : 0 }}
            transition={{ duration: reducedMotion ? 0 : 0.12 }}
            aria-hidden="true"
          >
            <ChevronDown />
          </motion.span>
        </button>
        {name !== undefined && (
          <input
            type="hidden"
            name={name}
            form={form}
            value={value}
            disabled={disabled}
          />
        )}

        <AnimatePresence>
          {open && (
            <motion.div
              className="rp-fluid__drop"
              initial={{ opacity: 1, height: 0 }}
              animate={{
                opacity: 1,
                height: "auto",
                transition: panelTransition,
              }}
              exit={{ opacity: 0, height: 0, transition: panelTransition }}
            >
              <div className="rp-fluid__panel">
                <motion.div
                  ref={listRef}
                  id={listId}
                  role="listbox"
                  tabIndex={-1}
                  aria-labelledby={labelId}
                  aria-activedescendant={
                    shownId ? optionId(shownId) : undefined
                  }
                  className="rp-fluid__list"
                  onKeyDown={onListKeyDown}
                >
                  {highlight && (
                    <motion.span
                      className={`rp-fluid__highlight ${auraClassName}`}
                      aria-hidden="true"
                      initial={false}
                      animate={{ y: highlight.y, height: highlight.height }}
                      transition={
                        reducedMotion ? { duration: 0 } : HIGHLIGHT_SPRING
                      }
                    />
                  )}
                  {options.map((option, index) => {
                    const isSelected = option.id === value;
                    const isActive = option.id === shownId;
                    const startsGroup =
                      option.group !== undefined &&
                      option.group !== options[index - 1]?.group;
                    return (
                      <React.Fragment key={option.id}>
                        {startsGroup && (
                          <span className="rp-fluid__group" role="presentation">
                            {option.group}
                          </span>
                        )}
                        <motion.button
                          ref={(node) => {
                            if (node) optionRefs.current.set(option.id, node);
                            else optionRefs.current.delete(option.id);
                          }}
                          id={optionId(option.id)}
                          type="button"
                          role="option"
                          tabIndex={-1}
                          aria-selected={isSelected}
                          aria-disabled={option.disabled || undefined}
                          className={cx(
                            "rp-fluid__option",
                            (isSelected || isActive) && "rp-fluid__option--lit",
                          )}
                          style={
                            {
                              "--disclosure-index": index,
                            } as React.CSSProperties
                          }
                          whileTap={{ scale: 0.98 }}
                          onHoverStart={() => {
                            if (!option.disabled) setActiveId(option.id);
                          }}
                          onHoverEnd={() => setActiveId(null)}
                          onClick={() => choose(option)}
                        >
                          {option.icon && (
                            <ToneIcon
                              icon={option.icon}
                              lit={isActive}
                              tone={option.tone ?? "accent"}
                            />
                          )}
                          <span className="rp-fluid__text">{option.label}</span>
                        </motion.button>
                        {option.separatorAfter && (
                          <motion.span
                            className="rp-fluid__rule"
                            role="presentation"
                            style={
                              {
                                "--disclosure-index": index,
                              } as React.CSSProperties
                            }
                          />
                        )}
                      </React.Fragment>
                    );
                  })}
                </motion.div>
              </div>
            </motion.div>
          )}
        </AnimatePresence>
      </div>
    </MotionConfig>
  );
}
