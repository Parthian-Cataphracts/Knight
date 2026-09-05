import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Tag } from "lucide-react";
import { apiRequest } from "@/lib/api/client";
import { useCollection } from "@/lib/api/hooks";
import type { Feature, Plan } from "@/lib/api/domain";
import { Drawer } from "@/components/data/Drawer";
import { Mono } from "@/components/data/PageShell";
import { Button } from "@/components/ui/Button";
import { cn } from "@/lib/utils/cn";
import { formatDateTime, formatNumber } from "@/lib/utils/format";

type Membership = "none" | "optional" | "included";

interface FeaturePrice {
  id: string;
  featureId: string;
  planId: string | null;
  amount: number;
  currency: string;
  billingPeriod: string;
  validFrom: string;
  validTo: string | null;
}

/**
 * Compose a plan's features and set their prices — the operator's half of what
 * a customer sees on the portal. Membership and price are separate decisions with
 * separate endpoints, and both are time-honest: removing a feature leaves
 * subscriptions that already have it alone, and setting a price closes the one it
 * replaces at the same instant the new one opens, so a period is always explained
 * by exactly one price (docs/domain-model.md §4, phase 28).
 */
export function PlanFeaturesDialog({
  plan,
  onClose,
  onChanged,
}: {
  plan: Plan | null;
  onClose: () => void;
  onChanged: () => void;
}) {
  const { t } = useTranslation();
  const features = useCollection<Feature>("/features?status=Published");

  const [busyId, setBusyId] = useState<string | null>(null);
  const [pricingFor, setPricingFor] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const membershipOf = (feature: Feature): Membership =>
    plan?.includedFeatures.includes(feature.slug)
      ? "included"
      : plan?.optionalFeatures.includes(feature.slug)
        ? "optional"
        : "none";

  const setMembership = async (feature: Feature, target: Membership) => {
    if (!plan) return;
    setBusyId(feature.id);
    setError(null);
    try {
      if (target === "none") {
        await apiRequest(`/plans/${plan.id}/features/${feature.id}`, { method: "DELETE" });
      } else {
        await apiRequest(`/plans/${plan.id}/features`, {
          method: "PUT",
          body: { featureId: feature.id, isIncluded: target === "included", isCustomerToggleable: target === "optional" },
        });
      }
      onChanged();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setBusyId(null);
    }
  };

  return (
    <Drawer
      open={plan !== null}
      title={t("plans.manageFeatures", "Features & pricing")}
      subtitle={plan?.name}
      onClose={onClose}
    >
      {plan ? (
        <div className="flex flex-col gap-4">
          <p className="text-body-sm text-on-surface-variant">
            {t("plans.manageFeaturesHint", "Choose which features this plan includes or offers, and set what each one costs on it.")}
          </p>

          {error ? (
            <p role="alert" className="rounded-md bg-error-container px-3 py-2 text-body-sm text-on-error-container">
              {error}
            </p>
          ) : null}

          <div className="flex flex-col gap-2">
            {(features.data ?? []).map((feature) => {
              const state = membershipOf(feature);
              return (
                <div key={feature.id} className="rounded-md border border-outline-variant p-3">
                  <div className="flex flex-wrap items-center justify-between gap-3">
                    <div>
                      <p className="text-body-sm font-medium text-on-surface">{feature.name}</p>
                      <Mono>{feature.slug}</Mono>
                    </div>
                    <Segmented
                      value={state}
                      busy={busyId === feature.id}
                      onChange={(target) => void setMembership(feature, target)}
                    />
                  </div>

                  {state !== "none" ? (
                    <div className="mt-2">
                      <button
                        type="button"
                        onClick={() => setPricingFor((current) => (current === feature.id ? null : feature.id))}
                        className="inline-flex items-center gap-1.5 text-body-sm text-primary hover:underline"
                      >
                        <Tag className="size-3.5" aria-hidden />
                        {pricingFor === feature.id ? t("plans.hidePricing", "Hide pricing") : t("plans.pricing", "Pricing")}
                      </button>
                      {pricingFor === feature.id ? (
                        <PricingPanel plan={plan} feature={feature} onChanged={onChanged} />
                      ) : null}
                    </div>
                  ) : null}
                </div>
              );
            })}
          </div>
        </div>
      ) : null}
    </Drawer>
  );
}

