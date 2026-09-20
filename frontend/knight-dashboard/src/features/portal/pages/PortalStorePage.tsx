import { useMemo, useState } from "react";
import { useParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { ArrowRight, ArrowLeft, Store, CheckCircle2, Loader2, ExternalLink } from "lucide-react";
import { Card, CardBody, CardHeader } from "@/components/ui/Card";
import { Meter } from "@/components/ui/Meter";
import { Button } from "@/components/ui/Button";
import { StatusChip } from "@/components/ui/StatusChip";
import { LoadingBlock, ErrorBlock } from "@/components/ui/StateBlock";
import { ApiError } from "@/lib/api/problem";
import { ButtonLink } from "../components";
import {
  useMyStores,
  useMySubscription,
  usePublicPlans,
  useCheckout,
  useProvisioning,
  type CheckoutResponse,
} from "../api";

/**
 * One store, managed from inside the portal: its status, a way in to the store's
 * own admin panel, and — the point of this page — add or remove the paid features
 * for this store without leaving KNIGHT. Provisioning progress only shows while a
 * store is still coming up.
 */
export function PortalStorePage() {
  const { t } = useTranslation();
  const { storeId } = useParams<{ storeId: string }>();
  const stores = useMyStores();
  const subscription = useMySubscription();
  const plans = usePublicPlans();

  if (stores.isLoading || subscription.isLoading || plans.isLoading) return <LoadingBlock rows={6} />;

  const failed = [stores, subscription, plans].find((q) => q.isError);
  if (failed?.isError) {
    const status = failed.error instanceof ApiError ? failed.error.status : undefined;
    const message = failed.error instanceof Error ? failed.error.message : String(failed.error);
    return <ErrorBlock message={message} status={status} onRetry={() => void failed.refetch()} />;
  }

  const store = stores.data?.find((s) => s.id === storeId);

  return (
    <div className="flex flex-col gap-6">
      <ButtonLink variant="outline" to="/portal" className="w-fit">
        <ArrowRight className="size-4 rtl:-scale-x-100" aria-hidden />
        {t("portal.store.back")}
      </ButtonLink>

      {store ? (
        <>
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="flex items-center gap-3">
              <span className="grid size-11 place-items-center rounded-lg bg-surface-high text-on-surface-variant">
                <Store className="size-5" aria-hidden />
              </span>
              <div>
                <h1 className="text-headline font-semibold text-on-surface">{store.name}</h1>
                <p dir="ltr" className="text-body-sm text-on-surface-variant">{store.primaryDomain}</p>
              </div>
            </div>
            {store.isReady ? (
              <StatusChip tone="success">
                <CheckCircle2 className="size-3.5" aria-hidden /> {t("portal.store.ready")}
              </StatusChip>
            ) : (
              <StatusChip tone="info">
                <Loader2 className="size-3.5 animate-spin" aria-hidden /> {t("portal.store.provisioning")}
              </StatusChip>
            )}
          </div>

          {store.isReady ? (
            <>
              <Card>
                <CardHeader title={t("portal.store.access", "دسترسی")} />
                <CardBody className="flex flex-wrap gap-2">
                  <ButtonLink href={`https://admin.${store.primaryDomain}`} target="_blank" rel="noreferrer">
                    {t("portal.store.manage", "مدیریت فروشگاه")}
                    <ExternalLink className="size-4" aria-hidden />
                  </ButtonLink>
                  <ButtonLink variant="outline" href={`https://${store.primaryDomain}`} target="_blank" rel="noreferrer">
                    {t("portal.store.open")}
                    <ExternalLink className="size-4" aria-hidden />
                  </ButtonLink>
                </CardBody>
              </Card>

              <FeatureManager
                currentFeatureIds={subscription.data?.featureIds ?? []}
                currentPlanId={subscription.data?.planId}
              />
            </>
          ) : (
            <ProvisioningProgress storeId={store.id} />
          )}
        </>
      ) : (
        <ErrorBlock message={t("common.noResults", "فروشگاهی پیدا نشد.")} />
      )}
    </div>
  );
}

/** Add or remove this store's paid features, priced live, then to checkout. */
function FeatureManager({
  currentFeatureIds,
  currentPlanId,
}: {
  currentFeatureIds: string[];
  currentPlanId?: string | undefined;
}) {
  const { t } = useTranslation();
  const plans = usePublicPlans();
  const checkout = useCheckout();
  const [result, setResult] = useState<CheckoutResponse | null>(null);

  // The plan the store is on (fall back to the first sellable plan) and its
  // optional features are what a merchant can turn on and off here.
  const plan = useMemo(() => {
    const list = plans.data ?? [];
    return list.find((p) => p.id === currentPlanId) ?? list[0];
  }, [plans.data, currentPlanId]);

  const [selected, setSelected] = useState<Set<string>>(() => new Set(currentFeatureIds));

  if (!plan) return null;

  const toggle = (id: string) =>
    setSelected((prev) => {
      const next = new Set(prev);
      next.has(id) ? next.delete(id) : next.add(id);
      return next;
    });

  const monthly =
    plan.basePrice +
    plan.optionalFeatures
      .filter((f) => selected.has(f.featureId))
      .reduce((sum, f) => sum + (f.price ?? 0), 0);

  const changed =
    selected.size !== currentFeatureIds.length ||
    currentFeatureIds.some((id) => !selected.has(id));

  const apply = () => {
    checkout.mutate(
      { planId: plan.id, billingInterval: "Monthly", selectedFeatureIds: [...selected] },
      { onSuccess: setResult },
    );
  };

  return (
    <Card>
      <CardHeader title={t("portal.store.featuresTitle", "فیچرهای فروشگاه")} />
      <CardBody className="flex flex-col gap-4">
        <p className="text-body-sm text-on-surface-variant">
          {t(
            "portal.store.featuresHint",
            "هر فیچر را روشن/خاموش کنید و «اعمال و پرداخت» را بزنید. بعد از پرداخت، فیچر روی فروشگاه شما فعال/غیرفعال می‌شود.",
          )}
        </p>

        {plan.optionalFeatures.length === 0 ? (
          <p className="text-body-sm text-on-surface-variant">
            {t("portal.store.noOptional", "برای این پلن فیچر اختیاری‌ای تعریف نشده.")}
          </p>
        ) : (
          <ul className="flex flex-col divide-y divide-outline-variant rounded-lg border border-outline-variant">
            {plan.optionalFeatures.map((f) => {
              const on = selected.has(f.featureId);
              return (
                <li key={f.featureId} className="flex items-center justify-between gap-3 p-3">
                  <div className="min-w-0">
                    <p className="text-body-sm font-medium text-on-surface">{f.name}</p>
                    {f.description ? (
                      <p className="mt-0.5 text-body-sm leading-6 text-on-surface-variant">{f.description}</p>
                    ) : null}
                  </div>
                  <button
                    type="button"
                    role="switch"
                    aria-checked={on}
                    aria-label={f.name}
                    onClick={() => toggle(f.featureId)}
                    className={
                      "relative h-6 w-11 shrink-0 rounded-full transition-colors " +
                      (on ? "bg-primary" : "bg-on-surface/20")
                    }
                  >
                    <span
                      className={
                        "absolute top-0.5 size-5 rounded-full bg-white transition-all " +
                        (on ? "start-0.5" : "end-0.5")
                      }
                    />
                  </button>
                </li>
              );
            })}
          </ul>
        )}

        <div className="flex flex-wrap items-center justify-between gap-3">
          <p className="text-body-sm text-on-surface-variant">
            {t("portal.store.monthlyEstimate", "برآورد ماهانه")}:{" "}
            <span className="font-semibold text-on-surface">{monthly.toLocaleString("fa-IR")}</span>
          </p>
          <Button onClick={apply} loading={checkout.isPending} disabled={!changed}>
            {t("portal.store.applyAndPay", "اعمال و پرداخت")}
            <ArrowLeft className="size-4 rtl:-scale-x-100" aria-hidden />
          </Button>
        </div>

        {result ? (
          <div className="rounded-lg bg-primary/10 p-3">
            <p className="text-body-sm text-on-surface">{t("portal.checkout.readyBody")}</p>
            <ButtonLink href={result.checkoutUrl} className="mt-2">
              {t("portal.checkout.pay")}
              <ArrowLeft className="size-4 rtl:-scale-x-100" aria-hidden />
            </ButtonLink>
          </div>
        ) : null}

        {checkout.isError ? (
          <p role="alert" className="rounded-md bg-error/15 px-3 py-2 text-body-sm text-error">
            {checkout.error instanceof Error ? checkout.error.message : t("common.errorTitle")}
          </p>
        ) : null}
      </CardBody>
    </Card>
  );
}

/** The step list, shown only while a store is still coming up. */
function ProvisioningProgress({ storeId }: { storeId: string }) {
  const { t } = useTranslation();
  const provisioning = useProvisioning(storeId);

  if (provisioning.isLoading) return <LoadingBlock rows={4} />;
  const progress = provisioning.data;
  if (!progress) return null;

  return (
    <Card>
      <CardHeader title={t("portal.provisioning.title")} />
      <CardBody className="flex flex-col gap-4">
        <p className="text-body font-medium text-on-surface">{progress.friendlyStatus}</p>
        <Meter label={t("portal.provisioning.progress")} value={progress.percentComplete} />
      </CardBody>
    </Card>
  );
}
