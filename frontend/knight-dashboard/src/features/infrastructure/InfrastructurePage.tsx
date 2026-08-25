import { useState } from "react";
import { useTranslation } from "react-i18next";
import { RefreshCw, Server as ServerIcon } from "lucide-react";
<<<<<<< HEAD
import { useAction, useCollection } from "@/lib/api/hooks";
import { apiRequest } from "@/lib/api/client";
import { AreaChart } from "@/components/data/Sparkline";
import type { Server } from "@/lib/api/domain";
=======
import { useAction, useCollection, useResource } from "@/lib/api/hooks";
import { apiRequest } from "@/lib/api/client";
import { AreaChart } from "@/components/data/Sparkline";
import type { Customer, FleetOverview, FleetServer, Server } from "@/lib/api/domain";
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
import type { HealthState } from "@/lib/api/types";
import { PageShell, PageHeader, KeyValue, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Drawer } from "@/components/data/Drawer";
<<<<<<< HEAD
=======
import { EditDrawer } from "@/features/shared/EditDrawer";
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
import { useAuthStore } from "@/store/auth";
import { Card, CardHeader } from "@/components/ui/Card";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { TextField } from "@/components/ui/TextField";
import { Meter } from "@/components/ui/Meter";
import { Button } from "@/components/ui/Button";
<<<<<<< HEAD
import { formatPercent } from "@/lib/utils/format";
=======
import { formatRelative } from "@/lib/utils/format";
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5

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

<<<<<<< HEAD
  const can = useAuthStore((state) => state.can);
  const [registering, setRegistering] = useState(false);
