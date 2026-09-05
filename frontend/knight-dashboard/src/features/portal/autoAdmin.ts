import { useMutation, useQuery } from "@tanstack/react-query";
import { apiRequest } from "@/lib/api/client";

/**
 * The customer's Automatic Admin surface (docs/adr/0038): the autonomy setting
 * and the content runs. The catalogue of parts and their prices comes from the
 * ordinary public plans — each part is a Feature on the à-la-carte plan — so this
 * slice is only the engine itself.
 */

export type Autonomy = "ApprovalRequired" | "FullyAutomatic";

export interface AutoAdminSettings {
  autonomy: Autonomy;
}

export interface ContentDraft {
  kind: string;
  body: string;
  generatorName: string;
}

export interface ContentPublication {
  channelKey: string;
  succeeded: boolean;
  detail: string;
  externalReference: string | null;
  publisherName: string;
  publishedAt: string;
}

export interface ContentRun {
  id: string;
  topic: string;
  autonomy: Autonomy;
  /** "Draft" (awaiting approval), "Published" or "Failed". */
  status: string;
  hasPublicationErrors: boolean;
  drafts: ContentDraft[];
  publications: ContentPublication[];
  createdAt: string;
  updatedAt: string | null;
}

const SETTINGS_KEY = ["portal", "me", "auto-admin", "settings"] as const;
const RUNS_KEY = ["portal", "me", "auto-admin", "runs"] as const;

export function useAutoAdminSettings() {
  return useQuery({
    queryKey: SETTINGS_KEY,
    queryFn: () => apiRequest<AutoAdminSettings>("/me/auto-admin/settings"),
  });
}

export function useSetAutonomy() {
  return useMutation({
    mutationFn: (autonomy: Autonomy) =>
      apiRequest<AutoAdminSettings>("/me/auto-admin/settings", { method: "PUT", body: { autonomy } }),
  });
}

export function useAutoAdminRuns() {
  return useQuery({
    queryKey: RUNS_KEY,
    queryFn: () => apiRequest<ContentRun[]>("/me/auto-admin/runs"),
  });
}

export function useSubmitRun() {
  return useMutation({
    mutationFn: (topic: string) =>
      apiRequest<ContentRun>("/me/auto-admin/runs", { method: "POST", body: { topic } }),
  });
}

export function useApproveRun() {
  return useMutation({
    mutationFn: (runId: string) =>
      apiRequest<ContentRun>(`/me/auto-admin/runs/${runId}/approve`, { method: "POST", body: {} }),
  });
}

export { SETTINGS_KEY as autoAdminSettingsKey, RUNS_KEY as autoAdminRunsKey };
