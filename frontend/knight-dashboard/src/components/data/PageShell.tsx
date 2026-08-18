import type { ReactNode } from "react";
import { cn } from "@/lib/utils/cn";

export function PageShell({ children }: { children: ReactNode }) {
  return <div className="mx-auto flex w-full max-w-[1400px] flex-col gap-6">{children}</div>;
}

export function PageHeader({
  title,
  subtitle,
  actions,
  breadcrumb,
}: {
  title: string;
  subtitle?: string | undefined;
  actions?: ReactNode | undefined;
  breadcrumb?: string | undefined;
}) {
  return (
    <header className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
      <div className="min-w-0">
        {breadcrumb ? (
          <p className="label-caps mb-1.5 text-on-surface-variant/80">{breadcrumb}</p>
        ) : null}
        <h1 className="text-headline font-semibold text-on-surface">{title}</h1>
        {subtitle ? (
          <p className="mt-1 text-body-sm text-on-surface-variant">{subtitle}</p>
        ) : null}
      </div>
      {actions ? <div className="flex shrink-0 flex-wrap items-center gap-2">{actions}</div> : null}
    </header>
  );
}

export function Toolbar({ children }: { children: ReactNode }) {
  return (
    <div className="flex flex-wrap items-center gap-2 border-b border-outline-variant px-4 py-3 sm:px-5">
      {children}
    </div>
  );
}

export function FilterTabs<T extends string>({
  value,
  options,
  onChange,
}: {
  value: T;
  options: { value: T; label: string; count?: number }[];
  onChange: (value: T) => void;
}) {
  return (
    <div className="flex flex-wrap items-center gap-1" role="tablist">
      {options.map((option) => (
        <button
          key={option.value}
          type="button"
          role="tab"
          aria-selected={option.value === value}
          onClick={() => onChange(option.value)}
          className={cn(
            "rounded-full px-3 py-1.5 text-body-sm transition-colors",
            option.value === value
              ? "bg-primary/15 font-medium text-primary"
              : "text-on-surface-variant hover:bg-surface-high",
          )}
        >
          {option.label}
          {option.count !== undefined ? (
            <span className="ms-1.5 font-mono text-label opacity-70" dir="ltr">
              {option.count}
            </span>
          ) : null}
        </button>
      ))}
    </div>
  );
}

export function KeyValue({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-start justify-between gap-4 py-2.5">
      <dt className="shrink-0 text-body-sm text-on-surface-variant">{label}</dt>
      <dd className="min-w-0 text-end text-body-sm text-on-surface">{children}</dd>
    </div>
  );
}

export function Mono({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <span dir="ltr" className={cn("font-mono text-label text-on-surface-variant", className)}>
      {children}
    </span>
  );
}