=======
  // Load figures are not on /servers and never were. The fleet overview reports
  // every machine in one batched call, so this is one request whatever the size
  // of the fleet - the shape the phase 10 work settled on rather than a request
  // per row.
  const fleet = useResource<FleetOverview>("/monitoring/fleet");
  const load = new Map<string, FleetServer>((fleet.data?.servers ?? []).map((s) => [s.id, s]));

  // Which machine belongs to whom. A dedicated server hosts one customer's
  // stores and nobody else's, and until now the dashboard never said whose.
  const customers = useCollection<Customer>("/customers");
  const customerName = (id: string | null) =>
    id === null ? null : (customers.data ?? []).find((c) => c.id === id)?.name ?? id;

  const can = useAuthStore((state) => state.can);
  const [registering, setRegistering] = useState(false);
  const [editing, setEditing] = useState<Server | null>(null);
  const [dedicating, setDedicating] = useState<Server | null>(null);
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5

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

  // Revocation takes effect on the agent's very next heartbeat, not at its next
  // restart: a compromised agent must stop being trusted immediately.
  const revokeAgent = useAction<unknown, string>(
    (agentId) => ({
      path: `/servers/agents/${agentId}/revoke`,
      options: { body: { reason: "Revoked from the dashboard." } },
    }),
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
<<<<<<< HEAD
    { key: "ip", header: t("infrastructure.ip"), mono: true, secondary: true, render: (row) => row.ipAddress },
=======
    {
      key: "owner",
      header: t("infrastructure.dedicatedTo"),
      render: (row) => {
        const owner = customerName(row.dedicatedCustomerId);
        return owner === null ? (
          <span className="text-on-surface-variant">{t("infrastructure.shared")}</span>
        ) : (
          <StatusChip tone="info">{owner}</StatusChip>
        );
      },
    },
    { key: "ip", header: t("infrastructure.ip"), mono: true, secondary: true, render: (row) => row.ipAddress ?? "—" },
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
    {
      key: "load",
      header: t("infrastructure.load"),
      mono: true,
<<<<<<< HEAD
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
=======
      render: (row) => {
        const current = load.get(row.id);

        // A machine whose agent has never reported has no load, which is not the
        // same as a load of zero and must not be drawn as one.
        return current === undefined ||
          current.cpuPercent === null ||
          current.memoryPercent === null ||
          current.diskPercent === null
          ? "—"
          : `${Math.round(current.cpuPercent)}% / ${Math.round(current.memoryPercent)}% / ${Math.round(current.diskPercent)}%`;
      },
    },
    {
      key: "region",
      header: t("infrastructure.region"),
      secondary: true,
      render: (row) => [row.provider, row.region].filter(Boolean).join(" · ") || "—",
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
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

<<<<<<< HEAD
=======
      <EditDrawer
        open={editing !== null}
        title={t("infrastructure.editServer")}
        subtitle={editing?.name}
        path={`/servers/${editing?.id ?? ""}`}
        method="PUT"
        fields={[
          { key: "name", label: t("common.name"), value: editing?.name ?? "", ltr: true },
          {
            key: "provider",
            label: t("infrastructure.provider"),
            value: editing?.provider ?? "",
            required: false,
            placeholder: "hetzner",
          },
          {
            key: "region",
            label: t("infrastructure.region"),
            value: editing?.region ?? "",
            required: false,
            placeholder: "fsn1",
          },
          {
            key: "ipAddress",
            label: t("infrastructure.ip"),
            value: editing?.ipAddress ?? "",
            ltr: true,
            required: false,
            placeholder: "203.0.113.10",
          },
        ]}
        onClose={() => setEditing(null)}
        onSaved={() => {
          setEditing(null);
          setSelected(null);
          void servers.refetch();
        }}
      />

      <EditDrawer
        open={dedicating !== null}
        title={t("infrastructure.dedicate")}
        subtitle={dedicating?.name}
        path={`/servers/${dedicating?.id ?? ""}/dedication`}
        method="PUT"
        fields={[
          {
            key: "customerId",
            label: t("customers.name"),
            value: dedicating?.dedicatedCustomerId ?? "",
            required: false,
            // Empty means shared, and the API types the id as nullable - so it
            // has to arrive as null rather than as an empty string.
            nullWhenEmpty: true,
            choices: [
              { value: "", label: t("infrastructure.shared") },
              ...(customers.data ?? []).map((customer) => ({
                value: customer.id,
                label: customer.name,
              })),
            ],
            note: t("infrastructure.dedicateNote"),
          },
        ]}
        onClose={() => setDedicating(null)}
        onSaved={() => {
          setDedicating(null);
          setSelected(null);
          void servers.refetch();
        }}
      />

>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
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
<<<<<<< HEAD
          can("agent.manage") && selected ? (
            <>
=======
          selected ? (
            <>
              {can("server.manage") ? (
                <>
                  <Button variant="outline" size="sm" onClick={() => setEditing(selected)}>
                    {t("common.edit")}
                  </Button>

                  <Button variant="outline" size="sm" onClick={() => setDedicating(selected)}>
                    {t("infrastructure.dedicate")}
                  </Button>
                </>
              ) : null}

              {can("agent.manage") ? (
                <>
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
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
<<<<<<< HEAD
=======
                </>
              ) : null}
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
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
            {selected ? <ServerAgents serverId={selected.id} onRevoke={(id) => revokeAgent.mutate(id)} /> : null}

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
<<<<<<< HEAD
                  tone={selected.cpuPercent > 80 ? "danger" : "primary"}
=======
                  tone={(load.get(selected.id)?.cpuPercent ?? 0) > 80 ? "danger" : "primary"}
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
                />
                <AreaChart series={series.memory} label={t("infrastructure.memoryTrend")} unit="%" />
              </div>
            ) : null}
<<<<<<< HEAD
            <div className="flex flex-col gap-4">
              <Meter label={t("dashboard.cpu")} value={selected.cpuPercent} tone={selected.cpuPercent > 80 ? "danger" : "primary"} />
              <Meter label={t("dashboard.memory")} value={selected.memoryPercent} tone={selected.memoryPercent > 75 ? "warning" : "primary"} />
              <Meter label={t("dashboard.disk")} value={selected.diskPercent} />
            </div>
=======

            {(() => {
              const current = load.get(selected.id);

              // Nothing rather than zeroes: a machine that has never reported is
              // not a machine that is idle, and three meters at 0% would say the
              // opposite of the truth.
              return current?.cpuPercent === undefined || current.cpuPercent === null ? (
                <Card className="p-4 text-body-sm text-on-surface-variant">
                  {t("infrastructure.noLoad")}
                </Card>
              ) : (
                <div className="flex flex-col gap-4">
                  <Meter
                    label={t("dashboard.cpu")}
                    value={current.cpuPercent}
                    tone={current.cpuPercent > 80 ? "danger" : "primary"}
                  />
                  <Meter
                    label={t("dashboard.memory")}
                    value={current.memoryPercent ?? 0}
                    tone={(current.memoryPercent ?? 0) > 75 ? "warning" : "primary"}
                  />
                  <Meter label={t("dashboard.disk")} value={current.diskPercent ?? 0} />
                </div>
              );
            })()}

>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
            <dl className="divide-y divide-outline-variant">
              <KeyValue label={t("common.status")}>
                <StatusChip tone={healthTone[selected.status]}>
                  {t(`health.${selected.status}`)}
                </StatusChip>
              </KeyValue>
<<<<<<< HEAD
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
=======
              {selected.statusReason ? (
                <KeyValue label={t("infrastructure.statusReason")}>{selected.statusReason}</KeyValue>
              ) : null}
              <KeyValue label={t("infrastructure.dedicatedTo")}>
                {customerName(selected.dedicatedCustomerId) ?? t("infrastructure.shared")}
              </KeyValue>
              <KeyValue label={t("infrastructure.ip")}>
                <Mono>{selected.ipAddress ?? "—"}</Mono>
              </KeyValue>
              <KeyValue label={t("infrastructure.provider")}>{selected.provider ?? "—"}</KeyValue>
              <KeyValue label={t("infrastructure.region")}>{selected.region ?? "—"}</KeyValue>
              <KeyValue label={t("infrastructure.lastSeen")}>
                {selected.lastSeenAt === null ? t("infrastructure.neverReported") : formatRelative(selected.lastSeenAt)}
              </KeyValue>
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
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
<<<<<<< HEAD
=======
  const [ipAddress, setIpAddress] = useState("");
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
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
<<<<<<< HEAD
=======
          ipAddress: ipAddress || undefined,
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
        },
      });

      setName("");
      setProvider("");
      setRegion("");
