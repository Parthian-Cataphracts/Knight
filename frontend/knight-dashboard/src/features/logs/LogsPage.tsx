import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useCollection } from "@/lib/api/hooks";
import type { LogEntry } from "@/lib/api/domain";
import { Search } from "lucide-react";
import { PageShell, PageHeader, Toolbar, FilterTabs, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { formatDateTime } from "@/lib/utils/format";

/** How many of the most recent entries a screen over a growing log asks for. */
const RECENT = 200;

type Filter = "all" | "Error" | "Warning" | "Information";

const levelTone: Record<LogEntry["level"], Tone> = {
  Debug: "neutral",
  Information: "info",
  Warning: "warning",
  Error: "danger",
  Critical: "danger",
};

/**
 * The structured log stream shipped by stores.
 *
 * Log shipping is a paid capability, so a store whose customer is not entitled
 * to it appears here not at all rather than as an empty row — which is why the
 * empty state says the stream may simply not be switched on.
 */
export function LogsPage() {
  const { t } = useTranslation();
  // The most recent, not all of them: a log stream grows without bound and this
  // screen renders what it is handed.
  const query = useCollection<LogEntry>(`/logs?pageSize=${RECENT}`);
  const [filter, setFilter] = useState<Filter>("all");
  const [search, setSearch] = useState("");

  const all = query.data ?? [];
  const term = search.trim().toLowerCase();

  const rows = all.filter((entry) => {
    const matchesLevel =
      filter === "all" ||
      entry.level === filter ||
      (filter === "Error" && entry.level === "Critical");

    const matchesSearch =
      term.length === 0 ||
      entry.message.toLowerCase().includes(term) ||
      (entry.storeName ?? "").toLowerCase().includes(term);

    return matchesLevel && matchesSearch;
  });

  const columns: Column<LogEntry>[] = [
    {
      key: "time",
      header: t("audit.timestamp"),
      mono: true,
      render: (row) => formatDateTime(row.timestamp),
    },
    {
      key: "level",
      header: t("common.status"),
      render: (row) => (
        <StatusChip tone={levelTone[row.level]}>{t(`logLevel.${row.level}`)}</StatusChip>
      ),
    },
    { key: "service", header: t("logs.service"), mono: true, render: (row) => row.service },
    { key: "store", header: t("nav.stores"), render: (row) => row.storeName ?? "—" },
    { key: "message", header: t("logs.message"), render: (row) => row.message },
    {
      key: "trace",
      header: t("logs.traceId"),
      mono: true,
      secondary: true,
      render: (row) => row.traceId ?? "—",
    },
  ];

  return (
    <PageShell>
      <PageHeader title={t("nav.logs")} subtitle={t("logs.subtitle")} />

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
                  value: "Error",
                  label: t("logLevel.Error"),
                  count: all.filter((entry) => entry.level === "Error" || entry.level === "Critical").length,
                },
                {
                  value: "Warning",
                  label: t("logLevel.Warning"),
                  count: all.filter((entry) => entry.level === "Warning").length,
                },
                {
                  value: "Information",
                  label: t("logLevel.Information"),
                  count: all.filter((entry) => entry.level === "Information").length,
                },
              ]}
            />
            <div className="relative ms-auto w-full sm:w-72">
              <Search
                className="pointer-events-none absolute inset-y-0 start-3 my-auto size-4 text-on-surface-variant"
                aria-hidden
              />
              <input
                type="search"
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                placeholder={t("logs.searchPlaceholder")}
                aria-label={t("logs.searchPlaceholder")}
                className="h-9 w-full rounded-md border border-outline-variant bg-surface-low ps-9 pe-3 text-body-sm text-on-surface focus:border-primary focus:outline-none"
              />
            </div>
          </Toolbar>
        }
      >
        {() => (
          <DataTable
            columns={columns}
            rows={rows}
            rowKey={(row) => row.id}
            cardTitle={(row) => <Mono>{row.service}</Mono>}
            emptyMessage={t("logs.none")}
          />
        )}
      </CollectionCard>
    </PageShell>
  );
}
