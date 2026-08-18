import { useState } from "react";
import { useTranslation } from "react-i18next";
import { AlertTriangle, Info, AlertOctagon, CheckCheck } from "lucide-react";
import { useCollection } from "@/lib/api/hooks";
import type { Alert } from "@/lib/api/fixtures-detail";
import { PageShell, PageHeader, Toolbar, FilterTabs, KeyValue, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Drawer } from "@/components/data/Drawer";
import { AreaChart } from "@/components/data/Sparkline";
import { Card } from "@/components/ui/Card";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { useAuthStore } from "@/store/auth";
import { formatDateTime, formatNumber, formatRelative } from "@/lib/utils/format";

const severityTone: Record<Alert["severity"], Tone> = {
  critical: "danger",
  warning: "warning",
  info: "info",
};

const statusTone: Record<Alert["status"], Tone> = {
  Open: "danger",
  Investigating: "warning",
  Acknowledged: "info",
  Resolved: "success",
};

type Filter = "all" | Alert["severity"];

export function AlertsPage() {
  const { t } = useTranslation();
  const query = useCollection<Alert>("/alerts");
  const can = useAuthStore((state) => state.can);
  const [filter, setFilter] = useState<Filter>("all");
  const [selected, setSelected] = useState<Alert | null>(null);

  const all = query.data ?? [];
  const rows = all.filter((alert) => filter === "all" || alert.severity === filter);
  const count = (severity: Alert["severity"]) => all.filter((a) => a.severity === severity).length;

  const tiles = [
    { key: "critical", icon: AlertOctagon, tone: "danger" as Tone, value: count("critical") },
    { key: "warning", icon: AlertTriangle, tone: "warning" as Tone, value: count("warning") },
    { key: "info", icon: Info, tone: "info" as Tone, value: count("info") },
    {
      key: "resolved",
      icon: CheckCheck,
      tone: "success" as Tone,
      value: all.filter((a) => a.status === "Resolved").length,
    },
  ];

  const columns: Column<Alert>[] = [
    {
      key: "title",
      header: t("alerts.title"),
      render: (row) => (
        <span className="flex flex-col">
          <span className="text-on-surface">{row.title}</span>
          <Mono>{row.reference}</Mono>
        </span>
      ),
    },
    {
      key: "severity",
      header: t("alerts.severity"),
      render: (row) => (
        <StatusChip tone={severityTone[row.severity]}>{t(`severity.${row.severity}`)}</StatusChip>
      ),
    },
    { key: "source", header: t("alerts.source"), mono: true, render: (row) => row.source },
    { key: "scope", header: t("alerts.scope"), render: (row) => row.scope },
    { key: "rule", header: t("alerts.rule"), mono: true, secondary: true, render: (row) => row.ruleKey },
    {
      key: "status",
      header: t("common.status"),
      render: (row) => (
        <StatusChip tone={statusTone[row.status]}>{t(`alertStatus.${row.status}`)}</StatusChip>
      ),
    },
    { key: "raised", header: t("alerts.raisedAt"), render: (row) => formatRelative(row.raisedAt) },
    {
      key: "assignee",
      header: t("alerts.assignee"),
      secondary: true,
      render: (row) => row.assignee ?? "—",
    },
  ];

  return (
    <PageShell>
      <PageHeader title={t("nav.alerts")} subtitle={t("alerts.subtitle")} />

      <div className="grid grid-cols-2 gap-4 xl:grid-cols-4">
        {tiles.map((tile) => (
          <Card key={tile.key} className="flex items-center justify-between gap-3 p-4">
            <div>
              <p className="text-body-sm text-on-surface-variant">{t(`alerts.tile_${tile.key}`)}</p>
              <p className="mt-1 text-headline font-semibold text-on-surface">
                {formatNumber(tile.value)}
              </p>
            </div>
            <StatusChip tone={tile.tone}>
              <tile.icon className="size-4" aria-hidden />
            </StatusChip>
          </Card>
        ))}
      </div>

      <CollectionCard
        query={query}
        toolbar={
          <Toolbar>
            <FilterTabs<Filter>
              value={filter}
              onChange={setFilter}
              options={[
                { value: "all", label: t("common.all"), count: all.length },
                { value: "critical", label: t("severity.critical"), count: count("critical") },
                { value: "warning", label: t("severity.warning"), count: count("warning") },
                { value: "info", label: t("severity.info"), count: count("info") },
              ]}
            />
          </Toolbar>
        }
      >
        {() => (
          <DataTable
            columns={columns}
            rows={rows}
            rowKey={(row) => row.id}
            onRowClick={setSelected}
            cardTitle={(row) => row.title}
            emptyMessage={t("common.noResults")}
          />
        )}
      </CollectionCard>

      <Drawer
        open={selected !== null}
        title={selected?.title ?? ""}
        subtitle={selected ? `${selected.reference} · ${formatDateTime(selected.raisedAt)}` : undefined}
        onClose={() => setSelected(null)}
        footer={
          can("incident.manage") ? (
            <>
              <Button variant="outline" size="sm">
                {t("alerts.acknowledge")}
              </Button>
              <Button size="sm">{t("alerts.assignToMe")}</Button>
            </>
          ) : undefined
        }
      >
        {selected ? (
          <div className="flex flex-col gap-5">
            <p className="text-body-sm text-on-surface-variant">{selected.detail}</p>

            {selected.series ? (
              <AreaChart
                series={selected.series}
                threshold={selected.threshold}
                label={t("alerts.metricTrend")}
                tone={selected.severity === "critical" ? "danger" : "warning"}
                unit={selected.metricKey?.endsWith("ms") ? "ms" : undefined}
              />
            ) : null}

            <dl className="divide-y divide-outline-variant">
              <KeyValue label={t("alerts.severity")}>
                <StatusChip tone={severityTone[selected.severity]}>
                  {t(`severity.${selected.severity}`)}
                </StatusChip>
              </KeyValue>
              <KeyValue label={t("common.status")}>
                <StatusChip tone={statusTone[selected.status]}>
                  {t(`alertStatus.${selected.status}`)}
                </StatusChip>
              </KeyValue>
              <KeyValue label={t("alerts.rule")}>
                <Mono>{selected.ruleKey}</Mono>
              </KeyValue>
              <KeyValue label={t("alerts.source")}>
                <Mono>{selected.source}</Mono>
              </KeyValue>
              <KeyValue label={t("alerts.scope")}>{selected.scope}</KeyValue>
              <KeyValue label={t("stores.environment")}>
                {t(`environment.${selected.environment}`)}
              </KeyValue>
              {selected.metricKey ? (
                <KeyValue label={t("alerts.metric")}>
                  <Mono>{selected.metricKey}</Mono>
                </KeyValue>
              ) : null}
              <KeyValue label={t("alerts.assignee")}>{selected.assignee ?? "—"}</KeyValue>
            </dl>

            {selected.logTail.length > 0 ? (
              <section>
                <h3 className="label-caps mb-2 text-on-surface-variant/80">{t("alerts.logTail")}</h3>
                <pre
                  dir="ltr"
                  className="overflow-x-auto rounded-md bg-surface-lowest p-3 font-mono text-code text-on-surface-variant"
                >
                  {selected.logTail.join("\n")}
                </pre>
              </section>
            ) : null}
          </div>
        ) : null}
      </Drawer>
    </PageShell>
  );
}
