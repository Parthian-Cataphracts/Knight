import { useState } from "react";
import { useTranslation } from "react-i18next";
import { CheckCircle2, CircleDot, Clock, XCircle, AlertTriangle } from "lucide-react";
import { useAction, useCollection } from "@/lib/api/hooks";
import { PageShell, PageHeader, Toolbar, FilterTabs, KeyValue, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Drawer } from "@/components/data/Drawer";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { useAuthStore } from "@/store/auth";
import { formatRelative } from "@/lib/utils/format";

interface ProvisioningStep {
  sequence: number;
  name: string;
  mode: string;
  status: string;
  detail: string | null;
  errorCode: string | null;
  completedAt: string | null;
}

interface ProvisioningRun {
  id: string;
  storeId: string;
  customerId: string;
  kind: string;
  state: string;
  awaitingOperator: boolean;
  currentStep: string | null;
  completedStepCount: number;
  totalStepCount: number;
  retainUntil: string | null;
  failureCode: string | null;
  failureMessage: string | null;
  createdAt: string;
  completedAt: string | null;
  steps: ProvisioningStep[];
}

type Tab = "all" | "Running" | "AwaitingOperator" | "Failed";

const stateTone: Record<string, Tone> = {
  Running: "info",
  AwaitingOperator: "warning",
  Succeeded: "success",
  Failed: "danger",
  Cancelled: "neutral",
};

const stepIcon: Record<string, typeof CheckCircle2> = {
  Succeeded: CheckCircle2,
  Waiting: CircleDot,
  Running: CircleDot,
  Failed: XCircle,
  Skipped: Clock,
  Pending: Clock,
};

const stepColor: Record<string, string> = {
  Succeeded: "text-success",
  Waiting: "text-info",
  Running: "text-info",
  Failed: "text-error",
  Skipped: "text-on-surface-variant/40",
  Pending: "text-on-surface-variant/40",
};

/**
 * The operator's view of provisioning runs — self-service and operator-started
 * alike (docs/self-service-saas-plan.md §6, G). A run that stalls on a step, or a
 * failure that needs a retry, is what an operator comes here to find and act on;
 * the customer's portal shows the friendly version of the same runs.
 */
