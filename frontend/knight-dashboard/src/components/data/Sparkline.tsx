import { useId } from "react";

/**
 * Dependency-free area chart. Time flows start→end, so in RTL the whole plot is
 * mirrored (docs/frontend-architecture.md section 4: charts flip with direction).
 */
export function AreaChart({
  series,
  threshold,
  label,
  tone = "primary",
  height = 96,
  unit,
}: {
  series: number[];
  threshold?: number | null | undefined;
  label: string;
  tone?: "primary" | "warning" | "danger" | undefined;
  height?: number | undefined;
  unit?: string | undefined;
}) {
  const id = useId();
  const width = 320;
  const max = Math.max(...series, threshold ?? 0) * 1.15 || 1;
  const stepX = series.length > 1 ? width / (series.length - 1) : width;

  const point = (value: number, index: number): [number, number] => [
    index * stepX,
    height - (value / max) * height,
  ];

  const line = series.map((value, index) => point(value, index).join(",")).join(" ");
  const area = `0,${height} ${line} ${width},${height}`;
  const color =
    tone === "danger" ? "var(--error)" : tone === "warning" ? "var(--warning)" : "var(--primary)";

  const last = series[series.length - 1] ?? 0;

  return (
    <figure className="flex flex-col gap-2">
      <figcaption className="flex items-baseline justify-between gap-2">
        <span className="text-body-sm text-on-surface-variant">{label}</span>
        <span dir="ltr" className="font-mono text-body-sm text-on-surface">
          {last}
          {unit ? ` ${unit}` : ""}
        </span>
      </figcaption>
      <div className="w-full overflow-hidden rounded-md bg-surface-lowest p-2 rtl:-scale-x-100">
        <svg
          viewBox={`0 0 ${width} ${height}`}
          preserveAspectRatio="none"
          className="h-24 w-full"
          role="img"
          aria-label={label}
        >
          <defs>
            <linearGradient id={`fill-${id}`} x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor={color} stopOpacity="0.35" />
              <stop offset="100%" stopColor={color} stopOpacity="0" />
            </linearGradient>
          </defs>
          {threshold ? (
            <line
              x1="0"
              x2={width}
              y1={height - (threshold / max) * height}
              y2={height - (threshold / max) * height}
              stroke="var(--outline)"
              strokeDasharray="4 4"
              strokeWidth="1"
            />
          ) : null}
          <polygon points={area} fill={`url(#fill-${id})`} />
          <polyline points={line} fill="none" stroke={color} strokeWidth="2" vectorEffect="non-scaling-stroke" />
        </svg>
      </div>
    </figure>
  );
}
