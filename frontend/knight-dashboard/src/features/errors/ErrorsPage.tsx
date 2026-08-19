import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useAction, useCollection } from "@/lib/api/hooks";
import type { ErrorGroup } from "@/lib/api/domain";
import type { ErrorEventSample } from "@/lib/api/fixtures-detail";
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
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const samples = useCollection<ErrorEventSample>(
    `/errors/groups/${selectedId ?? "none"}/events`,
    selectedId !== null,
  );
  const can = useAuthStore((state) => state.can);

  const [filter, setFilter] = useState<Filter>("all");
  const [selected, setSelectedGroup] = useState<ErrorGroup | null>(null);
  const setSelected = (group: ErrorGroup | null) => {
    setSelectedGroup(group);
    setSelectedId(group?.id ?? null);
  };

  // The server decides what the status becomes — resolving a group that has
  // recurred since reopens it as a regression — so the list is refetched rather
  // than patched locally.
  const act = useAction<unknown, { id: string; action: string }>(
    ({ id, action }) => ({ path: `/errors/groups/${id}/${action}` }),
    ["/errors/groups"],
  );

  const run = (action: string) => {
    if (!selected) return;

    act.mutate({ id: selected.id, action }, { onSuccess: () => setSelected(null) });
  };

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
          {row.isRegression ? (
            <span className="text-label text-error">{t("errors.regression")}</span>
          ) : null}
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
          can("errors.manage") && selected ? (
            <>
              <Button
                variant="outline"
                size="sm"
                disabled={act.isPending}
                onClick={() => run("ignore")}
              >
                {t("errors.ignore")}
              </Button>
              {selected.status === "New" ? (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={act.isPending}
                  onClick={() => run("acknowledge")}
                >
                  {t("errors.acknowledge")}
                </Button>
              ) : null}
              <Button size="sm" disabled={act.isPending} onClick={() => run("resolve")}>
                {t("errors.resolve")}
              </Button>
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
            {act.isError ? (
              <p role="alert" className="rounded-md bg-error-container px-3 py-2 text-body-sm text-on-error-container">
                {act.error.message}
              </p>
            ) : null}
            <dl className="divide-y divide-outline-variant">
              <KeyValue label={t("common.status")}>
                <StatusChip tone={groupTone[selected.status]}>
                  {t(`errorStatus.${selected.status}`)}
                </StatusChip>
                {selected.isRegression ? (
                  <span className="ms-2 text-label text-error">{t("errors.regression")}</span>
                ) : null}
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
                <Mono>{selected.firstSeenVersion ?? "—"}</Mono>
              </KeyValue>
            </dl>

            <section>
              <h3 className="label-caps mb-2 text-on-surface-variant/80">{t("errors.samples")}</h3>
              {(samples.data ?? []).length === 0 ? (
                <p className="text-body-sm text-on-surface-variant">{t("errors.noSamples")}</p>
              ) : (
                <ul className="flex flex-col gap-3">
                  {(samples.data ?? []).map((sample) => (
                    <li key={sample.id} className="rounded-md bg-surface-low p-3">
                      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
                        <Mono>{formatDateTime(sample.occurredAt)}</Mono>
                        <Mono className="text-primary">{sample.version}</Mono>
                        <Mono>trace: {sample.traceId}</Mono>
                      </div>
                      <pre
                        dir="ltr"
                        className="mt-2 overflow-x-auto rounded bg-surface-lowest p-2.5 font-mono text-code text-on-surface-variant"
                      >
                        {sample.stackTrace}
                      </pre>
                    </li>
                  ))}
                </ul>
              )}
            </section>
          </div>
        ) : null}
      </Drawer>
    </PageShell>
  );
}
