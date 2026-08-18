import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useCollection } from "@/lib/api/hooks";
import type { ErrorGroup } from "@/lib/api/domain";
import { PageShell, PageHeader, Toolbar, FilterTabs, KeyValue, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Drawer } from "@/components/data/Drawer";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { useAuthStore } from "@/store/auth";
import { formatDateTime, formatNumber, formatRelative } from "@/lib/utils/format";

const groupTone: Record<ErrorGroup["status"], Tone> = {
  New: "danger",
  Acknowledged: "warning",
  Resolved: "success",
  Ignored: "neutral",
};

type Filter = "all" | ErrorGroup["status"];

export function ErrorsPage() {
  const { t } = useTranslation();
  const query = useCollection<ErrorGroup>("/errors/groups");
  const can = useAuthStore((state) => state.can);
  const [filter, setFilter] = useState<Filter>("all");
  const [selected, setSelected] = useState<ErrorGroup | null>(null);

  const all = query.data ?? [];
  const rows = all.filter((group) => filter === "all" || group.status === filter);

  const columns: Column<ErrorGroup>[] = [
    {
      key: "title",
      header: t("errors.group"),
      render: (row) => (
        <span className="flex flex-col">
          <span dir="ltr" className="font-mono text-body-sm text-on-surface">
            {row.exceptionType}
          </span>
          <span className="line-clamp-1 text-body-sm text-on-surface-variant">{row.title}</span>
        </span>
      ),
    },
    {
      key: "store",
      header: t("errors.store"),
      mono: true,
      render: (row) => row.storeName,
    },
    {
      key: "endpoint",
      header: t("errors.endpoint"),
      mono: true,
      secondary: true,
      render: (row) => row.endpoint ?? "—",
    },
    {
      key: "count",
      header: t("errors.occurrences"),
      numeric: true,
      render: (row) => formatNumber(row.occurrenceCount),
    },
    {
      key: "status",
      header: t("common.status"),
      render: (row) => (
        <StatusChip tone={groupTone[row.status]}>{t(`errorStatus.${row.status}`)}</StatusChip>
      ),
    },
    {
      key: "lastSeen",
      header: t("errors.lastSeen"),
      render: (row) => formatRelative(row.lastSeenAt),
    },
  ];

  return (
    <PageShell>
      <PageHeader title={t("nav.errors")} subtitle={t("errors.subtitle")} />

      <CollectionCard
        query={query}
        toolbar={
          <Toolbar>
            <FilterTabs<Filter>
              value={filter}
              onChange={setFilter}
              options={[
                { value: "all", label: t("common.all"), count: all.length },
                { value: "New", label: t("errorStatus.New"), count: all.filter((g) => g.status === "New").length },
                {
                  value: "Acknowledged",
                  label: t("errorStatus.Acknowledged"),
                  count: all.filter((g) => g.status === "Acknowledged").length,
                },
                {
                  value: "Resolved",
                  label: t("errorStatus.Resolved"),
                  count: all.filter((g) => g.status === "Resolved").length,
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
            onRowClick={setSelected}
            cardTitle={(row) => (
              <span dir="ltr" className="font-mono">
                {row.exceptionType}
              </span>
            )}
            emptyMessage={t("common.noResults")}
          />
        )}
      </CollectionCard>

      <Drawer
        open={selected !== null}
        title={selected?.exceptionType ?? ""}
        subtitle={selected?.storeName}
        onClose={() => setSelected(null)}
        footer={
          can("errors.manage") ? (
            <>
              <Button variant="outline" size="sm">
                {t("errors.ignore")}
              </Button>
              <Button size="sm">{t("errors.resolve")}</Button>
            </>
          ) : undefined
        }
      >
        {selected ? (
          <div className="flex flex-col gap-5">
            <p
              dir="ltr"
              className="overflow-x-auto rounded-md bg-surface-lowest px-3 py-2.5 font-mono text-label text-on-surface-variant"
            >
              {selected.title}
            </p>
            <dl className="divide-y divide-outline-variant">
              <KeyValue label={t("common.status")}>
                <StatusChip tone={groupTone[selected.status]}>
                  {t(`errorStatus.${selected.status}`)}
                </StatusChip>
              </KeyValue>
              <KeyValue label={t("errors.endpoint")}>
                <Mono>{selected.endpoint ?? "—"}</Mono>
              </KeyValue>
              <KeyValue label={t("stores.environment")}>
                {t(`environment.${selected.environment}`)}
              </KeyValue>
              <KeyValue label={t("errors.occurrences")}>
                {formatNumber(selected.occurrenceCount)}
              </KeyValue>
              <KeyValue label={t("errors.firstSeen")}>
                <Mono>{formatDateTime(selected.firstSeenAt)}</Mono>
              </KeyValue>
              <KeyValue label={t("errors.lastSeen")}>
                <Mono>{formatDateTime(selected.lastSeenAt)}</Mono>
              </KeyValue>
              <KeyValue label={t("errors.firstSeenVersion")}>
                <Mono>{selected.firstSeenVersion}</Mono>
              </KeyValue>
            </dl>
          </div>
        ) : null}
      </Drawer>
    </PageShell>
  );
}