export function ProvisioningPage() {
  const { t } = useTranslation();
  const [tab, setTab] = useState<Tab>("all");
  const [selected, setSelected] = useState<ProvisioningRun | null>(null);

  const path = tab === "all" ? "/provisioning" : `/provisioning?state=${tab}`;
  const runs = useCollection<ProvisioningRun>(path);
  const can = useAuthStore((state) => state.can);

  const retry = useAction<unknown, string>((id) => ({ path: `/provisioning/${id}/retry` }), ["/provisioning"]);
  const resume = useAction<unknown, string>((id) => ({ path: `/provisioning/${id}/advance` }), ["/provisioning"]);
  const cancel = useAction<unknown, string>(
    (id) => ({ path: `/provisioning/${id}/cancel`, options: { body: { reason: t("provisioning.cancelReason") } } }),
    ["/provisioning"],
  );

  const columns: Column<ProvisioningRun>[] = [
    {
      key: "store",
      header: t("provisioning.store"),
      render: (row) => <Mono>{row.storeId.slice(0, 8)}</Mono>,
    },
    { key: "kind", header: t("provisioning.run"), render: (row) => t(`provisioningKind.${row.kind}`) },
    {
      key: "state",
      header: t("common.status"),
      render: (row) => (
        <StatusChip tone={stateTone[row.state] ?? "neutral"}>{t(`provisioningState.${row.state}`)}</StatusChip>
      ),
    },
    {
      key: "progress",
      header: t("provisioning.progress"),
      render: (row) => (
        <span className="flex min-w-24 items-center gap-2">
          <span className="h-1.5 flex-1 overflow-hidden rounded-full bg-surface-highest">
            <span
              className={`block h-full rounded-full ${row.state === "Failed" ? "bg-error" : "bg-primary"}`}
              style={{ width: `${row.totalStepCount === 0 ? 0 : (row.completedStepCount / row.totalStepCount) * 100}%` }}
            />
          </span>
          <span dir="ltr" className="font-mono text-label text-on-surface-variant">
            {row.completedStepCount}/{row.totalStepCount}
          </span>
        </span>
      ),
    },
    { key: "started", header: t("provisioning.started"), secondary: true, render: (row) => formatRelative(row.createdAt) },
  ];

  const finished = (run: ProvisioningRun) => ["Succeeded", "Cancelled"].includes(run.state);

  return (
    <PageShell>
      <PageHeader title={t("nav.provisioning")} subtitle={t("provisioning.subtitle")} />

      <CollectionCard
        query={runs}
        toolbar={
          <Toolbar>
            <FilterTabs<Tab>
              value={tab}
              onChange={setTab}
              options={[
                { value: "all", label: t("provisioning.tabAll") },
                { value: "Running", label: t("provisioningState.Running") },
                { value: "AwaitingOperator", label: t("provisioningState.AwaitingOperator") },
                { value: "Failed", label: t("provisioningState.Failed") },
              ]}
            />
          </Toolbar>
        }
      >
        {(rows) => (
          <DataTable
            columns={columns}
            rows={rows}
            rowKey={(row) => row.id}
            onRowClick={setSelected}
            cardTitle={(row) => `${t(`provisioningKind.${row.kind}`)} · ${row.storeId.slice(0, 8)}`}
            emptyMessage={t("common.noResults")}
          />
        )}
      </CollectionCard>

      <Drawer
        open={selected !== null}
        title={selected ? t(`provisioningKind.${selected.kind}`) : ""}
        subtitle={selected?.storeId}
        onClose={() => setSelected(null)}
        footer={
          selected && can("store.provision") && !finished(selected) ? (
            <>
              {selected.state === "Failed" ? (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={retry.isPending}
                  onClick={() => retry.mutate(selected.id)}
                >
                  {t("provisioning.retry")}
                </Button>
              ) : (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={resume.isPending}
                  onClick={() => resume.mutate(selected.id)}
                >
                  {t("provisioning.resume")}
                </Button>
              )}
              <Button
                variant="outline"
                size="sm"
                disabled={cancel.isPending}
                onClick={() => cancel.mutate(selected.id, { onSuccess: () => setSelected(null) })}
              >
                {t("provisioning.cancel")}
              </Button>
            </>
          ) : undefined
        }
      >
        {selected ? (
          <div className="flex flex-col gap-5">
            {(retry.isError || resume.isError || cancel.isError) ? (
              <p role="alert" className="rounded-md bg-error-container px-3 py-2 text-body-sm text-on-error-container">
                {(retry.error ?? resume.error ?? cancel.error)?.message}
              </p>
            ) : null}

            {selected.failureMessage ? (
              <p className="flex items-start gap-2 rounded-md bg-error/10 px-3 py-2.5 text-body-sm text-error">
                <AlertTriangle className="mt-0.5 size-4 shrink-0" aria-hidden />
                {selected.failureMessage}
              </p>
            ) : null}

            <dl className="divide-y divide-outline-variant">
              <KeyValue label={t("common.status")}>
                <StatusChip tone={stateTone[selected.state] ?? "neutral"}>
                  {t(`provisioningState.${selected.state}`)}
                </StatusChip>
              </KeyValue>
              <KeyValue label={t("provisioning.store")}>
                <Mono>{selected.storeId}</Mono>
              </KeyValue>
              <KeyValue label={t("provisioning.progress")}>
                <span dir="ltr" className="font-mono text-label">
                  {selected.completedStepCount}/{selected.totalStepCount}
                </span>
              </KeyValue>
              {selected.currentStep ? (
                <KeyValue label={t("provisioning.waitingFor")}>
                  {t(`provisioningStep.${selected.currentStep}`, selected.currentStep)}
                </KeyValue>
              ) : null}
              {selected.retainUntil ? (
                <KeyValue label={t("provisioning.retainUntil")}>{formatRelative(selected.retainUntil)}</KeyValue>
              ) : null}
              {selected.failureCode ? (
                <KeyValue label={t("provisioning.failure")}>
                  <Mono className="text-error">{selected.failureCode}</Mono>
                </KeyValue>
              ) : null}
            </dl>

            <section>
              <h3 className="label-caps mb-3 text-on-surface-variant/80">{t("provisioning.step")}</h3>
              <ol className="flex flex-col gap-2.5">
                {selected.steps.map((step) => {
                  const Icon = stepIcon[step.status] ?? CircleDot;
                  return (
                    <li key={step.sequence} className="flex items-start gap-3">
                      <Icon className={`mt-0.5 size-4 shrink-0 ${stepColor[step.status] ?? ""}`} aria-hidden />
                      <div className="min-w-0 flex-1">
                        <div className="flex items-baseline justify-between gap-2">
                          <span className="text-body-sm text-on-surface">
                            {t(`provisioningStep.${step.name}`, step.name)}
                          </span>
                          <span className="text-body-sm text-on-surface-variant">
                            {t(`provisioningStepStatus.${step.status}`, step.status)}
                          </span>
                        </div>
                        {step.detail ? (
                          <p className="mt-1 text-body-sm text-on-surface-variant">{step.detail}</p>
                        ) : null}
                      </div>
                    </li>
                  );
                })}
              </ol>
            </section>
          </div>
        ) : null}
      </Drawer>
    </PageShell>
  );
}
