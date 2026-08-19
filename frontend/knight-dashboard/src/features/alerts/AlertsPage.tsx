import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useAction, useCollection } from "@/lib/api/hooks";
import { PageShell, PageHeader, Toolbar, FilterTabs, KeyValue, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Drawer } from "@/components/data/Drawer";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { useAuthStore } from "@/store/auth";
import { formatDateTime, formatNumber, formatRelative } from "@/lib/utils/format";

/**
 * An alert as the control plane actually records it.
 *
 * The shape matters. There is no per-occurrence row and no assignee: an alert
 * is deduplicated by rule and source, so a server that has been offline for six
 * hours is one row carrying a count and a duration — which is the fact an
 * operator needs — rather than seven hundred rows burying it.
 */
interface Alert {
  id: string;
  source: string;
  sourceId: string;
  customerId: string | null;
  severity: "Critical" | "Warning" | "Info";
  ruleKey: string;
  message: string;
  raisedAt: string;
  resolvedAt: string | null;
  acknowledgedAt: string | null;
  occurrenceCount: number;
  lastObservedAt: string;
  isOpen: boolean;
}

const severityTone: Record<Alert["severity"], Tone> = {
  Critical: "danger",
  Warning: "warning",
  Info: "info",
};

const stateTone: Record<string, Tone> = {
  open: "danger",
  acknowledged: "warning",
  resolved: "success",
};

type Filter = "open" | "Critical" | "Warning" | "resolved";

/**
 * Acknowledged and resolved are different claims, and the screen keeps them
 * apart: acknowledging says somebody is looking, resolving says the condition
 * has cleared. Only the second closes the row.
 */
function stateOf(alert: Alert): string {
  return !alert.isOpen ? "resolved" : alert.acknowledgedAt ? "acknowledged" : "open";
}

