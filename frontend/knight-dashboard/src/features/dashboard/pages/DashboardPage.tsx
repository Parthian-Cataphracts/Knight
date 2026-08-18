import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { Building2, Store, CreditCard, PackageCheck, History, Receipt } from "lucide-react";
import { apiRequest } from "@/lib/api/client";
import { ApiError } from "@/lib/api/problem";
import type { DashboardOverview } from "@/lib/api/types";
import { Card, CardBody, CardHeader } from "@/components/ui/Card";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { LoadingBlock, ErrorBlock, EmptyBlock } from "@/components/ui/StateBlock";
import { formatNumber, formatRelative } from "@/lib/utils/format";

function StatTile({
  label,
  value,
  hint,
  icon: Icon,
  tone = "neutral",
}: {
  label: string;
  value: string;
  hint?: string;
  icon: typeof Building2;
  tone?: Tone;
}) {
  const accent =
    tone === "danger"
      ? "text-error"
      : tone === "warning"
        ? "text-warning"
        : tone === "success"
          ? "text-success"
          : "text-primary";

  return (
    <Card className="p-5">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-body-sm text-on-surface-variant">{label}</p>
          <p className="mt-2 text-headline font-semibold text-on-surface">{value}</p>
          {hint ? <p className="mt-1 text-body-sm text-on-surface-variant">{hint}</p> : null}
        </div>
        <span className={`grid size-10 shrink-0 place-items-center rounded-md bg-surface-high ${accent}`}>
          <Icon className="size-5" aria-hidden />
        </span>
      </div>
    </Card>
  );
}

/**
 * Reports what the control plane knows today: customers, stores, subscriptions,
 * entitlements, billing and the audit trail. Monitoring, alerts and feature
 * delivery arrive in later phases; their tiles are absent rather than showing
 * zeros that would look like measurements.
 */
export function DashboardPage() {
  const { t } = useTranslation();

  const query = useQuery({
    queryKey: ["monitoring", "overview"],
    queryFn: () => apiRequest<DashboardOverview>("/monitoring/overview"),
  });

  return (
    <div className="mx-auto flex w-full max-w-[1400px] flex-col gap-6">
      <header>
        <h1 className="text-headline font-semibold text-on-surface">{t("dashboard.title")}</h1>
        <p className="mt-1 text-body-sm text-on-surface-variant">{t("dashboard.subtitle")}</p>
      </header>

      {query.isPending ? (
        <Card>
          <LoadingBlock rows={6} />
        </Card>
      ) : query.isError ? (
        <Card>
          <ErrorBlock
            message={(query.error as Error).message}
            status={query.error instanceof ApiError ? query.error.status : undefined}
            onRetry={() => void query.refetch()}
          />
        </Card>
      ) : (
        <>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
            <StatTile
              label={t("dashboard.activeCustomers")}
              value={formatNumber(query.data.customers.active)}
              hint={`${formatNumber(query.data.customers.suspended)} ${t("dashboard.suspended")}`}
              icon={Building2}
            />
            <StatTile
              label={t("dashboard.connectedStores")}
              value={`${formatNumber(query.data.stores.connected)} / ${formatNumber(query.data.stores.total)}`}
              icon={Store}
              tone={query.data.stores.disconnected > 0 ? "warning" : "success"}
            />
            <StatTile
              label={t("dashboard.activeSubscriptions")}
              value={formatNumber(query.data.subscriptions.active)}
              hint={`${formatNumber(query.data.subscriptions.trial)} ${t("dashboard.onTrial")}`}
              icon={CreditCard}
            />
            <StatTile
              label={t("dashboard.activeEntitlements")}
              value={formatNumber(query.data.subscriptions.activeEntitlements)}
              icon={PackageCheck}
              tone="success"
            />
          </div>

          <div className="grid grid-cols-1 gap-6 xl:grid-cols-3">
            <Card>
              <CardHeader title={t("dashboard.billing")} icon={<Receipt className="size-5" />} />
              <CardBody className="flex flex-col gap-3">
                <div className="flex items-center justify-between gap-3">
                  <span className="text-body-sm text-on-surface-variant">{t("dashboard.invoicesDraft")}</span>
                  <StatusChip tone="neutral">{formatNumber(query.data.billing.draft)}</StatusChip>
                </div>
                <div className="flex items-center justify-between gap-3">
                  <span className="text-body-sm text-on-surface-variant">{t("dashboard.invoicesIssued")}</span>
                  <StatusChip tone="info">{formatNumber(query.data.billing.issued)}</StatusChip>
                </div>
                <div className="flex items-center justify-between gap-3">
                  <span className="text-body-sm text-on-surface-variant">{t("dashboard.invoicesOverdue")}</span>
                  <StatusChip tone={query.data.billing.overdue > 0 ? "danger" : "success"}>
                    {formatNumber(query.data.billing.overdue)}
                  </StatusChip>
                </div>
                <div className="flex items-center justify-between gap-3 border-t border-outline-variant pt-3">
                  <span className="text-body-sm text-on-surface-variant">{t("dashboard.outstanding")}</span>
                  <span className="font-mono text-body-sm text-on-surface" dir="ltr">
                    {formatNumber(query.data.billing.outstandingTotal)} {query.data.billing.currency ?? ""}
                  </span>
                </div>
              </CardBody>
            </Card>

            <Card className="xl:col-span-2">
              <CardHeader title={t("dashboard.activity")} icon={<History className="size-5" />} />
              <CardBody className="flex flex-col">
                {query.data.recentActivity.length === 0 ? (
                  <EmptyBlock>{t("common.noResults")}</EmptyBlock>
                ) : (
                  <ul className="flex flex-col divide-y divide-outline-variant">
                    {query.data.recentActivity.map((entry) => (
                      <li key={entry.id} className="flex items-center justify-between gap-3 py-2.5">
                        <span className="min-w-0">
                          <span className="block truncate text-body-sm text-on-surface">{entry.action}</span>
                          <span className="block truncate text-label text-on-surface-variant">
                            {entry.targetType}
                            {entry.actor ? ` · ${entry.actor}` : ""}
                          </span>
                        </span>
                        <span className="shrink-0 text-label text-on-surface-variant">
                          {formatRelative(entry.occurredAt)}
                        </span>
                      </li>
                    ))}
                  </ul>
                )}
              </CardBody>
            </Card>
          </div>
        </>
      )}
    </div>
  );
}
