import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useAction, useCollection } from "@/lib/api/hooks";
import type { Invoice } from "@/lib/api/domain";
import { PageShell, PageHeader, KeyValue, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Drawer } from "@/components/data/Drawer";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { TextField } from "@/components/ui/TextField";
import { useAuthStore } from "@/store/auth";
import { formatDateTime, formatNumber } from "@/lib/utils/format";

const invoiceTone: Record<Invoice["status"], Tone> = {
  Draft: "neutral",
  Issued: "info",
  Paid: "success",
  Void: "neutral",
  Overdue: "danger",
};

const methods = ["BankTransfer", "Card", "Cash", "Credit", "Other"] as const;

/**
 * Invoices, and the three things an operator does to one.
 *
 * KNIGHT invoices and records observed payments; it moves no money
 * (`risks.md` R14). So "record payment" here means exactly that — writing down
 * that a payment happened elsewhere — and the form says so rather than looking
 * like a checkout.
 */
export function BillingPage() {
  const { t } = useTranslation();
  const query = useCollection<Invoice>("/invoices");
  const can = useAuthStore((state) => state.can);
  const [selected, setSelected] = useState<Invoice | null>(null);

  const act = useAction<unknown, { id: string; action: "issue" | "void" }>(
    ({ id, action }) => ({ path: `/invoices/${id}/${action}` }),
    ["/invoices"],
  );

  const [amount, setAmount] = useState("");
  const [reference, setReference] = useState("");
  const [method, setMethod] = useState<(typeof methods)[number]>("BankTransfer");

  const recordPayment = useAction<
    unknown,
    { id: string; amount: number; currency: string; method: string; reference: string }
  >(
    ({ id, amount: paid, currency, method: how, reference: ref }) => ({
      path: `/invoices/${id}/payments`,
      options: { body: { amount: paid, currency, method: how, reference: ref || undefined } },
    }),
    ["/invoices"],
  );

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
        // The invoice carries its own currency; a fixed label would misreport
        // any customer billed in another one.
        row.total === 0 ? t("plans.free") : `${formatNumber(row.total)} ${row.currency}`,
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
            onRowClick={(row) => {
              setSelected(row);
              setAmount(String(row.total));
            }}
            cardTitle={(row) => row.number}
            emptyMessage={t("common.noResults")}
          />
        )}
      </CollectionCard>

      <Drawer
        open={selected !== null}
        title={selected?.number ?? ""}
        subtitle={selected?.customerName}
        onClose={() => setSelected(null)}
        footer={
          can("billing.manage") && selected ? (
            <>
              {selected.status === "Draft" ? (
                <Button
                  size="sm"
                  disabled={act.isPending}
                  onClick={() => act.mutate({ id: selected.id, action: "issue" })}
                >
                  {t("billing.issue")}
                </Button>
              ) : null}

              {selected.status !== "Paid" && selected.status !== "Void" ? (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={act.isPending}
                  onClick={() =>
                    act.mutate({ id: selected.id, action: "void" }, { onSuccess: () => setSelected(null) })
                  }
                >
                  {t("billing.void")}
                </Button>
              ) : null}
            </>
          ) : undefined
        }
      >
        {selected ? (
          <div className="flex flex-col gap-5">
            {act.isError || recordPayment.isError ? (
              <p
                role="alert"
                className="rounded-md bg-error-container px-3 py-2 text-body-sm text-on-error-container"
              >
                {(act.error ?? recordPayment.error)?.message}
              </p>
            ) : null}

            <dl className="divide-y divide-outline-variant">
              <KeyValue label={t("common.status")}>
                <StatusChip tone={invoiceTone[selected.status]}>
                  {t(`invoiceStatus.${selected.status}`)}
                </StatusChip>
              </KeyValue>
              <KeyValue label={t("billing.period")}>
                <Mono>
                  {formatDateTime(selected.periodStart)} — {formatDateTime(selected.periodEnd)}
                </Mono>
              </KeyValue>
              <KeyValue label={t("billing.total")}>
                {formatNumber(selected.total)} {selected.currency}
              </KeyValue>
              <KeyValue label={t("billing.issuedAt")}>
                {selected.issuedAt ? <Mono>{formatDateTime(selected.issuedAt)}</Mono> : "—"}
              </KeyValue>
            </dl>

            {can("billing.manage") && selected.status !== "Draft" && selected.status !== "Void" ? (
              <section className="flex flex-col gap-3">
                <h3 className="label-caps text-on-surface-variant/80">{t("billing.recordPayment")}</h3>

                {/* KNIGHT records that money moved; it never moves any. */}
                <p className="text-body-sm text-on-surface-variant">{t("billing.recordPaymentHint")}</p>

                <TextField
                  label={`${t("billing.amount")} (${selected.currency})`}
                  value={amount}
                  inputMode="decimal"
                  onChange={(event) => setAmount(event.target.value)}
                />

                <TextField
                  label={t("billing.reference")}
                  value={reference}
                  onChange={(event) => setReference(event.target.value)}
                />

                <div className="flex flex-wrap gap-2">
                  {methods.map((option) => (
                    <Button
                      key={option}
                      type="button"
                      size="sm"
                      variant={method === option ? "primary" : "outline"}
                      onClick={() => setMethod(option)}
                    >
                      {t(`paymentMethod.${option}`)}
                    </Button>
                  ))}
                </div>

                <Button
                  size="sm"
                  disabled={recordPayment.isPending || Number.isNaN(Number(amount)) || amount.length === 0}
                  onClick={() =>
                    recordPayment.mutate(
                      {
                        id: selected.id,
                        amount: Number(amount),
                        currency: selected.currency,
                        method,
                        reference,
                      },
                      { onSuccess: () => setReference("") },
                    )
                  }
                >
                  {t("billing.recordPayment")}
                </Button>
              </section>
            ) : null}
          </div>
        ) : null}
      </Drawer>
    </PageShell>
  );
}
