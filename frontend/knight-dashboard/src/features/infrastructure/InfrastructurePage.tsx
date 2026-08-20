import { useState } from "react";
import { useTranslation } from "react-i18next";
import { RefreshCw, Server as ServerIcon } from "lucide-react";
import { useAction, useCollection } from "@/lib/api/hooks";
import { apiRequest } from "@/lib/api/client";
import { AreaChart } from "@/components/data/Sparkline";
import type { Server } from "@/lib/api/domain";
import type { HealthState } from "@/lib/api/types";
import { PageShell, PageHeader, KeyValue, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Drawer } from "@/components/data/Drawer";
import { useAuthStore } from "@/store/auth";
import { Card, CardHeader } from "@/components/ui/Card";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { TextField } from "@/components/ui/TextField";
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

  const can = useAuthStore((state) => state.can);
  const [registering, setRegistering] = useState(false);

  // A provisioning token is shown exactly once: it is burned on first use, and
  // KNIGHT keeps only its hash. Holding it in state to display is the only
  // chance anyone has to copy it.
  const [issuedToken, setIssuedToken] = useState<string | null>(null);

  const provisionAgent = useAction<{ token: string }, string>(
    (serverId) => ({ path: `/servers/${serverId}/agents` }),
    ["/servers"],
  );

  const decommission = useAction<unknown, string>(
    (serverId) => ({ path: `/servers/${serverId}/decommission` }),
    ["/servers"],
  );
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
          <>
            {can("server.manage") ? (
              <Button size="sm" onClick={() => setRegistering(true)}>
                {t("infrastructure.registerServer")}
              </Button>
            ) : null}
            <Button variant="outline" size="sm" onClick={() => void servers.refetch()}>
              <RefreshCw className="size-4 rtl:-scale-x-100" aria-hidden />
              {t("common.refresh")}
            </Button>
          </>
        }
      />

      <RegisterServerForm
        open={registering}
        onClose={() => setRegistering(false)}
        onRegistered={() => {
          setRegistering(false);
          void servers.refetch();
        }}
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
        footer={
          can("agent.manage") && selected ? (
            <>
              <Button
                size="sm"
                disabled={provisionAgent.isPending}
                onClick={() =>
                  provisionAgent.mutate(selected.id, {
                    onSuccess: (issued) => setIssuedToken(issued.token),
                  })
                }
              >
                {t("infrastructure.addAgent")}
              </Button>

              <Button
                variant="outline"
                size="sm"
                disabled={decommission.isPending}
                onClick={() =>
                  decommission.mutate(selected.id, { onSuccess: () => setSelected(null) })
                }
              >
                {t("infrastructure.decommission")}
              </Button>
            </>
          ) : undefined
        }
        open={selected !== null}
        title={selected?.name ?? ""}
        subtitle={selected ? t(`hosting.${selected.hostingModel}`) : undefined}
        onClose={() => setSelected(null)}
      >
        {selected ? (
          <div className="flex flex-col gap-6">
            {issuedToken ? (
              <div className="rounded-md border border-warning/40 bg-warning/10 p-3">
                <p className="text-body-sm text-on-surface">{t("infrastructure.tokenShownOnce")}</p>
                <p dir="ltr" className="mt-2 break-all font-mono text-label text-on-surface">
                  {issuedToken}
                </p>
              </div>
            ) : null}

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

/**
 * Registering a machine.
 *
 * A server is registered before any agent exists for it: the record is what a
 * provisioning token is issued against, so the order cannot be reversed.
 */
function RegisterServerForm({
  open,
  onClose,
  onRegistered,
}: {
  open: boolean;
  onClose: () => void;
  onRegistered: () => void;
}) {
  const { t } = useTranslation();
  const [name, setName] = useState("");
  const [hostingModel, setHostingModel] = useState("SharedManaged");
  const [environment, setEnvironment] = useState("Production");
  const [provider, setProvider] = useState("");
  const [region, setRegion] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const submit = async () => {
    setSaving(true);
    setError(null);

    try {
      await apiRequest("/servers", {
        method: "POST",
        body: {
          name,
          hostingModel,
          environment,
          provider: provider || undefined,
          region: region || undefined,
        },
      });

      setName("");
      setProvider("");
      setRegion("");
      onRegistered();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Drawer
      open={open}
      title={t("infrastructure.registerServer")}
      onClose={onClose}
      footer={
        <Button size="sm" disabled={saving || name.trim().length === 0} onClick={() => void submit()}>
          {t("common.save")}
        </Button>
      }
    >
      <div className="flex flex-col gap-4">
        {error ? (
          <p role="alert" className="rounded-md bg-error-container px-3 py-2 text-body-sm text-on-error-container">
            {error}
          </p>
        ) : null}

        <TextField label={t("common.name")} value={name} onChange={(event) => setName(event.target.value)} />

        <fieldset className="flex flex-col gap-2">
          <legend className="text-body-sm font-medium text-on-surface-variant">
            {t("infrastructure.hostingModel")}
          </legend>
          <div className="flex flex-wrap gap-2">
            {["SharedManaged", "DedicatedManaged", "CustomerManaged"].map((option) => (
              <Button
                key={option}
                type="button"
                size="sm"
                variant={hostingModel === option ? "primary" : "outline"}
                onClick={() => setHostingModel(option)}
              >
                {t(`hosting.${option}`)}
              </Button>
            ))}
          </div>
        </fieldset>

        <fieldset className="flex flex-col gap-2">
          <legend className="text-body-sm font-medium text-on-surface-variant">
            {t("stores.environment")}
          </legend>
          <div className="flex flex-wrap gap-2">
            {["Development", "Staging", "Production"].map((option) => (
              <Button
                key={option}
                type="button"
                size="sm"
                variant={environment === option ? "primary" : "outline"}
                onClick={() => setEnvironment(option)}
              >
                {t(`environment.${option}`)}
              </Button>
            ))}
          </div>
        </fieldset>

        <TextField
          label={t("infrastructure.provider")}
          value={provider}
          onChange={(event) => setProvider(event.target.value)}
        />

        <TextField
          label={t("infrastructure.region")}
          value={region}
          onChange={(event) => setRegion(event.target.value)}
        />
      </div>
    </Drawer>
  );
}
