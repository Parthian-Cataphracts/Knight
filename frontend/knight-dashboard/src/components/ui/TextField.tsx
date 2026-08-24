import { useId, type InputHTMLAttributes, type ReactNode } from "react";
import { cn } from "@/lib/utils/cn";

interface TextFieldProps extends InputHTMLAttributes<HTMLInputElement> {
  label: string;
  icon?: ReactNode;
  error?: string | undefined;

  /**
   * Shown under the field, and announced with it. For a value whose rules a
   * reader cannot guess from its label - the server refusing it afterwards is
   * a poor way to learn what it wanted.
   */
  hint?: string | undefined;
}

export function TextField({ label, icon, error, hint, className, ...rest }: TextFieldProps) {
  const id = useId();
  const errorId = `${id}-error`;
  const hintId = `${id}-hint`;

  // Both when both are present: the hint still explains the rule the error is
  // about, so dropping it the moment something goes wrong is backwards.
  const describedBy = [hint ? hintId : null, error ? errorId : null].filter(Boolean).join(" ");

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
          aria-describedby={describedBy === "" ? undefined : describedBy}
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
      {hint ? (
        <p id={hintId} className="text-body-sm text-on-surface-variant">
          {hint}
        </p>
      ) : null}

      {error ? (
        <p id={errorId} role="alert" className="text-body-sm text-error">
          {error}
        </p>
      ) : null}
    </div>
  );
}
