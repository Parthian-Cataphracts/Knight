import { useState } from "react";
import { useTranslation } from "react-i18next";
import { PlayCircle, ShieldOff, RotateCcw, RefreshCw, CheckCircle2, ShieldCheck } from "lucide-react";
import { useAction, useCollection } from "@/lib/api/hooks";
import type { ProvisioningJob, ProvisioningStep, Store, StoreBackup } from "@/lib/api/domain";
import { Card, CardBody, CardHeader } from "@/components/ui/Card";
import { Button } from "@/components/ui/Button";
import { TextField } from "@/components/ui/TextField";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { DataTable, type Column } from "@/components/data/DataTable";
import { CollectionCard } from "@/components/data/CollectionCard";
import { KeyValue } from "@/components/data/PageShell";
import { useAuthStore } from "@/store/auth";
import { formatDateTime, formatRelative } from "@/lib/utils/format";

const stepTone: Record<ProvisioningStep["status"], Tone> = {
  Pending: "neutral",
  Waiting: "warning",
  Succeeded: "success",
  Failed: "danger",
  Skipped: "neutral",
};

const runTone: Record<ProvisioningJob["state"], Tone> = {
  Running: "info",
  AwaitingOperator: "warning",
  Succeeded: "success",
  Failed: "danger",
  Cancelled: "neutral",
};

const backupTone: Record<StoreBackup["status"], Tone> = {
  Succeeded: "success",
  Failed: "danger",
  Running: "info",
};

/**
 * The provisioning run for one store, and the backups it has reported.
 *
 * The screen is built around a single question: what is this store waiting for?
 * So the step a run is sitting on is shown with its own detail text — "waiting
 * for the agent on the store's server to enrol" — rather than a bare status, and
 * the action offered is the one that step actually needs. A manual step gets a
 * button; an automatic one gets a re-check, because nothing an operator can
 * click will make a store report healthy.
 */
