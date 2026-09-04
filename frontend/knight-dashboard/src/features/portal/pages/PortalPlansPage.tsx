import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { Check, CreditCard, ArrowRight } from "lucide-react";
import { Card, CardBody } from "@/components/ui/Card";
import { Button } from "@/components/ui/Button";
import { LoadingBlock, ErrorBlock } from "@/components/ui/StateBlock";
import { ApiError } from "@/lib/api/problem";
import { cn } from "@/lib/utils/cn";
import { ButtonLink } from "../components";
import { formatMoney } from "../money";
import { usePublicPlans, useCheckout, type PublicPlan, type CheckoutResponse } from "../api";

type Interval = "monthly" | "yearly";

/**
 * Choose a plan, add optional features (the CUSTOM selection), and check out. The
 * price shown is only ever a preview: the checkout the server opens computes the
 * authoritative amount itself (docs/self-service-saas-plan.md §6).
 */
export function PortalPlansPage() {
  const { t } = useTranslation();
  const plans = usePublicPlans();
  const checkout = useCheckout();

  const [planId, setPlanId] = useState<string | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [interval, setInterval] = useState<Interval>("monthly");
  const [result, setResult] = useState<CheckoutResponse | null>(null);

  const plan = useMemo(() => plans.data?.find((p) => p.id === planId) ?? null, [plans.data, planId]);

  if (plans.isLoading) return <LoadingBlock rows={6} />;
  if (plans.isError) {
    const status = plans.error instanceof ApiError ? plans.error.status : undefined;
    const message = plans.error instanceof Error ? plans.error.message : String(plans.error);
    return <ErrorBlock message={message} status={status} onRetry={() => void plans.refetch()} />;
  }

  const list = plans.data ?? [];

  const toggleFeature = (id: string) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const choosePlan = (p: PublicPlan) => {
    setPlanId(p.id);
    setSelected(new Set());
    setResult(null);
  };

  const previewTotal = plan
    ? plan.basePrice +
      plan.optionalFeatures.filter((f) => selected.has(f.featureId)).reduce((sum, f) => sum + (f.price ?? 0), 0)
    : 0;

  const onCheckout = () => {
    if (!plan) return;
    checkout.mutate(
      { planId: plan.id, billingInterval: interval, selectedFeatureIds: [...selected] },
      { onSuccess: setResult },
    );
  };

  if (result) {
    return (
      <div className="mx-auto max-w-md">
        <Card>
          <CardBody className="flex flex-col items-start gap-4">
            <span className="grid size-12 place-items-center rounded-xl bg-primary/15 text-primary">
              <CreditCard className="size-6" aria-hidden />
            </span>
            <div>
              <h2 className="text-title font-semibold text-on-surface">{t("portal.checkout.readyTitle")}</h2>
              <p className="mt-1 text-body-sm text-on-surface-variant">{t("portal.checkout.readyBody")}</p>
            </div>
            <p className="text-headline font-semibold text-on-surface">{formatMoney(result.amount, result.currency)}</p>
            <ButtonLink href={result.checkoutUrl}>
              {t("portal.checkout.pay")}
              <ArrowRight className="size-4 rtl:-scale-x-100" aria-hidden />
            </ButtonLink>
          </CardBody>
        </Card>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <h1 className="text-headline font-semibold text-on-surface">{t("portal.plans.title")}</h1>
        <p className="mt-1 text-body-sm text-on-surface-variant">{t("portal.plans.subtitle")}</p>
      </div>

      <div className="inline-flex w-fit rounded-lg bg-surface-low p-1">
        {(["monthly", "yearly"] as Interval[]).map((value) => (
          <button
            key={value}
            type="button"
            onClick={() => setInterval(value)}
            className={cn(
              "rounded-md px-4 py-1.5 text-body-sm font-medium transition-colors",
              interval === value ? "bg-primary text-on-primary" : "text-on-surface-variant hover:text-on-surface",
            )}
          >
            {t(`portal.plans.${value}`)}
          </button>
        ))}
      </div>

      <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
        {list.map((p) => (
          <button
            key={p.id}
            type="button"
            onClick={() => choosePlan(p)}
            className={cn(
              "card-surface flex flex-col gap-3 p-5 text-start transition-colors",
              planId === p.id ? "ring-2 ring-primary" : "hover:bg-surface-high",
            )}
          >
            <div className="flex items-center justify-between">
              <h3 className="text-title font-semibold text-on-surface">{p.name}</h3>
              {planId === p.id ? <Check className="size-5 text-primary" aria-hidden /> : null}
            </div>
            <p className="text-headline font-semibold text-on-surface">
              {formatMoney(p.basePrice, p.currency)}
              <span className="text-body-sm font-normal text-on-surface-variant"> / {t(`portal.plans.${interval}`)}</span>
            </p>
            {p.description ? <p className="text-body-sm text-on-surface-variant">{p.description}</p> : null}
            <ul className="mt-1 flex flex-col gap-1.5">
              {p.includedFeatures.map((f) => (
                <li key={f.featureId} className="flex items-center gap-2 text-body-sm text-on-surface-variant">
                  <Check className="size-4 shrink-0 text-success" aria-hidden />
                  {f.name}
                </li>
              ))}
            </ul>
          </button>
        ))}
      </div>

      {plan && plan.optionalFeatures.length > 0 ? (
        <Card>
          <CardBody className="flex flex-col gap-3">
            <h3 className="text-title font-semibold text-on-surface">{t("portal.plans.addOns")}</h3>
            <div className="flex flex-col gap-2">
              {plan.optionalFeatures.map((f) => (
                <label
                  key={f.featureId}
                  className="flex cursor-pointer items-center justify-between gap-3 rounded-md border border-outline-variant px-3 py-2.5 hover:bg-surface-high"
                >
                  <span className="flex items-center gap-2.5">
                    <input
                      type="checkbox"
                      className="size-4 rounded-sm accent-[var(--primary)]"
                      checked={selected.has(f.featureId)}
                      onChange={() => toggleFeature(f.featureId)}
                    />
                    <span>
                      <span className="block text-body-sm font-medium text-on-surface">{f.name}</span>
                      {f.description ? (
                        <span className="block text-body-sm text-on-surface-variant">{f.description}</span>
                      ) : null}
                    </span>
                  </span>
                  <span className="text-body-sm font-medium text-on-surface">
                    {f.price === null ? "—" : `+${formatMoney(f.price, f.currency)}`}
                  </span>
                </label>
              ))}
            </div>
          </CardBody>
        </Card>
      ) : null}

      {plan ? (
        <div className="flex flex-wrap items-center justify-between gap-4 rounded-lg bg-surface-low px-5 py-4">
          <div>
            <p className="text-body-sm text-on-surface-variant">{t("portal.plans.total")}</p>
            <p className="text-headline font-semibold text-on-surface">{formatMoney(previewTotal, plan.currency)}</p>
          </div>
          <Button onClick={onCheckout} loading={checkout.isPending}>
            {t("portal.plans.checkout")}
            <ArrowRight className="size-4 rtl:-scale-x-100" aria-hidden />
          </Button>
        </div>
      ) : null}

      {checkout.isError ? (
        <p role="alert" className="rounded-md bg-error/15 px-3 py-2 text-body-sm text-error">
          {checkout.error instanceof Error ? checkout.error.message : t("common.errorTitle")}
        </p>
      ) : null}
    </div>
  );
}
