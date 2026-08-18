import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import {
  Building2,
  Store,
  AlertTriangle,
  PlayCircle,
  Server,
  History,
  PackageX,
} from "lucide-react";
import { apiRequest } from "@/lib/api/client";
import type { DashboardOverview, HealthState } from "@/lib/api/types";
import { Card, CardBody, CardHeader } from "@/components/ui/Card";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Meter } from "@/components/ui/Meter";
import { LoadingBlock, ErrorBlock, EmptyBlock } from "@/components/ui/StateBlock";
import { formatNumber, formatRelative } from "@/lib/utils/format";

const healthTone: Record<HealthState, Tone> = {
  Healthy: "success",
  Degraded: "warning",
  Offline: "danger",
  Unknown: "neutral",
};

const severityTone = { critical: "danger", warning: "warning", info: "info" } as const;

function StatTile({
  label,
  value,
  hint,
  icon: Icon,
  tone = "neutral",
}: {
  label: string;
  value: string;
  hint?: string;
  icon: typeof Building2;
  tone?: Tone;
}) {
  const accent =
    tone === "danger"
      ? "text-error"
      : tone === "warning"
        ? "text-warning"
        : tone === "success"
          ? "text-success"
          : "text-primary";

  return (
    <Card className="p-5">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-body-sm text-on-surface-variant">{label}</p>
          <p className="mt-2 text-headline font-semibold text-on-surface">{value}</p>
          {hint ? <p className="mt-1 text-body-sm text-on-surface-variant">{hint}</p> : null}
        </div>
        <span className={`grid size-10 shrink-0 place-items-center rounded-md bg-surface-high ${accent}`}>
          <Icon className="size-5" aria-hidden />
        </span>
      </div>
    </Card>
  );
}

