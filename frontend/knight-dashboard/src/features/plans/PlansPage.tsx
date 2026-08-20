import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Check, X, Plus } from "lucide-react";
import { useCollection } from "@/lib/api/hooks";
import { ChangeSubscriptionDialog } from "./ChangeSubscriptionDialog";
import { EditDrawer } from "@/features/shared/EditDrawer";
import { useAction } from "@/lib/api/hooks";
import type { EntitlementMatrixRow, Plan, Subscription } from "@/lib/api/domain";
import { PageShell, PageHeader, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { Card, CardBody, CardHeader } from "@/components/ui/Card";
import { StatusChip, type Tone } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { useAuthStore } from "@/store/auth";
import { formatDateTime, formatNumber } from "@/lib/utils/format";

const subscriptionTone: Record<Subscription["status"], Tone> = {
  Active: "success",
  Trial: "info",
  PastDue: "warning",
  Suspended: "warning",
  Cancelled: "neutral",
};

function price(amount: number, currency: string, free: string): string {
  if (amount === 0) return free;
  return `${formatNumber(amount)} ${currency === "IRT" ? "تومان" : currency}`;
}

export function PlansPage() {
  const { t } = useTranslation();
  const plans = useCollection<Plan>("/plans");
  const matrix = useCollection<EntitlementMatrixRow>("/plans/entitlement-matrix");
  const subscriptions = useCollection<Subscription>("/subscriptions");
  const can = useAuthStore((state) => state.can);
  const [changing, setChanging] = useState<Subscription | null>(null);
  const [editingPlan, setEditingPlan] = useState<Plan | null>(null);
  const [creatingPlan, setCreatingPlan] = useState(false);

  // Activation is what decides whether a plan can be sold. Kept apart from
  // editing its price, because changing what a plan costs and deciding whether
  // anybody may buy it are different decisions with different consequences.
  const planLifecycle = useAction<unknown, { id: string; action: "activate" | "deactivate" }>(
    ({ id, action }) => ({ path: `/plans/${id}/${action}` }),
    ["/plans"],
  );

  const planKeys = (plans.data ?? []).map((plan) => plan.key);

  const subscriptionColumns: Column<Subscription>[] = [
    { key: "customer", header: t("subscriptions.customer"), render: (row) => row.customerName },
    { key: "plan", header: t("subscriptions.plan"), render: (row) => row.planName },
    {
      key: "status",
      header: t("common.status"),
      render: (row) => (
        <StatusChip tone={subscriptionTone[row.status]}>
          {t(`subscriptionStatus.${row.status}`)}
        </StatusChip>
      ),
    },
    {
      key: "optional",
      header: t("subscriptions.optionalFeatures"),
      numeric: true,
      render: (row) => formatNumber(row.optionalFeatures),
    },
    {
      key: "total",
      header: t("subscriptions.monthlyTotal"),
      numeric: true,
      render: (row) => price(row.monthlyTotal, row.currency, t("plans.free")),
    },
    {
      key: "period",
      header: t("subscriptions.periodEnd"),
      secondary: true,
      render: (row) => <Mono>{formatDateTime(row.currentPeriodEnd)}</Mono>,
    },
  ];

  return (
    <PageShell>
      <EditDrawer
        open={creatingPlan}
        title={t("plans.create")}
        path="/plans"
        method="POST"
        fields={[
          { key: "key", label: t("plans.key"), value: "", ltr: true, placeholder: "professional" },
          { key: "name", label: t("common.name"), value: "" },
          { key: "description", label: t("plans.description"), value: "", required: false },
          { key: "basePrice", label: t("plans.basePrice"), value: "0", ltr: true },
          { key: "currency", label: t("plans.currency"), value: "IRR", ltr: true },
        ]}
        onClose={() => setCreatingPlan(false)}
        onSaved={() => {
          setCreatingPlan(false);
          void plans.refetch();
        }}
      />

      <EditDrawer
        open={editingPlan !== null}
        title={t("plans.edit")}
        subtitle={editingPlan?.name}
        path={`/plans/${editingPlan?.id ?? ""}`}
        fields={[
          { key: "name", label: t("common.name"), value: editingPlan?.name ?? "" },
          {
            key: "description",
            label: t("plans.description"),
            value: editingPlan?.description ?? "",
            required: false,
          },
          {
            key: "basePrice",
            label: t("plans.basePrice"),
            value: String(editingPlan?.basePrice ?? 0),
            ltr: true,
          },
          { key: "currency", label: t("plans.currency"), value: editingPlan?.currency ?? "IRR", ltr: true },
        ]}
        onClose={() => setEditingPlan(null)}
        onSaved={() => {
          setEditingPlan(null);
          void plans.refetch();
        }}
      />

      <ChangeSubscriptionDialog
        subscription={changing}
        onClose={() => setChanging(null)}
        onChanged={() => {
          setChanging(null);
          void subscriptions.refetch();
        }}
      />

      <PageHeader
        title={t("nav.plans")}
        subtitle={t("plans.subtitle")}
        actions={
          can("plan.manage") ? (
            <Button size="sm" onClick={() => setCreatingPlan(true)}>
              <Plus className="size-4" aria-hidden />
              {t("plans.create")}
            </Button>
          ) : undefined
        }
      />

      <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
        {(plans.data ?? []).map((plan) => (
          <Card key={plan.id} className="flex flex-col p-5">
            <div className="flex items-start justify-between gap-3">
              <div>
                <h2 className="text-title font-semibold text-on-surface">{plan.name}</h2>
                <p className="label-caps mt-1 text-on-surface-variant/80">{plan.key}</p>
              </div>
              <StatusChip tone="info">
                {formatNumber(plan.customerCount)} {t("plans.customers")}
              </StatusChip>
            </div>
            <p className="mt-3 text-body-sm text-on-surface-variant">{plan.description}</p>
            <p className="mt-4 text-headline font-semibold text-on-surface">
              {price(plan.basePrice, plan.currency, t("plans.free"))}
              {plan.basePrice > 0 ? (
                <span className="ms-1 text-body-sm font-normal text-on-surface-variant">
                  / {t("plans.perMonth")}
                </span>
              ) : null}
            </p>
            <ul className="mt-4 flex flex-1 flex-col gap-2">
              {plan.includedFeatures.map((slug) => (
                <li key={slug} className="flex items-center gap-2 text-body-sm text-on-surface">
                  <Check className="size-4 shrink-0 text-success" aria-hidden />
                  <Mono>{slug}</Mono>
                </li>
              ))}
              {plan.optionalFeatures.map((slug) => (
                <li
                  key={slug}
                  className="flex items-center gap-2 text-body-sm text-on-surface-variant"
                >
                  <Plus className="size-4 shrink-0 text-primary" aria-hidden />
                  <Mono>{slug}</Mono>
                </li>
              ))}
            </ul>
            {can("plan.manage") ? (
              <div className="mt-5 flex flex-wrap gap-2">
                <Button variant="outline" size="sm" onClick={() => setEditingPlan(plan)}>
                  {t("plans.edit")}
                </Button>

                <Button
                  variant="outline"
                  size="sm"
                  disabled={planLifecycle.isPending}
                  onClick={() =>
                    planLifecycle.mutate({
                      id: plan.id,
                      action: plan.isActive ? "deactivate" : "activate",
                    })
                  }
                >
                  {plan.isActive ? t("plans.deactivate") : t("plans.activate")}
                </Button>
              </div>
            ) : null}
          </Card>
        ))}
      </div>

      <CollectionCard query={matrix}>
        {(rows) => (
          <>
            <CardHeader title={t("plans.matrix")} />
            <div className="overflow-x-auto">
              <table className="w-full border-collapse text-body-sm">
                <thead>
                  <tr className="border-b border-outline-variant">
                    <th scope="col" className="label-caps px-5 py-3 text-start text-on-surface-variant/80">
                      {t("plans.feature")}
                    </th>
                    {planKeys.map((key) => (
                      <th
                        key={key}
                        scope="col"
                        className="label-caps px-5 py-3 text-center text-on-surface-variant/80"
                      >
                        {t(`planKey.${key}`)}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {rows.map((row) => (
                    <tr key={row.featureSlug} className="border-b border-outline-variant/60 last:border-0">
                      <td className="px-5 py-3.5">
                        <span className="flex flex-col">
                          <span className="text-on-surface">{row.featureName}</span>
                          <Mono>{row.featureSlug}</Mono>
                        </span>
                      </td>
                      {planKeys.map((key) => {
                        const value = row.values[key];
                        return (
                          <td key={key} className="px-5 py-3.5 text-center">
                            {value === true ? (
                              <Check className="mx-auto size-4 text-success" aria-label={t("common.yes")} />
                            ) : value === false || value === undefined ? (
                              <X
                                className="mx-auto size-4 text-on-surface-variant/40"
                                aria-label={t("common.no")}
                              />
                            ) : (
                              <span className="text-body-sm text-primary">{value}</span>
                            )}
                          </td>
                        );
                      })}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )}
      </CollectionCard>

      <CollectionCard query={subscriptions}>
        {(rows) => (
          <>
            <CardHeader title={t("plans.subscriptions")} />
            <DataTable
              onRowClick={(row) => { if (can("subscription.manage")) setChanging(row); }}
              columns={subscriptionColumns}
              rows={rows}
              rowKey={(row) => row.id}
              cardTitle={(row) => row.customerName}
              emptyMessage={t("common.noResults")}
            />
          </>
        )}
      </CollectionCard>

      <Card>
        <CardBody className="text-body-sm text-on-surface-variant">{t("plans.pricingNote")}</CardBody>
      </Card>
    </PageShell>
  );
}
