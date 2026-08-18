import type { ButtonHTMLAttributes, ReactNode } from "react";
import { cn } from "@/lib/utils/cn";

type Variant = "primary" | "outline" | "ghost" | "danger";
type Size = "sm" | "md";

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  size?: Size;
  loading?: boolean;
  children: ReactNode;
}

const variants: Record<Variant, string> = {
  primary: "bg-primary text-on-primary hover:opacity-90 border border-transparent",
  outline: "border border-outline-variant text-on-surface hover:bg-surface-high",
  ghost: "text-on-surface-variant hover:bg-surface-high hover:text-on-surface",
  danger: "bg-error text-on-error hover:opacity-90 border border-transparent",
};

const sizes: Record<Size, string> = {
  sm: "h-9 px-3 text-body-sm gap-1.5",
  md: "h-11 px-4 text-body-sm gap-2",
};

export function Button({
  variant = "primary",
  size = "md",
  loading = false,
  className,
  disabled,
  children,
  ...rest
}: ButtonProps) {
  return (
    <button
      {...rest}
      disabled={disabled === true || loading}
      className={cn(
        "inline-flex items-center justify-center rounded-md font-medium transition-colors",
        "disabled:cursor-not-allowed disabled:opacity-50",
        variants[variant],
        sizes[size],
        className,
      )}
    >
      {loading ? (
        <span
          aria-hidden
          className="size-4 animate-spin rounded-full border-2 border-current border-e-transparent"
        />
      ) : null}
      {children}
    </button>
  );
}
