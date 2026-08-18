import type { Tone } from "@/components/ui/StatusChip";
import type { InstallationState, JobStatus } from "@/lib/api/domain";

export const installationTone: Record<InstallationState, Tone> = {
  Installed: "success",
  Installing: "info",
  Updating: "info",
  Pending: "info",
  RollingBack: "warning",
  Uninstalling: "warning",
  Disabled: "neutral",
  NotInstalled: "neutral",
  Failed: "danger",
};

export const jobTone: Record<JobStatus, Tone> = {
  Succeeded: "success",
  Running: "info",
  Claimed: "info",
  Queued: "neutral",
  Cancelled: "neutral",
  Failed: "danger",
  TimedOut: "danger",
};