export function ProvisioningPanel({ store }: { store: Store }) {
  const { t } = useTranslation();
  const can = useAuthStore((state) => state.can);

  const jobs = useCollection<ProvisioningJob>(`/provisioning?storeId=${store.id}&pageSize=5`);
  const backups = useCollection<StoreBackup>(`/stores/${store.id}/backups?limit=10`);

  const [detail, setDetail] = useState("");
  const [baseImage, setBaseImage] = useState("");
  const [thumbprint, setThumbprint] = useState(store.mutualTlsThumbprint ?? "");

  const refresh = async () => {
    await Promise.all([jobs.refetch(), backups.refetch()]);
  };

  const start = useAction<ProvisioningJob, "provision" | "deprovision">(
    (kind) => ({
      path: kind === "provision"
        ? `/provisioning/stores/${store.id}`
        : `/provisioning/stores/${store.id}/deprovision`,
      options: { body: {} },
    }),
    ["/provisioning", "/stores"],
  );

  const completeStep = useAction<ProvisioningJob, { jobId: string; step: string }>(
    ({ jobId, step }) => ({
      path: `/provisioning/${jobId}/steps`,
      options: {
        body: {
          step,
          detail: detail.trim() === "" ? null : detail.trim(),
          baseImageVersion: baseImage.trim() === "" ? null : baseImage.trim(),
        },
      },
    }),
    ["/provisioning", "/stores"],
  );

  const advance = useAction<ProvisioningJob, string>(
    (jobId) => ({ path: `/provisioning/${jobId}/advance` }),
    ["/provisioning", "/stores"],
  );

  const retry = useAction<ProvisioningJob, string>(
    (jobId) => ({ path: `/provisioning/${jobId}/retry` }),
    ["/provisioning", "/stores"],
  );

  const setMutualTls = useAction<Store, string | null>(
    (value) => ({
      path: `/stores/${store.id}/mutual-tls`,
      options: { method: "PUT", body: { thumbprint: value } },
    }),
    ["/stores"],
  );

  const run = (jobs.data ?? [])[0];
  const current = run?.steps.find((step) => step.name === run.currentStep);
  const isManual = current?.mode === "Manual";

  // Mutual TLS is a promise about dedicated infrastructure. Offering the field
  // on shared hosting would be offering something the API correctly refuses.
  const supportsMutualTls = store.hostingModel !== "SharedManaged";

  const stepColumns: Column<ProvisioningStep>[] = [
    { key: "sequence", header: "#", render: (row) => String(row.sequence) },
    { key: "name", header: t("provisioning.step"), mono: true, render: (row) => t(`provisioningStep.${row.name}`, row.name) },
    {
      key: "mode",
      header: t("provisioning.mode"),
      render: (row) => (
        <StatusChip tone={row.mode === "Manual" ? "warning" : "neutral"}>
          {t(`provisioningMode.${row.mode}`)}
        </StatusChip>
      ),
    },
    {
      key: "status",
      header: t("common.status"),
      render: (row) => (
        <StatusChip tone={stepTone[row.status]}>{t(`provisioningStepStatus.${row.status}`)}</StatusChip>
      ),
    },
    { key: "detail", header: t("provisioning.detail"), secondary: true, render: (row) => row.detail ?? "—" },
    {
      key: "completed",
      header: t("provisioning.completedAt"),
      render: (row) => (row.completedAt ? formatRelative(row.completedAt) : "—"),
    },
  ];

  const backupColumns: Column<StoreBackup>[] = [
    {
      key: "status",
      header: t("common.status"),
      render: (row) => <StatusChip tone={backupTone[row.status]}>{t(`backupStatus.${row.status}`)}</StatusChip>,
    },
    { key: "kind", header: t("backups.kind"), render: (row) => t(`backupKind.${row.kind}`) },
    { key: "startedAt", header: t("backups.startedAt"), render: (row) => formatDateTime(row.startedAt) },
    {
      key: "size",
      header: t("backups.size"),
      render: (row) => (row.sizeBytes === null ? "—" : `${Math.round(row.sizeBytes / 1_048_576)} MB`),
    },
    {
      key: "duration",
      header: t("backups.duration"),
      render: (row) => (row.durationSeconds === null ? "—" : t("backups.seconds", { count: row.durationSeconds })),
    },
    { key: "location", header: t("backups.location"), secondary: true, mono: true, render: (row) => row.location ?? "—" },
  ];

  return (
    <div className="flex flex-col gap-6">
      <Card>
        <CardHeader
          title={t("provisioning.title")}
          icon={<PlayCircle className="size-5" />}
          action={
            <span className="flex flex-wrap gap-2">
              {run && !["Succeeded", "Cancelled"].includes(run.state) ? (
                <Button
                  size="sm"
                  variant="outline"
                  disabled={advance.isPending}
                  onClick={() => advance.mutate(run.id, { onSuccess: () => void refresh() })}
                >
                  <RefreshCw className="size-4 rtl:-scale-x-100" aria-hidden />
                  {t("provisioning.recheck")}
                </Button>
              ) : null}

              {run?.state === "Failed" ? (
                <Button
                  size="sm"
                  variant="outline"
                  disabled={retry.isPending}
                  onClick={() => retry.mutate(run.id, { onSuccess: () => void refresh() })}
                >
                  <RotateCcw className="size-4 rtl:-scale-x-100" aria-hidden />
                  {t("provisioning.retry")}
                </Button>
              ) : null}

              {can("store.provision") ? (
                <Button
                  size="sm"
                  disabled={start.isPending}
                  onClick={() => start.mutate("provision", { onSuccess: () => void refresh() })}
                >
                  {t("provisioning.start")}
                </Button>
              ) : null}

              {can("store.deprovision") && store.status !== "Archived" ? (
                <Button
                  size="sm"
                  variant="danger"
                  disabled={start.isPending}
                  onClick={() => start.mutate("deprovision", { onSuccess: () => void refresh() })}
                >
                  <ShieldOff className="size-4" aria-hidden />
                  {t("provisioning.deprovision")}
                </Button>
              ) : null}
            </span>
          }
        />

        <CardBody className="flex flex-col gap-4">
          {run ? (
            <>
              <dl className="divide-y divide-outline-variant">
                <KeyValue label={t("provisioning.run")}>
                  <span className="flex items-center gap-2">
                    <StatusChip tone={runTone[run.state]}>{t(`provisioningState.${run.state}`)}</StatusChip>
                    <span className="text-body-sm text-on-surface-variant">
                      {t(`provisioningKind.${run.kind}`)} · {run.completedStepCount}/{run.totalStepCount}
                    </span>
                  </span>
                </KeyValue>

                <KeyValue label={t("provisioning.waitingFor")}>
                  {current
                    ? `${t(`provisioningStep.${current.name}`, current.name)} — ${current.detail ?? ""}`
                    : t("provisioning.nothing")}
                </KeyValue>

                {run.baseImageVersion ? (
                  <KeyValue label={t("provisioning.baseImage")}>{run.baseImageVersion}</KeyValue>
                ) : null}

                {run.retainUntil ? (
                  <KeyValue label={t("provisioning.retainUntil")}>{formatDateTime(run.retainUntil)}</KeyValue>
                ) : null}

                {run.failureMessage ? (
                  <KeyValue label={t("provisioning.failure")}>{run.failureMessage}</KeyValue>
                ) : null}
              </dl>

              {isManual && can("store.provision") ? (
                <div className="rounded-md border border-outline-variant p-3">
                  {/* Only a manual step is offered here. An automatic one is a
                      fact KNIGHT checks, and a button that claimed to complete
                      it would be a button that lies. */}
                  <p className="text-body-sm text-on-surface">
                    {t("provisioning.manualPrompt", {
                      step: t(`provisioningStep.${current!.name}`, current!.name),
                    })}
                  </p>

                  <div className="mt-3 grid gap-3 md:grid-cols-2">
                    <TextField
                      label={t("provisioning.detail")}
                      value={detail}
                      onChange={(event) => setDetail(event.target.value)}
                      placeholder={t("provisioning.detailPlaceholder")}
                    />

                    {current!.name === "instance" ? (
                      <TextField
                        label={t("provisioning.baseImage")}
                        value={baseImage}
                        dir="ltr"
                        onChange={(event) => setBaseImage(event.target.value)}
                        placeholder="2.3.0"
                      />
                    ) : null}
                  </div>

                  <Button
                    className="mt-3"
                    size="sm"
                    disabled={completeStep.isPending}
                    onClick={() =>
                      completeStep.mutate(
                        { jobId: run.id, step: current!.name },
                        {
                          onSuccess: () => {
                            setDetail("");
                            setBaseImage("");
                            void refresh();
                          },
                        },
                      )
                    }
                  >
                    <CheckCircle2 className="size-4" aria-hidden />
                    {t("provisioning.markDone")}
                  </Button>

                  {completeStep.isError ? (
                    <p className="mt-2 text-body-sm text-error">{completeStep.error.message}</p>
                  ) : null}
                </div>
              ) : null}

              <DataTable
                columns={stepColumns}
                rows={[...run.steps].sort((a, b) => a.sequence - b.sequence)}
                rowKey={(row) => row.name}
                cardTitle={(row) => t(`provisioningStep.${row.name}`, row.name)}
                emptyMessage={t("common.noResults")}
              />
            </>
          ) : (
            <p className="text-body-sm text-on-surface-variant">{t("provisioning.none")}</p>
          )}

          {start.isError ? <p className="text-body-sm text-error">{start.error.message}</p> : null}
        </CardBody>
      </Card>

      {supportsMutualTls && can("store.credentials.manage") ? (
        <Card>
          <CardHeader title={t("mutualTls.title")} icon={<ShieldCheck className="size-5" />} />
          <CardBody className="flex flex-col gap-3">
            <p className="text-body-sm text-on-surface-variant">{t("mutualTls.explain")}</p>

            <TextField
              label={t("mutualTls.thumbprint")}
              value={thumbprint}
              dir="ltr"
              onChange={(event) => setThumbprint(event.target.value)}
              placeholder="sha256 hex"
            />

            <div className="flex gap-2">
              <Button
                size="sm"
                disabled={setMutualTls.isPending}
                onClick={() => setMutualTls.mutate(thumbprint.trim() === "" ? null : thumbprint.trim())}
              >
                {t("common.save")}
              </Button>

              {store.requiresMutualTls ? (
                <Button
                  size="sm"
                  variant="outline"
                  disabled={setMutualTls.isPending}
                  onClick={() => {
                    setThumbprint("");
                    setMutualTls.mutate(null);
                  }}
                >
                  {t("mutualTls.clear")}
                </Button>
              ) : null}
            </div>

            {setMutualTls.isError ? (
              <p className="text-body-sm text-error">{setMutualTls.error.message}</p>
            ) : null}
          </CardBody>
        </Card>
      ) : null}

      <CollectionCard query={backups}>
        {(rows) => (
          <>
            <CardHeader title={t("backups.title")} />
            <DataTable
              columns={backupColumns}
              rows={rows}
              rowKey={(row) => row.id}
              cardTitle={(row) => formatDateTime(row.startedAt)}
              emptyMessage={t("backups.none")}
            />
            <CardBody className="border-t border-outline-variant text-body-sm text-on-surface-variant">
              {t("backups.note")}
            </CardBody>
          </>
        )}
      </CollectionCard>
    </div>
  );
}
