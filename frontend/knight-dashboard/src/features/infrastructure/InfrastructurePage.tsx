import { useState } from "react";
import { useTranslation } from "react-i18next";
import { RefreshCw, Server as ServerIcon } from "lucide-react";
import { useCollection } from "@/lib/api/hooks";
import { AreaChart } from "@/components/data/Sparkline";
import type { Server } from "@/lib/api/domain";
import type { HealthState } from "@/lib/api/types";
import { PageShell, PageHeader, KeyValue, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Drawer } from "@/components/data/Drawer";
import { Card, CardHeader } from "@/components/ui/Card";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Meter } from "@/components/ui/Meter";
import { Button } from "@/components/ui/Button";
import { formatPercent } from "@/lib/utils/format";

interface PlatformService {
  key: string;
  name: string;
  detail: string;
  status: HealthState;
  metrics: [string, string][];
}

const healthTone: Record<HealthState, Tone> = {
  Healthy: "success",
  Degraded: "warning",
  Offline: "danger",
  Unknown: "neutral",
};

export function InfrastructurePage() {
  const { t } = useTranslation();
  const services = useCollection<PlatformService>("/infrastructure/services");
  const servers = useCollection<Server>("/servers");
  const [selected, setSelected] = useState<Server | null>(null);
  const metrics = useCollection<{ cpu: number[]; memory: number[] }>(
    `/servers/${selected?.id ?? "none"}/metrics`,
    selected !== null,
  );
  const series = metrics.data?.[0];

  const columns: Column<Server>[] = [
    {
      key: "name",
      header: t("infrastructure.server"),
      render: (row) => (
        <span className="flex flex-col">
          <span dir="ltr" className="font-mono text-body-sm text-on-surface">
            {row.name}
          </span>
          <span className="text-body-sm text-on-surface-variant">
            {t(`hosting.${row.hostingModel}`)}
          </span>
        </span>
      ),
    },
    {
      key: "environment",
      header: t("stores.environment"),
      render: (row) => t(`environment.${row.environment}`),
    },
    {
      key: "status",
      header: t("common.status"),
      render: (row) => (
        <StatusChip tone={healthTone[row.status]}>{t(`health.${row.status}`)}</StatusChip>
      ),
    },
    { key: "ip", header: t("infrastructure.ip"), mono: true, secondary: true, render: (row) => row.ipAddress },
    {
      key: "load",
      header: t("infrastructure.load"),
      mono: true,
      render: (row) => `${row.cpuPercent}% / ${row.memoryPercent}% / ${row.diskPercent}%`,
    },
    {
      key: "stores",
      header: t("infrastructure.stores"),
      numeric: true,
      render: (row) => row.storeCount,
    },
    {
      key: "agent",
      header: t("infrastructure.agent"),
      mono: true,
      secondary: true,
      render: (row) => row.agentVersion ?? "—",
    },
  ];

  return (
    <PageShell>
      <PageHeader
        title={t("nav.infrastructure")}
        subtitle={t("infrastructure.subtitle")}
        actions={
          <Button variant="outline" size="sm" onClick={() => void servers.refetch()}>
            <RefreshCw className="size-4 rtl:-scale-x-100" aria-hidden />
            {t("common.refresh")}
          </Button>
        }
      />

      <CollectionCard query={services}>
        {(rows) => (
          <>
            <CardHeader title={t("infrastructure.platformServices")} />
            <div className="grid grid-cols-1 gap-3 p-5 sm:grid-cols-2 xl:grid-cols-3">
              {rows.map((service) => (
                <div key={service.key} className="rounded-md bg-surface-low p-4">
                  <div className="flex items-start justify-between gap-2">
                    <div className="min-w-0">
                      <p className="truncate text-body-sm font-medium text-on-surface">
                        {service.name}
                      </p>
                      <p className="truncate text-body-sm text-on-surface-variant">
                        {service.detail}
                      </p>
                    </div>
                    <StatusChip tone={healthTone[service.status]}>
                      {t(`health.${service.status}`)}
                    </StatusChip>
                  </div>
                  <dl className="mt-3 flex flex-wrap gap-x-5 gap-y-1">
                    {service.metrics.map(([label, value]) => (
                      <div key={label} className="flex items-baseline gap-1.5">
                        <dt className="label-caps text-on-surface-variant/80">{label}</dt>
                        <dd dir="ltr" className="font-mono text-label text-on-surface">
                          {value}
                        </dd>
                      </div>
                    ))}
                  </dl>
                </div>
              ))}
            </div>
          </>
        )}
      </CollectionCard>

      <CollectionCard query={servers}>
        {(rows) => (
          <>
            <CardHeader title={t("infrastructure.servers")} icon={<ServerIcon className="size-5" />} />
            <DataTable
              columns={columns}
              rows={rows}
              rowKey={(row) => row.id}
              onRowClick={setSelected}
              cardTitle={(row) => (
                <span dir="ltr" className="font-mono">
                  {row.name}
                </span>
              )}
              emptyMessage={t("common.noResults")}
            />
          </>
        )}
      </CollectionCard>

      <Drawer
        open={selected !== null}
        title={selected?.name ?? ""}
        subtitle={selected ? t(`hosting.${selected.hostingModel}`) : undefined}
        onClose={() => setSelected(null)}
      >
        {selected ? (
          <div className="flex flex-col gap-6">
            {series ? (
              <div className="flex flex-col gap-4">
                <AreaChart
                  series={series.cpu}
                  label={t("infrastructure.cpuTrend")}
                  unit="%"
                  tone={selected.cpuPercent > 80 ? "danger" : "primary"}
                />
                <AreaChart series={series.memory} label={t("infrastructure.memoryTrend")} unit="%" />
              </div>
            ) : null}
            <div className="flex flex-col gap-4">
              <Meter label={t("dashboard.cpu")} value={selected.cpuPercent} tone={selected.cpuPercent > 80 ? "danger" : "primary"} />
              <Meter label={t("dashboard.memory")} value={selected.memoryPercent} tone={selected.memoryPercent > 75 ? "warning" : "primary"} />
              <Meter label={t("dashboard.disk")} value={selected.diskPercent} />
            </div>
            <dl className="divide-y divide-outline-variant">
              <KeyValue label={t("common.status")}>
                <StatusChip tone={healthTone[selected.status]}>
                  {t(`health.${selected.status}`)}
                </StatusChip>
              </KeyValue>
              <KeyValue label={t("infrastructure.ip")}>
                <Mono>{selected.ipAddress}</Mono>
              </KeyValue>
              <KeyValue label={t("infrastructure.uptime")}>
                {formatPercent(selected.uptimePercent)}
              </KeyValue>
              <KeyValue label={t("infrastructure.agent")}>
                <Mono>{selected.agentVersion ?? "—"}</Mono>
              </KeyValue>
              <KeyValue label={t("infrastructure.stores")}>{selected.storeCount}</KeyValue>
            </dl>
            <Card className="p-4 text-body-sm text-on-surface-variant">
              {t("infrastructure.agentNote")}
            </Card>
          </div>
        ) : null}
      </Drawer>
    </PageShell>
  );
}
