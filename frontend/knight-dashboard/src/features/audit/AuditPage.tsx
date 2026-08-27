import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Download } from "lucide-react";
import { useCollection } from "@/lib/api/hooks";
import type { AuditEntry } from "@/lib/api/domain";
import { PageShell, PageHeader, Toolbar, FilterTabs, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { StatusChip } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { formatDateTime } from "@/lib/utils/format";

/** How many of the most recent entries a screen over a growing log asks for. */
const RECENT = 200;

type Filter = "all" | "Success" | "Failure";

export function AuditPage() {
  const { t } = useTranslation();
  // A page size of its own, which is how a screen says "the most recent this
  // many" rather than "all of them". The audit log is append-only and only ever
  // grows, so following its pages would mean pulling every sensitive operation
  // this platform has ever recorded in order to render two hundred rows.
  const query = useCollection<AuditEntry>(`/audit-logs?pageSize=${RECENT}`);
  const [filter, setFilter] = useState<Filter>("all");

  const all = query.data ?? [];
  const rows = all.filter((entry) => filter === "all" || entry.result === filter);

  const columns: Column<AuditEntry>[] = [
    {
      key: "time",
      header: t("audit.timestamp"),
      mono: true,
      render: (row) => formatDateTime(row.occurredAt),
    },
    {
      key: "actor",
      header: t("audit.actor"),
      render: (row) => (
        <span className="flex flex-col">
          <span className="text-on-surface">{row.actor}</span>
          <span className="label-caps text-on-surface-variant/80">
            {t(`actorType.${row.actorType}`)}
          </span>
        </span>
      ),
    },
    { key: "action", header: t("audit.action"), mono: true, render: (row) => row.action },
    { key: "target", header: t("audit.target"), render: (row) => row.target },
    {
      key: "customer",
      header: t("audit.customer"),
      secondary: true,
      render: (row) => row.customerName ?? "—",
    },
    {
      key: "result",
      header: t("audit.result"),
      render: (row) => (
        <StatusChip tone={row.result === "Success" ? "success" : "danger"}>
          {t(`auditResult.${row.result}`)}
        </StatusChip>
      ),
    },
    {
      key: "ip",
      header: t("audit.ip"),
      mono: true,
      secondary: true,
      render: (row) => row.ipAddress ?? "—",
    },
    {
      key: "correlation",
      header: t("common.correlationId"),
      mono: true,
      secondary: true,
      render: (row) => row.correlationId || "—",
    },
  ];

  return (
    <PageShell>
      <PageHeader
        title={t("nav.audit")}
        subtitle={t("audit.subtitle")}
        actions={
          <Button variant="outline" size="sm">
            <Download className="size-4" aria-hidden />
            {t("audit.export")}
          </Button>
        }
      />

      <CollectionCard
        query={query}
        toolbar={
          <Toolbar>
            <FilterTabs<Filter>
              value={filter}
              onChange={setFilter}
              options={[
                { value: "all", label: t("common.all"), count: all.length },
                {
                  value: "Success",
                  label: t("auditResult.Success"),
                  count: all.filter((e) => e.result === "Success").length,
                },
                {
                  value: "Failure",
                  label: t("auditResult.Failure"),
                  count: all.filter((e) => e.result === "Failure").length,
                },
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
            cardTitle={(row) => <Mono>{row.action}</Mono>}
            emptyMessage={t("common.noResults")}
          />
        )}
      </CollectionCard>
    </PageShell>
  );
}
