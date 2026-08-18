import { cn } from "@/lib/utils/cn";
import { formatPercent } from "@/lib/utils/format";

export function Meter({
  label,
  value,
  tone = "primary",
}: {
  label: string;
  value: number;
  tone?: "primary" | "warning" | "danger";
}) {
  const bar = tone === "danger" ? "bg-error" : tone === "warning" ? "bg-warning" : "bg-primary";

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-baseline justify-between gap-2">
        <span className="text-body-sm text-on-surface-variant">{label}</span>
        <span className="font-mono text-body-sm text-on-surface" dir="ltr">
          {formatPercent(value)}
        </span>
      </div>
      <div
        role="meter"
        aria-valuenow={value}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-label={label}
        className="h-2 w-full overflow-hidden rounded-full bg-surface-highest"
      >
        <div className={cn("h-full rounded-full", bar)} style={{ width: value + "%" }} />
      </div>
    </div>
  );
}
