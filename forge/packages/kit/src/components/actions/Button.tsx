// Copied from GalaAI/ark packages/ui/src/actions/Button.tsx at 0f4b55c6 for @4ge/kit. Keep visual parity with the source; change it there first or record why here.
import type { ButtonHTMLAttributes, ReactNode } from "react";

/**
 * default = neutral outline · primary = filled bone · danger = rust outline ·
 * accent = the zone pill, mono, always lit.
 *
 * There is no ember variant, deliberately. Ember is warning and gates only.
 * A primary action in ember is a bug, not a style choice.
 */
export type ButtonVariant = "default" | "primary" | "danger" | "accent";

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  children?: ReactNode;
}

export type IconButtonIcon = "search" | "trash" | "upload" | "close";

export interface IconButtonProps extends Omit<
  ButtonProps,
  "aria-label" | "children"
> {
  icon: IconButtonIcon;
  label: string;
}

export function Button({
  variant = "default",
  children,
  className,
  type = "button",
  ...rest
}: ButtonProps) {
  const cls = [
    "rp-btn",
    variant !== "default" ? `rp-btn--${variant}` : "",
    className ?? "",
  ]
    .filter(Boolean)
    .join(" ");
  return (
    <button type={type} className={cls} {...rest}>
      {children}
    </button>
  );
}

function ActionIcon({ icon }: { icon: IconButtonIcon }) {
  const paths = {
    search: (
      <>
        <circle cx="11" cy="11" r="6.5" />
        <path d="m16 16 4 4" />
      </>
    ),
    trash: (
      <>
        <path d="M4 7h16" />
        <path d="M9 7V4h6v3" />
        <path d="m7 7 1 13h8l1-13" />
        <path d="M10 11v5M14 11v5" />
      </>
    ),
    upload: (
      <>
        <path d="M12 16V4" />
        <path d="m7 9 5-5 5 5" />
        <path d="M5 14v5h14v-5" />
      </>
    ),
    close: (
      <>
        <path d="M6 6l12 12" />
        <path d="M18 6 6 18" />
      </>
    ),
  } satisfies Record<IconButtonIcon, ReactNode>;

  return (
    <svg
      viewBox="0 0 24 24"
      width="18"
      height="18"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      {paths[icon]}
    </svg>
  );
}

/**
 * Shared icon-only action control. @4ge/kit owns its glyphs, hit target and
 * accessible label so app surfaces only compose behavior and placement.
 */
export function IconButton({
  icon,
  label,
  title,
  className,
  ...rest
}: IconButtonProps) {
  return (
    <Button
      className={["rp-icon-btn", className ?? ""].filter(Boolean).join(" ")}
      aria-label={label}
      title={title ?? label}
      {...rest}
    >
      <ActionIcon icon={icon} />
    </Button>
  );
}
