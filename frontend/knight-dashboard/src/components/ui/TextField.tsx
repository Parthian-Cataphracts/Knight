import { useId, type InputHTMLAttributes, type ReactNode } from "react";
import { cn } from "@/lib/utils/cn";

interface TextFieldProps extends InputHTMLAttributes<HTMLInputElement> {
  label: string;
  icon?: ReactNode;
  error?: string | undefined;
}

export function TextField({ label, icon, error, className, ...rest }: TextFieldProps) {
  const id = useId();
  const errorId = `${id}-error`;

  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-body-sm font-medium text-on-surface-variant">
        {label}
      </label>
      <div className="relative">
        {icon ? (
          <span className="pointer-events-none absolute inset-y-0 start-3 flex items-center text-on-surface-variant">
            {icon}
          </span>
        ) : null}
        <input
          {...rest}
          id={id}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? errorId : undefined}
          className={cn(
            "h-11 w-full rounded-md border bg-surface-low px-3 text-body text-on-surface",
            "placeholder:text-on-surface-variant/60",
            "focus:border-primary focus:outline-none focus:ring-2 focus:ring-primary/30",
            icon ? "ps-10" : undefined,
            error ? "border-error" : "border-outline-variant",
            className,
          )}
        />
      </div>
      {error ? (
        <p id={errorId} role="alert" className="text-body-sm text-error">
          {error}
        </p>
      ) : null}
    </div>
  );
}
