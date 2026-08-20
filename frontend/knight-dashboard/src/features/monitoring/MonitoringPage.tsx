import { useTranslation } from "react-i18next";
import { RefreshCw } from "lucide-react";
import { useCollection } from "@/lib/api/hooks";
import type { IntegrationStatus, Store } from "@/lib/api/domain";
import { PageShell, PageHeader, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Card, CardBody, CardHeader } from "@/components/ui/Card";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { formatRelative } from "@/lib/utils/format";

const integrationTone: Record<IntegrationStatus, Tone> = {
  Connected: "success",
  Degraded: "warning",
  Pending: "info",
  Disconnected: "danger",
  NotRegistered: "neutral",
};

/** Alert rules from docs/observability.md section 8, shown as the active rule set. */
const ALERT_RULES = [
  "server.offline",
  "store.unreachable",
  "store.degraded",
  "feature.install.failed",
  "feature.entitled_not_installed",
  "feature.drift",
  "job.stuck",
  "error.spike",
  "backup.failed",
] as const;

export function MonitoringPage() {
  const { t } = useTranslation();
  const stores = useCollection<Store>("/stores");

  const columns: Column<Store>[] = [
    {
      key: "store",
      header: t("monitoring.store"),
      render: (row) => (
        <span className="flex flex-col">
          <span dir="ltr" className="font-mono text-body-sm text-on-surface">
            {row.primaryDomain}
          </span>
          <span className="text-body-sm text-on-surface-variant">{row.customerName}</span>
        </span>
      ),
    },
    {
      key: "environment",
      header: t("stores.environment"),
      render: (row) => t(`environment.${row.environment}`),
    },
    {
      key: "integration",
      header: t("monitoring.reachability"),
      render: (row) => (
        <StatusChip tone={integrationTone[row.integrationStatus]}>
          {t(`integrationStatus.${row.integrationStatus}`)}
        </StatusChip>
      ),
    },
    { key: "version", header: t("stores.version"), mono: true, render: (row) => row.applicationVersion ?? "—" },
    {
      key: "features",
      header: t("monitoring.installedFeatures"),
      numeric: true,
      render: (row) => row.installedFeatureCount ?? "—",
    },
    {
      key: "lastSeen",
      header: t("stores.lastSeen"),
      render: (row) => (row.lastSeenAt ? formatRelative(row.lastSeenAt) : "—"),
    },
  ];

  return (
    <PageShell>
      <PageHeader
        title={t("nav.monitoring")}
        subtitle={t("monitoring.subtitle")}
        actions={
          <Button variant="outline" size="sm" onClick={() => void stores.refetch()}>
            <RefreshCw className="size-4 rtl:-scale-x-100" aria-hidden />
            {t("common.refresh")}
          </Button>
        }
      />

      <CollectionCard query={stores}>
        {(rows) => (
          <>
            <CardHeader title={t("monitoring.storeHealth")} />
            <DataTable
              columns={columns}
              rows={rows}
              rowKey={(row) => row.id}
              cardTitle={(row) => (
                <span dir="ltr" className="font-mono">
                  {row.primaryDomain}
                </span>
              )}
              emptyMessage={t("common.noResults")}
            />
          </>
        )}
      </CollectionCard>

      <Card>
        <CardHeader title={t("monitoring.alertRules")} />
        <CardBody className="flex flex-wrap gap-2">
          {ALERT_RULES.map((rule) => (
            <Mono key={rule} className="rounded-full bg-surface-low px-3 py-1.5">
              {rule}
            </Mono>
          ))}
        </CardBody>
      </Card>
    </PageShell>
  );
}