function Segmented({ value, busy, onChange }: { value: Membership; busy: boolean; onChange: (v: Membership) => void }) {
  const { t } = useTranslation();
  const options: { key: Membership; label: string }[] = [
    { key: "none", label: t("plans.notInPlan", "Not in plan") },
    { key: "optional", label: t("plans.optional", "Optional") },
    { key: "included", label: t("plans.included", "Included") },
  ];
  return (
    <div className="inline-flex overflow-hidden rounded-lg border border-outline-variant" role="group">
      {options.map((option) => (
        <button
          key={option.key}
          type="button"
          disabled={busy}
          aria-pressed={value === option.key}
          onClick={() => onChange(option.key)}
          className={cn(
            "px-3 py-1.5 text-body-sm transition-colors disabled:opacity-50",
            value === option.key ? "bg-primary text-on-primary" : "text-on-surface-variant hover:bg-surface-high",
          )}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}

const BILLING_PERIODS = ["Monthly", "Yearly", "OneTime"] as const;

function PricingPanel({ plan, feature, onChanged }: { plan: Plan; feature: Feature; onChanged: () => void }) {
  const { t } = useTranslation();
  const prices = useCollection<FeaturePrice>(`/plans/prices/${feature.id}`);

  const [amount, setAmount] = useState("");
  const [billingPeriod, setBillingPeriod] = useState<string>("Monthly");
  const [scopeToPlan, setScopeToPlan] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    const value = Number(amount);
    if (!Number.isFinite(value) || value < 0) {
      setError(t("plans.priceInvalid", "Enter a price of zero or more."));
      return;
    }
    setSaving(true);
    setError(null);
    try {
      await apiRequest("/plans/prices", {
        method: "PUT",
        body: {
          featureId: feature.id,
          planId: scopeToPlan ? plan.id : null,
          amount: value,
          currency: plan.currency,
          billingPeriod,
        },
      });
      setAmount("");
      await prices.refetch();
      onChanged();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setSaving(false);
    }
  };

  const scopeLabel = (planId: string | null) =>
    planId === null
      ? t("plans.scopeAll", "All plans")
      : planId === plan.id
        ? t("plans.scopeThis", "This plan")
        : t("plans.scopeOther", "Another plan");

  const inForce = (price: FeaturePrice) => price.validTo === null;

  return (
    <div className="mt-3 flex flex-col gap-3 rounded-md bg-surface-low p-3">
      <div className="flex flex-wrap items-end gap-2">
        <label className="flex flex-col gap-1 text-body-sm">
          <span className="text-on-surface-variant">{t("plans.amount", "Amount")}</span>
          <input
            value={amount}
            onChange={(event) => setAmount(event.target.value)}
            inputMode="decimal"
            dir="ltr"
            placeholder="0"
            className="w-28 rounded-md border border-outline-variant bg-surface px-2 py-1.5 text-on-surface focus:border-primary focus:outline-none"
          />
        </label>
        <label className="flex flex-col gap-1 text-body-sm">
          <span className="text-on-surface-variant">{t("plans.billingPeriod", "Billing")}</span>
          <select
            value={billingPeriod}
            onChange={(event) => setBillingPeriod(event.target.value)}
            className="rounded-md border border-outline-variant bg-surface px-2 py-1.5 text-on-surface focus:border-primary focus:outline-none"
          >
            {BILLING_PERIODS.map((period) => (
              <option key={period} value={period}>
                {t(`plans.period.${period}`, period)}
              </option>
            ))}
          </select>
        </label>
        <label className="flex items-center gap-2 py-1.5 text-body-sm text-on-surface">
          <input type="checkbox" className="size-4 rounded-sm accent-[var(--primary)]" checked={scopeToPlan} onChange={(event) => setScopeToPlan(event.target.checked)} />
          {t("plans.scopeToPlan", "Only this plan")}
        </label>
        <Button size="sm" loading={saving} onClick={() => void submit()}>
          {t("plans.setPrice", "Set price")}
        </Button>
      </div>

      {error ? <p role="alert" className="text-body-sm text-error">{error}</p> : null}

      <div className="overflow-x-auto">
        <table className="w-full text-body-sm">
          <thead>
            <tr className="border-b border-outline-variant text-on-surface-variant">
              <th scope="col" className="py-1.5 text-start font-normal">{t("plans.price", "Price")}</th>
              <th scope="col" className="py-1.5 text-start font-normal">{t("plans.scope", "Scope")}</th>
              <th scope="col" className="py-1.5 text-start font-normal">{t("plans.from", "From")}</th>
              <th scope="col" className="py-1.5 text-start font-normal">{t("plans.to", "To")}</th>
            </tr>
          </thead>
          <tbody>
            {(prices.data ?? []).length === 0 ? (
              <tr>
                <td colSpan={4} className="py-2 text-on-surface-variant">{t("plans.noPrices", "No price set yet.")}</td>
              </tr>
            ) : (
              (prices.data ?? []).map((price) => (
                <tr key={price.id} className={cn("border-b border-outline-variant/50", inForce(price) && "text-on-surface")}>
                  <td className="py-1.5">
                    <Mono>{formatNumber(price.amount)} {price.currency}</Mono>
                    <span className="ms-1 text-on-surface-variant">/ {t(`plans.period.${price.billingPeriod}`, price.billingPeriod)}</span>
                  </td>
                  <td className="py-1.5 text-on-surface-variant">{scopeLabel(price.planId)}</td>
                  <td className="py-1.5 text-on-surface-variant">{formatDateTime(price.validFrom)}</td>
                  <td className="py-1.5 text-on-surface-variant">
                    {price.validTo === null ? t("plans.current", "current") : formatDateTime(price.validTo)}
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}
