import { useTranslation } from "react-i18next";
import { useCollection } from "@/lib/api/hooks";
import type { Invoice } from "@/lib/api/domain";
import { PageShell, PageHeader, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { formatDateTime, formatNumber } from "@/lib/utils/format";

const invoiceTone: Record<Invoice["status"], Tone> = {
  Paid: "success",
  Issued: "info",
  Draft: "neutral",
  Void: "neutral",
  Overdue: "danger",
};

export function BillingPage() {
  const { t } = useTranslation();
  const query = useCollection<Invoice>("/invoices");

  const columns: Column<Invoice>[] = [
    {
      key: "number",
      header: t("billing.number"),
      mono: true,
      render: (row) => row.number,
    },
    { key: "customer", header: t("billing.customer"), render: (row) => row.customerName },
    {
      key: "period",
      header: t("billing.period"),
      secondary: true,
      render: (row) => (
        <Mono>
          {formatDateTime(row.periodStart)} — {formatDateTime(row.periodEnd)}
        </Mono>
      ),
    },
    {
      key: "total",
      header: t("billing.total"),
      numeric: true,
      render: (row) =>
        row.total === 0 ? t("plans.free") : `${formatNumber(row.total)} ${t("billing.currency")}`,
    },
    {
      key: "status",
      header: t("common.status"),
      render: (row) => (
        <StatusChip tone={invoiceTone[row.status]}>{t(`invoiceStatus.${row.status}`)}</StatusChip>
      ),
    },
    {
      key: "issued",
      header: t("billing.issuedAt"),
      secondary: true,
      render: (row) => (row.issuedAt ? <Mono>{formatDateTime(row.issuedAt)}</Mono> : "—"),
    },
  ];

  return (
    <PageShell>
      <PageHeader title={t("nav.billing")} subtitle={t("billing.subtitle")} />
      <CollectionCard query={query}>
        {(rows) => (
          <DataTable
            columns={columns}
            rows={rows}
            rowKey={(row) => row.id}
            cardTitle={(row) => (
              <span dir="ltr" className="font-mono">
                {row.number}
              </span>
            )}
            emptyMessage={t("common.noResults")}
          />
        )}
      </CollectionCard>
    </PageShell>
  );
}