export function AlertsPage() {
  const { t } = useTranslation();

  // Resolved alerts are fetched alongside the open ones so the filter can move
  // between them without a second round trip, and so "when did it clear?" is
  // answerable on the screen that showed it broken.
  const query = useCollection<Alert>("/monitoring/alerts?openOnly=false&pageSize=100");
  const can = useAuthStore((state) => state.can);
  const [filter, setFilter] = useState<Filter>("open");
  const [selected, setSelected] = useState<Alert | null>(null);

  const act = useAction<unknown, { id: string; action: "acknowledge" | "resolve" }>(
    ({ id, action }) => ({ path: `/monitoring/alerts/${id}/${action}` }),
    ["/monitoring/alerts"],
  );

  const all = query.data ?? [];

  const rows = all.filter((alert) => {
    if (filter === "open") return alert.isOpen;
    if (filter === "resolved") return !alert.isOpen;
    return alert.isOpen && alert.severity === filter;
  });

  const count = (predicate: (alert: Alert) => boolean) => all.filter(predicate).length;

  const columns: Column<Alert>[] = [
    {
      key: "rule",
      header: t("alerts.rule"),
      render: (row) => (
        <span className="flex flex-col">
          <span dir="ltr" className="font-mono text-body-sm text-on-surface">
            {row.ruleKey}
          </span>
          <span className="line-clamp-1 text-body-sm text-on-surface-variant">{row.message}</span>
        </span>
      ),
    },
    {
      key: "severity",
      header: t("alerts.severity"),
      render: (row) => (
        <StatusChip tone={severityTone[row.severity]}>
          {t(`severity.${row.severity.toLowerCase()}`)}
        </StatusChip>
      ),
    },
    { key: "source", header: t("alerts.source"), mono: true, render: (row) => row.source },
    {
      key: "count",
      header: t("alerts.occurrences"),
      numeric: true,
      render: (row) => formatNumber(row.occurrenceCount),
    },
    {
      key: "status",
      header: t("common.status"),
      render: (row) => (
        <StatusChip tone={stateTone[stateOf(row)]!}>{t(`alertState.${stateOf(row)}`)}</StatusChip>
      ),
    },
    { key: "raised", header: t("alerts.raisedAt"), render: (row) => formatRelative(row.raisedAt) },
  ];

  return (
    <PageShell>
      <PageHeader title={t("nav.alerts")} subtitle={t("alerts.subtitle")} />

      <CollectionCard
        query={query}
        toolbar={
          <Toolbar>
            <FilterTabs<Filter>
              value={filter}
              onChange={setFilter}
              options={[
                { value: "open", label: t("alertState.open"), count: count((a) => a.isOpen) },
                {
                  value: "Critical",
                  label: t("severity.critical"),
                  count: count((a) => a.isOpen && a.severity === "Critical"),
                },
                {
                  value: "Warning",
                  label: t("severity.warning"),
                  count: count((a) => a.isOpen && a.severity === "Warning"),
                },
                { value: "resolved", label: t("alertState.resolved"), count: count((a) => !a.isOpen) },
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
            cardTitle={(row) => (
              <span dir="ltr" className="font-mono">
                {row.ruleKey}
              </span>
            )}
            emptyMessage={t("alerts.noneOpen")}
          />
        )}
      </CollectionCard>

      <Drawer
        open={selected !== null}
        title={selected?.ruleKey ?? ""}
        subtitle={selected ? formatDateTime(selected.raisedAt) : undefined}
        onClose={() => setSelected(null)}
        footer={
          can("server.manage") && selected?.isOpen ? (
            <>
              <Button
                variant="outline"
                size="sm"
                disabled={act.isPending || selected.acknowledgedAt !== null}
                onClick={() => act.mutate({ id: selected.id, action: "acknowledge" })}
              >
                {t("alerts.acknowledge")}
              </Button>
              <Button
                size="sm"
                disabled={act.isPending}
                onClick={() =>
                  act.mutate(
                    { id: selected.id, action: "resolve" },
                    { onSuccess: () => setSelected(null) },
                  )
                }
              >
                {t("alerts.resolve")}
              </Button>
            </>
          ) : undefined
        }
      >
        {selected ? (
          <div className="flex flex-col gap-5">
            {act.isError ? (
              <p
                role="alert"
                className="rounded-md bg-error-container px-3 py-2 text-body-sm text-on-error-container"
              >
                {act.error.message}
              </p>
            ) : null}

            <p className="text-body-sm text-on-surface">{selected.message}</p>

            <dl className="divide-y divide-outline-variant">
              <KeyValue label={t("alerts.severity")}>
                <StatusChip tone={severityTone[selected.severity]}>
                  {t(`severity.${selected.severity.toLowerCase()}`)}
                </StatusChip>
              </KeyValue>
              <KeyValue label={t("common.status")}>
                <StatusChip tone={stateTone[stateOf(selected)]!}>
                  {t(`alertState.${stateOf(selected)}`)}
                </StatusChip>
              </KeyValue>
              <KeyValue label={t("alerts.source")}>
                <Mono>
                  {selected.source} · {selected.sourceId}
                </Mono>
              </KeyValue>
              <KeyValue label={t("alerts.occurrences")}>{formatNumber(selected.occurrenceCount)}</KeyValue>
              <KeyValue label={t("alerts.raisedAt")}>
                <Mono>{formatDateTime(selected.raisedAt)}</Mono>
              </KeyValue>
              <KeyValue label={t("alerts.lastObserved")}>
                <Mono>{formatDateTime(selected.lastObservedAt)}</Mono>
              </KeyValue>
              {selected.acknowledgedAt ? (
                <KeyValue label={t("alerts.acknowledgedAt")}>
                  <Mono>{formatDateTime(selected.acknowledgedAt)}</Mono>
                </KeyValue>
              ) : null}
              {selected.resolvedAt ? (
                <KeyValue label={t("alerts.resolvedAt")}>
                  <Mono>{formatDateTime(selected.resolvedAt)}</Mono>
                </KeyValue>
              ) : null}
            </dl>
          </div>
        ) : null}
      </Drawer>
    </PageShell>
  );
}
