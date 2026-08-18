import { useTranslation } from "react-i18next";
import { useCollection } from "@/lib/api/hooks";
import type { Incident } from "@/lib/api/domain";
import { PageShell, PageHeader, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { formatRelative } from "@/lib/utils/format";

const statusTone: Record<Incident["status"], Tone> = {
  Open: "danger",
  Investigating: "warning",
  Mitigated: "info",
  Resolved: "success",
};

const severityTone: Record<Incident["severity"], Tone> = {
  critical: "danger",
  warning: "warning",
  info: "info",
};

export function IncidentsPage() {
  const { t } = useTranslation();
  const query = useCollection<Incident>("/incidents");

  const columns: Column<Incident>[] = [
    { key: "reference", header: t("incidents.reference"), mono: true, render: (row) => row.reference },
    {
      key: "title",
      header: t("incidents.title"),
      render: (row) => <span className="text-on-surface">{row.title}</span>,
    },
    {
      key: "severity",
      header: t("incidents.severity"),
      render: (row) => (
        <StatusChip tone={severityTone[row.severity]}>{t(`severity.${row.severity}`)}</StatusChip>
      ),
    },
    {
      key: "status",
      header: t("common.status"),
      render: (row) => (
        <StatusChip tone={statusTone[row.status]}>{t(`incidentStatus.${row.status}`)}</StatusChip>
      ),
    },
    {
      key: "scope",
      header: t("incidents.scope"),
      mono: true,
      secondary: true,
      render: (row) => row.storeName ?? row.serverName ?? "—",
    },
    { key: "opened", header: t("incidents.openedAt"), render: (row) => formatRelative(row.openedAt) },
    {
      key: "resolved",
      header: t("incidents.resolvedAt"),
      secondary: true,
      render: (row) => (row.resolvedAt ? formatRelative(row.resolvedAt) : "—"),
    },
  ];

  return (
    <PageShell>
      <PageHeader title={t("nav.incidents")} subtitle={t("incidents.subtitle")} />
      <CollectionCard query={query}>
        {(rows) => (
          <DataTable
            columns={columns}
            rows={rows}
            rowKey={(row) => row.id}
            cardTitle={(row) => (
              <span className="flex flex-col gap-1">
                <Mono>{row.reference}</Mono>
                <span>{row.title}</span>
              </span>
            )}
            emptyMessage={t("common.noResults")}
          />
        )}
      </CollectionCard>
    </PageShell>
  );
}