<<<<<<< HEAD
=======
      setIpAddress("");
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
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
<<<<<<< HEAD
=======

        <TextField
          label={t("infrastructure.ip")}
          dir="ltr"
          placeholder="203.0.113.10"
          hint={t("infrastructure.ipHint")}
          value={ipAddress}
          onChange={(event) => setIpAddress(event.target.value)}
        />
>>>>>>> 389fa13b7f2681289077cda7a8f26f31ce4ef5e5
      </div>
    </Drawer>
  );
}

/**
 * The agents enrolled on one machine.
 *
 * Listed inside the server's own panel rather than on a screen of their own: an
 * agent has no meaning apart from the machine it reports for, and the question
 * being asked is always "what is reporting for this box".
 */
function ServerAgents({ serverId, onRevoke }: { serverId: string; onRevoke: (agentId: string) => void }) {
  const { t } = useTranslation();
  const can = useAuthStore((state) => state.can);

  const agents = useCollection<{
    id: string;
    status: string;
    version: string | null;
    lastSeenAt: string | null;
  }>(`/servers/${serverId}/agents`);

  const rows = agents.data ?? [];

  if (rows.length === 0) {
    return <p className="text-body-sm text-on-surface-variant">{t("infrastructure.noAgents")}</p>;
  }

  return (
    <section className="flex flex-col gap-2">
      <h3 className="label-caps text-on-surface-variant/80">{t("infrastructure.agents")}</h3>

      {rows.map((agent) => (
        <div key={agent.id} className="flex flex-wrap items-center gap-3 rounded-md bg-surface-low p-3">
          <span dir="ltr" className="flex-1 font-mono text-label text-on-surface-variant">
            {agent.version ?? "—"} · {agent.id.slice(0, 8)}
          </span>

          <StatusChip tone={agent.status === "Enrolled" ? "success" : "neutral"}>{agent.status}</StatusChip>

          {can("agent.manage") && agent.status !== "Revoked" ? (
            <Button variant="outline" size="sm" onClick={() => onRevoke(agent.id)}>
              {t("infrastructure.revokeAgent")}
            </Button>
          ) : null}
        </div>
      ))}
    </section>
  );
}
