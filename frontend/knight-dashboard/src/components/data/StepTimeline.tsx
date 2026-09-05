import type { ReactNode } from "react";
import { CheckCircle2, CircleDot, Clock, XCircle } from "lucide-react";

/**
 * One row of a job's progress: a step, where it got to, and what it said.
 *
 * `status` is whatever vocabulary the source speaks — a provisioning run's
 * `Succeeded`/`Waiting`, an installation job's `Running`, the portal's lowercase
 * `succeeded` — and is normalised here rather than at every call site.
 */
export interface StepTimelineItem {
  id: string | number;
  label: ReactNode;
  status: string;
  detail?: ReactNode;
  /** Rendered at the end of the header row instead of the status label — a duration, say. */
  trailing?: ReactNode;
}

type Canonical = "succeeded" | "running" | "waiting" | "failed" | "skipped" | "pending";

const icon: Record<Canonical, typeof CheckCircle2> = {
  succeeded: CheckCircle2,
  running: CircleDot,
  waiting: CircleDot,
  failed: XCircle,
  skipped: Clock,
  pending: Clock,
};

const color: Record<Canonical, string> = {
  succeeded: "text-success",
  running: "text-info",
  waiting: "text-info",
  failed: "text-error",
  skipped: "text-on-surface-variant/40",
  pending: "text-on-surface-variant/40",
};

/** Maps every status a step source uses onto the six the icons cover. */
function canonical(status: string): Canonical {
  switch (status.trim().toLowerCase()) {
    case "succeeded":
    case "success":
    case "done":
      return "succeeded";
    case "failed":
    case "error":
      return "failed";
    case "skipped":
      return "skipped";
    case "running":
    case "active":
      return "running";
    case "waiting":
      return "waiting";
    default:
      return "pending";
  }
}

/**
 * A job's steps as a vertical timeline — the one place per-step progress is
 * drawn, so provisioning, installation and the customer portal render a run the
 * same way instead of each maintaining its own icon map and markup.
 *
 * `statusLabel` turns a raw status into the words shown at the end of a row; a
 * caller that would rather show something else (a duration) passes `trailing` on
 * the item instead.
 */
export function StepTimeline({
  steps,
  statusLabel,
}: {
  steps: StepTimelineItem[];
  statusLabel?: (status: string) => ReactNode;
}) {
  return (
    <ol className="flex flex-col gap-2.5">
      {steps.map((step) => {
        const key = canonical(step.status);
        const Icon = icon[key];

        return (
          <li key={step.id} className="flex items-start gap-3">
            <Icon className={`mt-0.5 size-4 shrink-0 ${color[key]}`} aria-hidden />
            <div className="min-w-0 flex-1">
              <div className="flex items-baseline justify-between gap-2">
                <span className="text-body-sm text-on-surface">{step.label}</span>
                {step.trailing ??
                  (statusLabel ? (
                    <span className="shrink-0 text-body-sm text-on-surface-variant">{statusLabel(step.status)}</span>
                  ) : null)}
              </div>
              {step.detail ? (
                // A div, not a p: a caller may pass a styled block (a code
                // panel), and a block inside a p is invalid markup.
                <div className="mt-1 text-body-sm text-on-surface-variant">{step.detail}</div>
              ) : null}
            </div>
          </li>
        );
      })}
    </ol>
  );
}
