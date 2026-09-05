import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useCollection } from "@/lib/api/hooks";
import { apiDownload } from "@/lib/api/client";
import type { LogEntry } from "@/lib/api/domain";
import { Download, Search } from "lucide-react";
import { PageShell, PageHeader, Toolbar, FilterTabs, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { formatDateTime } from "@/lib/utils/format";
import { useAuthStore } from "@/store/auth";

/** How many of the most recent entries a screen over a growing log asks for. */
const RECENT = 200;

/**
 * The severity floors the tabs offer. "Warning" and "Error" become the
 * `minSeverity` the API filters on — everything at or above that level — which is
 * how the errors, warnings and alerts are pulled out of the noise server-side
 * rather than after the fact (docs/risks.md §3.4).
 */
type Severity = "all" | "Warning" | "Error";

const levelTone: Record<LogEntry["level"], Tone> = {
  Debug: "neutral",
  Information: "info",
  Warning: "warning",
  Error: "danger",
  Critical: "danger",
};

/** A `datetime-local` value (local wall clock, no zone) as an ISO instant for the API. */
function toInstant(local: string): string | null {
  if (!local) return null;
  const parsed = new Date(local);
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
}

/**
 * The structured log stream shipped by stores, read centrally across every one.
 *
 * The filtering runs on the server: a severity floor, a full-text search of the
 * message, and a time range are query parameters, not a pass over a page already
 * fetched, so a match that is older than the most recent {@link RECENT} entries
 * is still found. Log shipping is a paid capability, so a store whose customer is
 * not entitled to it appears here not at all — which is why the empty state says
 * the stream may simply not be switched on.
 */
export function LogsPage() {
  const { t } = useTranslation();
  const canExport = useAuthStore((state) => state.can("logs.export"));

  const [severity, setSeverity] = useState<Severity>("all");
  const [draftSearch, setDraftSearch] = useState("");
  const [search, setSearch] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState<string | null>(null);

  // The query the server filters on. Everything but the page size drives a
  // refetch, because the whole point is to filter rows this page never loaded.
  const params = useMemo(() => {
    const search_ = new URLSearchParams({ pageSize: String(RECENT) });
    if (severity !== "all") search_.set("minSeverity", severity);
    if (search.trim()) search_.set("search", search.trim());
    const fromInstant = toInstant(from);
    const toInstant_ = toInstant(to);
    if (fromInstant) search_.set("from", fromInstant);
    if (toInstant_) search_.set("to", toInstant_);
    return search_.toString();
  }, [severity, search, from, to]);

  const query = useCollection<LogEntry>(`/logs?${params}`);
  const rows = query.data ?? [];

  async function exportCsv() {
    setExporting(true);
    setExportError(null);
    try {
      // The export carries the same filter as the view, minus the page size —
      // the server caps it — so what downloads is what is on screen, widened to
      // everything that matches rather than the most recent page of it.
      const exportParams = new URLSearchParams(params);
      exportParams.delete("pageSize");
      const blob = await apiDownload(`/logs/export?${exportParams.toString()}`);
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = `logs-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, "")}.csv`;
      link.click();
      URL.revokeObjectURL(url);
    } catch {
      setExportError(t("logs.exportFailed"));
    } finally {
      setExporting(false);
    }
  }

  function commitSearch() {
    setSearch(draftSearch);
  }

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

  const dateFieldClass =
    "h-9 rounded-md border border-outline-variant bg-surface-low px-3 text-body-sm text-on-surface focus:border-primary focus:outline-none";

  return (
    <PageShell>
      <PageHeader
        title={t("nav.logs")}
        subtitle={t("logs.subtitle")}
        actions={
          canExport ? (
            <Button variant="outline" onClick={exportCsv} disabled={exporting || rows.length === 0}>
              <Download className="size-4" aria-hidden />
              {exporting ? t("logs.exporting") : t("logs.export")}
            </Button>
          ) : undefined
        }
      />

      <CollectionCard
        query={query}
        toolbar={
          <Toolbar>
            <FilterTabs<Severity>
              value={severity}
              onChange={setSeverity}
              options={[
                { value: "all", label: t("logs.severityAll") },
                { value: "Warning", label: t("logs.warningsAndAbove") },
                { value: "Error", label: t("logs.errorsAndAbove") },
              ]}
            />

            <div className="flex flex-wrap items-center gap-2">
              <label className="flex items-center gap-1 text-body-sm text-on-surface-variant">
                {t("logs.from")}
                <input
                  type="datetime-local"
                  value={from}
                  onChange={(event) => setFrom(event.target.value)}
                  aria-label={t("logs.from")}
                  className={dateFieldClass}
                />
              </label>
              <label className="flex items-center gap-1 text-body-sm text-on-surface-variant">
                {t("logs.to")}
                <input
                  type="datetime-local"
                  value={to}
                  onChange={(event) => setTo(event.target.value)}
                  aria-label={t("logs.to")}
                  className={dateFieldClass}
                />
              </label>
              {(from || to) && (
                <button
                  type="button"
                  onClick={() => {
                    setFrom("");
                    setTo("");
                  }}
                  className="text-body-sm text-primary hover:underline"
                >
                  {t("logs.clearRange")}
                </button>
              )}
            </div>

            <div className="relative ms-auto w-full sm:w-72">
              <Search
                className="pointer-events-none absolute inset-y-0 start-3 my-auto size-4 text-on-surface-variant"
                aria-hidden
              />
              <input
                type="search"
                value={draftSearch}
                onChange={(event) => setDraftSearch(event.target.value)}
                onKeyDown={(event) => event.key === "Enter" && commitSearch()}
                onBlur={commitSearch}
                placeholder={t("logs.searchPlaceholder")}
                aria-label={t("logs.searchPlaceholder")}
                className="h-9 w-full rounded-md border border-outline-variant bg-surface-low ps-9 pe-3 text-body-sm text-on-surface focus:border-primary focus:outline-none"
              />
            </div>
          </Toolbar>
        }
      >
        {() => (
          <>
            {exportError && (
              <p role="alert" className="mb-3 text-body-sm text-danger">
                {exportError}
              </p>
            )}
            <DataTable
              columns={columns}
              rows={rows}
              rowKey={(row) => row.id}
              cardTitle={(row) => <Mono>{row.service}</Mono>}
              emptyMessage={t("logs.none")}
            />
          </>
        )}
      </CollectionCard>
    </PageShell>
  );
}
