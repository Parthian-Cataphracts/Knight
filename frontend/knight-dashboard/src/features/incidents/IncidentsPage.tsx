import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useAction, useCollection } from "@/lib/api/hooks";
import type { Incident } from "@/lib/api/domain";
import type { IncidentEvent } from "@/lib/api/fixtures-detail";
import { PageShell, PageHeader, KeyValue, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Drawer } from "@/components/data/Drawer";
import { Timeline } from "@/components/data/Tabs";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { TextField } from "@/components/ui/TextField";
import { useAuthStore } from "@/store/auth";
import { formatDateTime, formatRelative } from "@/lib/utils/format";

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
  const can = useAuthStore((state) => state.can);
  const [selected, setSelected] = useState<Incident | null>(null);
  const timeline = useCollection<IncidentEvent>(`/incidents/${selected?.id ?? "none"}/events`);
  const [note, setNote] = useState("");

  // Both lists are invalidated: an action changes the incident's status on the
  // table and appends to the timeline in the drawer, and refetching only one
  // would leave the other showing the state from a moment ago.
  const act = useAction<unknown, { id: string; action: string; body?: unknown }>(
    ({ id, action, body }) => ({ path: `/incidents/${id}/${action}`, options: { body } }),
    ["/incidents"],
  );

  const run = (action: string, body?: unknown) => {
    if (!selected) return;

    act.mutate(
      { id: selected.id, action, body },
      {
        onSuccess: () => setNote(""),
      },
    );
  };

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
            onRowClick={setSelected}
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

      <Drawer
        open={selected !== null}
        title={selected?.title ?? ""}
        subtitle={selected ? `${selected.reference} · ${formatDateTime(selected.openedAt)}` : undefined}
        onClose={() => setSelected(null)}
        footer={
          can("incident.manage") && selected ? (
            <>
              {selected.status === "Resolved" ? (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={act.isPending || note.trim().length === 0}
                  onClick={() => run("reopen", { reason: note.trim() })}
                >
                  {t("incidents.reopen")}
                </Button>
              ) : (
                <>
                  {selected.status === "Open" ? (
                    <Button
                      variant="outline"
                      size="sm"
                      disabled={act.isPending}
                      onClick={() => run("acknowledge", { message: note.trim() || undefined })}
                    >
                      {t("incidents.acknowledge")}
                    </Button>
                  ) : null}
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={act.isPending || note.trim().length === 0}
                    onClick={() => run("mitigate", { message: note.trim() })}
                  >
                    {t("incidents.mitigate")}
                  </Button>
                  <Button
                    size="sm"
                    disabled={act.isPending}
                    onClick={() => run("resolve", { rootCause: note.trim() || undefined })}
                  >
                    {t("incidents.resolve")}
                  </Button>
                </>
              )}
            </>
          ) : undefined
        }
      >
        {selected ? (
          <div className="flex flex-col gap-5">
            <dl className="divide-y divide-outline-variant">
              <KeyValue label={t("incidents.severity")}>
                <StatusChip tone={severityTone[selected.severity]}>
                  {t(`severity.${selected.severity}`)}
                </StatusChip>
              </KeyValue>
              <KeyValue label={t("common.status")}>
                <StatusChip tone={statusTone[selected.status]}>
                  {t(`incidentStatus.${selected.status}`)}
                </StatusChip>
              </KeyValue>
              <KeyValue label={t("incidents.scope")}>
                <Mono>{selected.storeName ?? selected.serverName ?? "—"}</Mono>
              </KeyValue>
              <KeyValue label={t("incidents.openedAt")}>{formatRelative(selected.openedAt)}</KeyValue>
              {selected.resolvedAt ? (
                <KeyValue label={t("incidents.resolvedAt")}>
                  {formatRelative(selected.resolvedAt)}
                </KeyValue>
              ) : null}
            </dl>

            {act.isError ? (
              <p role="alert" className="rounded-md bg-error-container px-3 py-2 text-body-sm text-on-error-container">
                {act.error.message}
              </p>
            ) : null}

            {can("incident.manage") ? (
              <section className="flex flex-col gap-2">
                <TextField
                  label={t("incidents.note")}
                  value={note}
                  onChange={(event) => setNote(event.target.value)}
                  placeholder={t("incidents.notePlaceholder")}
                />
                <Button
                  variant="outline"
                  size="sm"
                  disabled={act.isPending || note.trim().length === 0}
                  onClick={() => run("notes", { message: note.trim() })}
                >
                  {t("incidents.addNote")}
                </Button>
              </section>
            ) : null}

            <section>
              <h3 className="label-caps mb-3 text-on-surface-variant/80">{t("incidents.timeline")}</h3>
              {(timeline.data ?? []).length === 0 ? (
                <p className="text-body-sm text-on-surface-variant">{t("incidents.noEvents")}</p>
              ) : (
                <Timeline
                  items={(timeline.data ?? []).map((event) => ({
                    id: event.id,
                    title: event.message,
                    meta: `${t(`incidentEvent.${event.type}`)} · ${event.actor} · ${formatRelative(event.occurredAt)}`,
                    tone:
                      event.type === "Opened"
                        ? ("danger" as const)
                        : event.type === "Resolved"
                          ? ("success" as const)
                          : ("default" as const),
                  }))}
                />
              )}
            </section>
          </div>
        ) : null}
      </Drawer>
    </PageShell>
  );
}
