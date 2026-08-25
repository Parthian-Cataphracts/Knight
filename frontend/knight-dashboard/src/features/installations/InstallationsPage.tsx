import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { AlertTriangle, CheckCircle2, CircleDot, RotateCcw, XCircle, Clock } from "lucide-react";
<<<<<<< HEAD
import { useAction, useCollection, useResource } from "@/lib/api/hooks";
import { useRealtimeRefresh } from "@/lib/realtime/useRealtime";
import { isRealtimeConnected } from "@/lib/realtime/connection";
import type { Installation, Job, JobDetail, JobStep } from "@/lib/api/domain";
import type { InstallPlan } from "@/lib/api/fixtures-detail";
=======
import { useQuery } from "@tanstack/react-query";
import { useAction, useCollection, useResource } from "@/lib/api/hooks";
import { apiRequest } from "@/lib/api/client";
import { useRealtimeRefresh } from "@/lib/realtime/useRealtime";
import { isRealtimeConnected } from "@/lib/realtime/connection";
import type { InstallPlan, Installation, Job, JobDetail, JobStep } from "@/lib/api/domain";

>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
import { PageShell, PageHeader, Toolbar, FilterTabs, KeyValue, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Drawer } from "@/components/data/Drawer";
import { StatusChip } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { useAuthStore } from "@/store/auth";
import { formatRelative } from "@/lib/utils/format";
import { installationTone, jobTone } from "./installationTone";
import { InstallPreviewDialog } from "./InstallPreviewDialog";

type Tab = "installations" | "jobs";

const stepIcon = {
  Succeeded: CheckCircle2,
  Running: CircleDot,
  Failed: XCircle,
  Skipped: Clock,
} as const;

const stepColor = {
  Succeeded: "text-success",
  Running: "text-info",
  Failed: "text-error",
  Skipped: "text-on-surface-variant/40",
} as const;

/**
 * A job's steps.
 *
 * Fetched from the job's own endpoint rather than carried on the list: a page of
 * jobs would otherwise ship every step of every job to draw one progress bar,
 * and the steps are only ever read one job at a time.
 */
function JobProgress({ job }: { job: Job }) {
  const { t } = useTranslation();
  const detail = useResource<JobDetail>(`/jobs/${job.id}`);

  // The drawer is open on one job while it is running, so its steps follow the
  // same pushes the lists do.
  useRealtimeRefresh(["jobProgress", "jobCompleted"], [`/jobs/${job.id}`]);
  const steps = detail.data?.steps ?? [];

  if (steps.length === 0) {
    return (
      <p className="text-body-sm text-on-surface-variant">
        {t("jobs.noSteps")} ({job.completedStepCount}/{job.totalStepCount})
      </p>
    );
  }

  return (
    <ol className="flex flex-col gap-2.5">
      {steps.map((step: JobStep) => {
        const Icon = stepIcon[step.status];
        return (
          <li key={step.sequence} className="flex items-start gap-3">
            <Icon className={`mt-0.5 size-4 shrink-0 ${stepColor[step.status]}`} aria-hidden />
            <div className="min-w-0 flex-1">
              <div className="flex items-baseline justify-between gap-2">
                <span dir="ltr" className="font-mono text-label text-on-surface">
                  {step.sequence}. {step.name}
                </span>
                <span className="text-body-sm text-on-surface-variant">
                  {t(`stepStatus.${step.status}`)}
                </span>
              </div>
              {step.output ? (
                <p
                  dir="ltr"
                  className="mt-1 overflow-x-auto rounded bg-surface-lowest px-2 py-1.5 font-mono text-label text-on-surface-variant"
                >
                  {step.output}
                </p>
              ) : null}
            </div>
          </li>
        );
      })}
    </ol>
  );
}

/**
 * Installation state per store, and the jobs that change it. Entitlement and
 * installation are separate columns on purpose — a paid-for feature that is not
 * running must be visible, not hidden behind a single toggle.
 */
