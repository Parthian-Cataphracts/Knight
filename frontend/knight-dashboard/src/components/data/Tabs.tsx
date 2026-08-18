import type { ReactNode } from "react";
import { cn } from "@/lib/utils/cn";

export interface TabDefinition<T extends string> {
  value: T;
  label: string;
  count?: number;
}

/** Underlined tab strip; scrolls horizontally on narrow screens instead of wrapping. */
export function Tabs<T extends string>({
  value,
  options,
  onChange,
}: {
  value: T;
  options: TabDefinition<T>[];
  onChange: (value: T) => void;
}) {
  return (
    <div className="-mx-4 overflow-x-auto border-b border-outline-variant px-4 sm:mx-0 sm:px-0">
      <div className="flex min-w-max gap-1" role="tablist">
        {options.map((option) => (
          <button
            key={option.value}
            type="button"
            role="tab"
            aria-selected={option.value === value}
            onClick={() => onChange(option.value)}
            className={cn(
              "-mb-px whitespace-nowrap border-b-2 px-4 py-3 text-body-sm transition-colors",
              option.value === value
                ? "border-primary font-medium text-primary"
                : "border-transparent text-on-surface-variant hover:text-on-surface",
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
    </div>
  );
}

export function Timeline({
  items,
}: {
  items: { id: string; title: string; meta: string; tone?: "default" | "warning" | "danger" | "success"; body?: ReactNode }[];
}) {
  return (
    <ol className="relative flex flex-col gap-5 ps-5">
      <span aria-hidden className="absolute inset-y-1 start-1.5 w-px bg-outline-variant" />
      {items.map((item) => (
        <li key={item.id} className="relative">
          <span
            aria-hidden
            className={cn(
              "absolute -start-[1.13rem] top-1.5 size-2.5 rounded-full ring-4 ring-[var(--surface-container)]",
              item.tone === "danger"
                ? "bg-error"
                : item.tone === "warning"
                  ? "bg-warning"
                  : item.tone === "success"
                    ? "bg-success"
                    : "bg-primary",
            )}
          />
          <p className="text-body-sm text-on-surface">{item.title}</p>
          <p className="mt-0.5 text-body-sm text-on-surface-variant">{item.meta}</p>
          {item.body ? <div className="mt-2">{item.body}</div> : null}
        </li>
      ))}
    </ol>
  );
}