export function DashboardPage() {
  const { t } = useTranslation();

  const query = useQuery({
    queryKey: ["monitoring", "overview"],
    queryFn: () => apiRequest<DashboardOverview>("/monitoring/overview"),
  });

  return (
    <div className="mx-auto flex w-full max-w-[1400px] flex-col gap-6">
      <header>
        <h1 className="text-headline font-semibold text-on-surface">{t("dashboard.title")}</h1>
        <p className="mt-1 text-body-sm text-on-surface-variant">{t("dashboard.subtitle")}</p>
      </header>

      {query.isPending ? (
        <Card>
          <LoadingBlock rows={6} />
        </Card>
      ) : query.isError ? (
        <Card>
          <ErrorBlock message={(query.error as Error).message} onRetry={() => void query.refetch()} />
        </Card>
      ) : (
        <>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
            <StatTile
              label={t("dashboard.activeCustomers")}
              value={formatNumber(query.data.customers.active)}
              hint={`${formatNumber(query.data.customers.suspended)} ${t("dashboard.suspended")}`}
              icon={Building2}
            />
            <StatTile
              label={t("dashboard.connectedStores")}
              value={`${formatNumber(query.data.stores.connected)} / ${formatNumber(query.data.stores.total)}`}
              icon={Store}
              tone="success"
            />
            <StatTile
              label={t("dashboard.openAlerts")}
              value={formatNumber(query.data.alerts.open)}
              icon={AlertTriangle}
              tone={query.data.alerts.critical > 0 ? "danger" : "warning"}
            />
            <StatTile
              label={t("dashboard.runningJobs")}
              value={formatNumber(query.data.featureDelivery.runningJobs)}
              icon={PlayCircle}
            />
          </div>

          <div className="grid grid-cols-1 gap-6 xl:grid-cols-3">
            <Card className="xl:col-span-2">
              <CardHeader title={t("dashboard.services")} icon={<Server className="size-5" />} />
              <CardBody className="grid grid-cols-1 gap-3 sm:grid-cols-2">
                {query.data.services.map((service) => (
                  <div
                    key={service.name}
                    className="flex items-center justify-between gap-3 rounded-md bg-surface-low px-4 py-3"
                  >
                    <span className="truncate text-body-sm text-on-surface">{service.name}</span>
                    <span className="flex shrink-0 items-center gap-2">
                      {service.latencyMs !== null ? (
                        <span className="font-mono text-label text-on-surface-variant" dir="ltr">
                          {service.latencyMs}ms
                        </span>
                      ) : null}
                      <StatusChip tone={healthTone[service.state]}>
                        {t(`health.${service.state}`)}
                      </StatusChip>
                    </span>
                  </div>
                ))}
              </CardBody>
            </Card>

            <Card>
              <CardHeader title={t("dashboard.resources")} />
              <CardBody className="flex flex-col gap-5">
                <Meter label={t("dashboard.cpu")} value={query.data.resources.cpuPercent} />
                <Meter
                  label={t("dashboard.memory")}
                  value={query.data.resources.memoryPercent}
                  tone={query.data.resources.memoryPercent > 75 ? "warning" : "primary"}
                />
                <Meter label={t("dashboard.disk")} value={query.data.resources.diskPercent} />
              </CardBody>
            </Card>
          </div>

          <div className="grid grid-cols-1 gap-6 xl:grid-cols-3">
            <Card>
              <CardHeader
                title={t("dashboard.featureDelivery")}
                icon={<PackageX className="size-5" />}
              />
              <CardBody className="flex flex-col gap-3">
                <div className="flex items-center justify-between gap-3">
                  <span className="text-body-sm text-on-surface-variant">
                    {t("dashboard.failedInstalls")}
                  </span>
                  <StatusChip
                    tone={query.data.featureDelivery.failedInstallations > 0 ? "danger" : "success"}
                  >
                    {formatNumber(query.data.featureDelivery.failedInstallations)}
                  </StatusChip>
                </div>
                <div className="flex items-center justify-between gap-3">
                  <span className="text-body-sm text-on-surface-variant">
                    {t("dashboard.entitledNotInstalled")}
                  </span>
                  <StatusChip
                    tone={
                      query.data.featureDelivery.entitledNotInstalled > 0 ? "warning" : "success"
                    }
                  >
                    {formatNumber(query.data.featureDelivery.entitledNotInstalled)}
                  </StatusChip>
                </div>
                <div className="flex items-center justify-between gap-3">
                  <span className="text-body-sm text-on-surface-variant">
                    {t("dashboard.runningJobs")}
                  </span>
                  <StatusChip tone="info">
                    {formatNumber(query.data.featureDelivery.runningJobs)}
                  </StatusChip>
                </div>
              </CardBody>
            </Card>

            <Card>
              <CardHeader
                title={t("dashboard.alerts")}
                icon={<AlertTriangle className="size-5" />}
              />
              {query.data.openAlerts.length === 0 ? (
                <EmptyBlock>{t("dashboard.noAlerts")}</EmptyBlock>
              ) : (
                <ul className="divide-y divide-outline-variant">
                  {query.data.openAlerts.map((alert) => (
                    <li key={alert.id} className="flex flex-col gap-1.5 px-5 py-4">
                      <div className="flex items-center justify-between gap-2">
                        <span className="truncate text-body-sm font-medium text-on-surface">
                          {alert.title}
                        </span>
                        <StatusChip tone={severityTone[alert.severity]}>
                          {t(`severity.${alert.severity}`)}
                        </StatusChip>
                      </div>
                      <p className="text-body-sm text-on-surface-variant">{alert.detail}</p>
                      <time className="font-mono text-label text-on-surface-variant" dir="ltr">
                        {formatRelative(alert.raisedAt)}
                      </time>
                    </li>
                  ))}
                </ul>
              )}
            </Card>

            <Card>
              <CardHeader title={t("dashboard.activity")} icon={<History className="size-5" />} />
              <ul className="divide-y divide-outline-variant">
                {query.data.recentActivity.map((entry) => (
                  <li key={entry.id} className="flex flex-col gap-1 px-5 py-4">
                    <span className="label-caps text-primary">{entry.action}</span>
                    <span className="truncate text-body-sm text-on-surface">{entry.target}</span>
                    <span className="text-body-sm text-on-surface-variant">
                      {entry.actor} · {formatRelative(entry.occurredAt)}
                    </span>
                  </li>
                ))}
              </ul>
            </Card>
          </div>
        </>
      )}
    </div>
  );
}