export function InstallationsPage() {
  const { t } = useTranslation();
  const [tab, setTab] = useState<Tab>("installations");
  const installations = useCollection<Installation>("/installations");
  const jobs = useCollection<Job>("/jobs");
  const can = useAuthStore((state) => state.can);
  const [selectedJob, setSelectedJob] = useState<Job | null>(null);
  const [selectedInstallation, setSelectedInstallation] = useState<Installation | null>(null);
  const [previewFor, setPreviewFor] = useState<Installation | null>(null);

  // Every installation action takes the store and feature rather than the
  // installation id: the delivery engine keys on the pair, because an
  // installation record may not exist yet for something never installed.
  const installAction = useAction<unknown, { action: string; storeId: string; featureId: string }>(
    ({ action, storeId, featureId }) => ({
      path: `/installations/${action}`,
      options: { body: { storeId, featureId } },
    }),
    ["/installations", "/jobs"],
  );

  const cancelJob = useAction<unknown, string>(
    (id) => ({ path: `/jobs/${id}/cancel` }),
    ["/jobs", "/installations"],
  );

  // A job is the one thing in KNIGHT an operator watches happen: it runs for
  // minutes on somebody else's machine and can fail halfway. The push says the
  // data is stale and the lists refetch — so somebody who had the tab closed
  // sees the same thing as somebody who watched it.
  useRealtimeRefresh(
    ["jobProgress", "jobCompleted", "featureInstallationStateChanged"],
    ["/jobs", "/installations"],
  );
<<<<<<< HEAD
  const preview = useResource<InstallPlan>(
    `/stores/${previewFor?.storeId ?? "none"}/features/${previewFor?.featureId ?? "none"}/plan`,
    previewFor !== null,
  );
=======
  // The dry run is a POST, because it takes a body: the store, the Feature and
  // an optional version range. It reads nothing and changes nothing, which is
  // why it is safe to run every time the dialog opens.
  const preview = useQuery({
    queryKey: ["install-plan", previewFor?.storeId, previewFor?.featureSlug],
    enabled: previewFor !== null,
    queryFn: () =>
      apiRequest<InstallPlan>("/installations/plan", {
        method: "POST",
        body: {
          storeId: previewFor?.storeId,
          slug: previewFor?.featureSlug,
          versionRange: null,
        },
      }),
  });
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5

  const installationColumns: Column<Installation>[] = [
    {
      key: "store",
      header: t("installations.store"),
      render: (row) => (
        <span dir="ltr" className="font-mono text-body-sm text-on-surface">
          {row.storeName}
        </span>
      ),
    },
    {
      key: "feature",
      header: t("installations.feature"),
      render: (row) => (
        <span className="flex flex-col">
          <span className="text-on-surface">{row.featureName}</span>
          <Mono>{row.featureSlug}</Mono>
        </span>
      ),
    },
    {
      key: "entitlement",
      header: t("installations.entitlement"),
      render: (row) => (
        <StatusChip tone={row.entitled ? "success" : "neutral"}>
          {row.entitled ? t("installations.entitled") : t("installations.notEntitled")}
        </StatusChip>
      ),
    },
    {
      key: "state",
      header: t("installations.installation"),
      render: (row) => (
        <StatusChip tone={installationTone[row.state]}>
          {t(`installationState.${row.state}`)}
        </StatusChip>
      ),
    },
    {
      key: "version",
      header: t("installations.version"),
      mono: true,
      render: (row) =>
        row.installedVersion
          ? row.targetVersion && row.targetVersion !== row.installedVersion
            ? `${row.installedVersion} → ${row.targetVersion}`
            : row.installedVersion
          : (row.targetVersion ?? "—"),
    },
    {
      key: "changed",
      header: t("installations.lastChange"),
      secondary: true,
      render: (row) => formatRelative(row.lastTransitionAt),
    },
  ];

  const jobColumns: Column<Job>[] = [
    { key: "type", header: t("jobs.type"), render: (row) => t(`jobType.${row.type}`) },
    {
      key: "target",
      header: t("jobs.target"),
      render: (row) => (
        <span className="flex flex-col">
          <span className="text-on-surface">
            {row.featureSlug}
            {row.targetVersion ? ` ${row.targetVersion}` : ""}
          </span>
          <Mono>{row.storeName ?? row.storeId}</Mono>
        </span>
      ),
    },
    {
      key: "status",
      header: t("common.status"),
      render: (row) => (
        <StatusChip tone={jobTone[row.state]}>{t(`jobStatus.${row.state}`)}</StatusChip>
      ),
    },
    {
      key: "progress",
      header: t("jobs.progress"),
      render: (row) => (
        <span className="flex min-w-24 items-center gap-2">
          <span className="h-1.5 flex-1 overflow-hidden rounded-full bg-surface-highest">
            <span
              className={`block h-full rounded-full ${row.state === "Failed" ? "bg-error" : "bg-primary"}`}
              style={{
                width: `${row.totalStepCount === 0 ? 0 : (row.completedStepCount / row.totalStepCount) * 100}%`,
              }}
            />
          </span>
          <span dir="ltr" className="font-mono text-label text-on-surface-variant">
            {row.completedStepCount}/{row.totalStepCount}
          </span>
        </span>
      ),
    },
    {
      key: "queued",
      header: t("jobs.queuedAt"),
      secondary: true,
      render: (row) => formatRelative(row.queuedAt),
    },
  ];

  return (
    <PageShell>
      <PageHeader
        title={t("nav.installations")}
        subtitle={t("installations.subtitle")}
        actions={<LiveIndicator />}
      />

      {tab === "installations" ? (
        <CollectionCard
          query={installations}
          toolbar={
            <Toolbar>
              <FilterTabs<Tab>
                value={tab}
                onChange={setTab}
                options={[
                  { value: "installations", label: t("installations.tabInstallations") },
                  { value: "jobs", label: t("installations.tabJobs") },
                ]}
              />
            </Toolbar>
          }
        >
          {(rows) => (
            <DataTable
              columns={installationColumns}
              rows={rows}
              rowKey={(row) => row.id}
              onRowClick={setSelectedInstallation}
              cardTitle={(row) => row.featureName ?? row.featureSlug}
              emptyMessage={t("common.noResults")}
            />
          )}
        </CollectionCard>
      ) : (
        <CollectionCard
          query={jobs}
          toolbar={
            <Toolbar>
              <FilterTabs<Tab>
                value={tab}
                onChange={setTab}
                options={[
                  { value: "installations", label: t("installations.tabInstallations") },
                  { value: "jobs", label: t("installations.tabJobs") },
                ]}
              />
            </Toolbar>
          }
        >
          {(rows) => (
            <DataTable
              columns={jobColumns}
              rows={rows}
              rowKey={(row) => row.id}
              onRowClick={setSelectedJob}
              cardTitle={(row) => row.featureSlug}
              emptyMessage={t("common.noResults")}
            />
          )}
        </CollectionCard>
      )}

      <Drawer
        open={selectedInstallation !== null}
        title={selectedInstallation?.featureName ?? selectedInstallation?.featureSlug ?? ""}
        subtitle={selectedInstallation?.storeName ?? undefined}
        onClose={() => setSelectedInstallation(null)}
        footer={
          can("installation.manage") && selectedInstallation ? (
            <>
              {selectedInstallation.state === "Installed" ? (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={installAction.isPending}
                  onClick={() =>
                    installAction.mutate({
                      action: "disable",
                      storeId: selectedInstallation.storeId,
                      featureId: selectedInstallation.featureId,
                    })
                  }
                >
                  {t("installations.disable")}
                </Button>
              ) : null}

              {selectedInstallation.state === "Disabled" ? (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={installAction.isPending}
                  onClick={() =>
                    installAction.mutate({
                      action: "enable",
                      storeId: selectedInstallation.storeId,
                      featureId: selectedInstallation.featureId,
                    })
                  }
                >
                  {t("installations.enable")}
                </Button>
              ) : null}

              {/* Rollback exists only where there is something to go back to.
                  Offering it otherwise would promise a previous version that
                  was never installed. */}
              {selectedInstallation.previousVersion !== null &&
              can("installation.rollback") ? (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={installAction.isPending}
                  onClick={() =>
                    installAction.mutate({
                      action: "rollback",
                      storeId: selectedInstallation.storeId,
                      featureId: selectedInstallation.featureId,
                    })
                  }
                >
                  {t("installations.rollback")}
                </Button>
              ) : null}

              {/* Uninstalling removes code and keeps data: a customer who
                  resubscribes must find their data where they left it
                  (docs/adr/0016-feature-migration-and-removal-policy.md). */}
              {selectedInstallation.installedVersion !== null &&
              can("installation.uninstall") ? (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={installAction.isPending}
                  onClick={() =>
                    installAction.mutate({
                      action: "uninstall",
                      storeId: selectedInstallation.storeId,
                      featureId: selectedInstallation.featureId,
                    })
                  }
                >
                  {t("installations.uninstall")}
                </Button>
              ) : null}

              {/* The preview dialog is deliberately the only route to
                  installing: a dependency plan and a compatibility verdict are
                  things an operator must see before code moves. */}
              <Button
                size="sm"
                onClick={() => {
                  setPreviewFor(selectedInstallation);
                  setSelectedInstallation(null);
                }}
              >
                {selectedInstallation.installedVersion === null
                  ? t("installations.install")
                  : t("installations.upgrade")}
              </Button>
            </>
          ) : undefined
        }
      >
        {selectedInstallation ? (
          <div className="flex flex-col gap-5">
            {installAction.isError ? (
              <p
                role="alert"
                className="rounded-md bg-error-container px-3 py-2 text-body-sm text-on-error-container"
              >
                {installAction.error.message}
              </p>
            ) : null}

            {selectedInstallation.blockingReason ? (
              <p className="flex items-start gap-2 rounded-md bg-error/10 px-3 py-2.5 text-body-sm text-error">
                <AlertTriangle className="mt-0.5 size-4 shrink-0" aria-hidden />
                {selectedInstallation.blockingReason}
              </p>
            ) : null}

            {selectedInstallation.rollbackOutcome === "ManualInterventionRequired" ? (
              <p className="flex items-start gap-2 rounded-md bg-warning/10 px-3 py-2.5 text-body-sm text-warning">
                <RotateCcw className="mt-0.5 size-4 shrink-0" aria-hidden />
                {t("installations.manualIntervention")}
              </p>
            ) : null}

            <dl className="divide-y divide-outline-variant">
              <KeyValue label={t("installations.entitlement")}>
                <StatusChip tone={selectedInstallation.entitled ? "success" : "neutral"}>
                  {selectedInstallation.entitled
                    ? t("installations.entitled")
                    : t("installations.notEntitled")}
                </StatusChip>
              </KeyValue>
              <KeyValue label={t("installations.installation")}>
                <StatusChip tone={installationTone[selectedInstallation.state]}>
                  {t(`installationState.${selectedInstallation.state}`)}
                </StatusChip>
              </KeyValue>
              <KeyValue label={t("installations.enabled")}>
                {selectedInstallation.isEnabled ? t("common.yes") : t("common.no")}
              </KeyValue>
              <KeyValue label={t("installations.installedVersion")}>
                <Mono>{selectedInstallation.installedVersion ?? "—"}</Mono>
              </KeyValue>
              <KeyValue label={t("installations.desiredVersion")}>
                <Mono>{selectedInstallation.targetVersion ?? "—"}</Mono>
              </KeyValue>
              <KeyValue label={t("installations.health")}>
                {t(`health.${selectedInstallation.health}`)}
              </KeyValue>
              <KeyValue label={t("installations.lastChange")}>
                {formatRelative(selectedInstallation.lastTransitionAt)}
              </KeyValue>
            </dl>
          </div>
        ) : null}
      </Drawer>

      <Drawer
        open={selectedJob !== null}
        title={selectedJob ? t(`jobType.${selectedJob.type}`) : ""}
        subtitle={selectedJob?.featureSlug}
        onClose={() => setSelectedJob(null)}
        footer={
          selectedJob && can("job.manage") && selectedJob.state === "Queued" ? (
            <Button
              variant="outline"
              size="sm"
              disabled={cancelJob.isPending}
              onClick={() => cancelJob.mutate(selectedJob.id, { onSuccess: () => setSelectedJob(null) })}
            >
              {t("jobs.cancel")}
            </Button>
          ) : undefined
        }
      >
        {selectedJob ? (
          <div className="flex flex-col gap-5">
            <dl className="divide-y divide-outline-variant">
              <KeyValue label={t("common.status")}>
                <StatusChip tone={jobTone[selectedJob.state]}>
                  {t(`jobStatus.${selectedJob.state}`)}
                </StatusChip>
              </KeyValue>
              <KeyValue label={t("jobs.store")}>
                <Mono>{selectedJob.storeName ?? selectedJob.storeId}</Mono>
              </KeyValue>
              <KeyValue label={t("jobs.progress")}>
                <span dir="ltr" className="font-mono text-label">
                  {selectedJob.completedStepCount}/{selectedJob.totalStepCount}
                </span>
              </KeyValue>
              <KeyValue label={t("jobs.attempts")}>
                <span dir="ltr" className="font-mono text-label">
                  {selectedJob.attemptCount}/{selectedJob.maxAttempts}
                </span>
              </KeyValue>
              <KeyValue label={t("jobs.trigger")}>{selectedJob.trigger}</KeyValue>
              <KeyValue label={t("jobs.queuedAt")}>{formatRelative(selectedJob.queuedAt)}</KeyValue>
              {selectedJob.failureCode ? (
                <KeyValue label={t("jobs.errorCode")}>
                  <Mono className="text-error">{selectedJob.failureCode}</Mono>
                </KeyValue>
              ) : null}
              {selectedJob.failureMessage ? (
                <KeyValue label={t("jobs.errorMessage")}>{selectedJob.failureMessage}</KeyValue>
              ) : null}
              {selectedJob.rollbackOutcome !== "NotAttempted" ? (
                <KeyValue label={t("jobs.rollback")}>
                  <StatusChip
                    tone={selectedJob.rollbackOutcome === "RolledBack" ? "success" : "warning"}
                  >
                    {t(`rollbackOutcome.${selectedJob.rollbackOutcome}`)}
                  </StatusChip>
                </KeyValue>
              ) : null}
              <KeyValue label={t("common.correlationId")}>
                <Mono>{selectedJob.correlationId}</Mono>
              </KeyValue>
            </dl>

            <section>
              <h3 className="label-caps mb-3 text-on-surface-variant/80">{t("jobs.steps")}</h3>
              <JobProgress job={selectedJob} />
            </section>
          </div>
        ) : null}
      </Drawer>

      <InstallPreviewDialog
        open={previewFor !== null}
        plan={preview.data ?? null}
        storeName={previewFor?.storeName ?? ""}
        featureName={previewFor?.featureName ?? previewFor?.featureSlug ?? ""}
        onClose={() => setPreviewFor(null)}
        onConfirm={() => setPreviewFor(null)}
      />
    </PageShell>
  );
}

/**
 * Says whether the screen is following changes or waiting for a refresh.
 *
 * Worth showing because the two look identical when nothing is happening, and
 * an operator staring at a stalled job needs to know which of the two they are
 * looking at.
 */
function LiveIndicator() {
  const { t } = useTranslation();
  const [live, setLive] = useState(isRealtimeConnected());

  useEffect(() => {
    const timer = setInterval(() => setLive(isRealtimeConnected()), 2000);

    return () => clearInterval(timer);
  }, []);

  return (
    <span className="flex items-center gap-2 text-body-sm text-on-surface-variant">
      <span
        aria-hidden
        className={`size-2 rounded-full ${live ? "bg-success" : "bg-on-surface-variant/40"}`}
      />
      {live ? t("jobs.live") : t("jobs.notLive")}
    </span>
  );
}
