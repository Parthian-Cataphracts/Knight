import type { AnchorHTMLAttributes, ReactNode } from "react";
import { Link } from "react-router-dom";
import { cn } from "@/lib/utils/cn";

type Variant = "primary" | "outline";

const base =
  "inline-flex h-11 items-center justify-center gap-2 rounded-md px-4 text-body-sm font-medium transition-colors";

const variants: Record<Variant, string> = {
  primary: "bg-primary text-on-primary hover:opacity-90",
  outline: "border border-outline-variant text-on-surface hover:bg-surface-high",
};

/** A link that looks like a button — the portal navigates with these, and a real
 *  `<button>` must not wrap an anchor. */
export function ButtonLink({
  to,
  href,
  variant = "primary",
  className,
  children,
  ...rest
}: {
  to?: string;
  href?: string;
  variant?: Variant;
  className?: string;
  children: ReactNode;
} & Omit<AnchorHTMLAttributes<HTMLAnchorElement>, "href">) {
  const classes = cn(base, variants[variant], className);

  if (href) {
    return (
      <a className={classes} href={href} {...rest}>
        {children}
      </a>
    );
  }

  return (
    <Link className={classes} to={to ?? "#"}>
      {children}
    </Link>
  );
}
