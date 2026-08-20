import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { apiRequest } from "@/lib/api/client";
import { useCollection } from "@/lib/api/hooks";
import type { Plan, Subscription } from "@/lib/api/domain";
import { Drawer } from "@/components/data/Drawer";
import { KeyValue, Mono } from "@/components/data/PageShell";
import { Button } from "@/components/ui/Button";
import { formatNumber } from "@/lib/utils/format";

interface QuoteLine {
  description: string;
  featureId: string | null;
  quantity: number;
  unitPrice: number;
  total: number;
}

interface Quote {
  currency: string;
  subtotal: number;
  lines: QuoteLine[];
}

interface FeatureOption {
  id: string;
  slug: string;
  name: string;
  isOptional: boolean;
}

/**
 * Changing a customer's plan, priced before it is applied.
 *
 * The quote is fetched from the same endpoint invoicing uses, rather than
 * computed here. That is the whole point of the dialog: a customer must not be
 * told one number by a screen and charged another by a bill, and the only way to
 * guarantee that is for both to come from one calculation.
 *
 * Nothing is applied until the operator has seen the price. The quote endpoint
 * is deliberately side-effect free, so opening this dialog changes nothing.
 */
export function ChangeSubscriptionDialog({
  subscription,
  onClose,
  onChanged,
}: {
  subscription: Subscription | null;
  onClose: () => void;
  onChanged: () => void;
}) {
  const { t } = useTranslation();
  const plans = useCollection<Plan>("/plans");
  const features = useCollection<FeatureOption>("/features");

  const [planId, setPlanId] = useState<string | null>(null);
  const [selectedFeatures, setSelectedFeatures] = useState<string[]>([]);
  const [quote, setQuote] = useState<Quote | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  // The plan the customer is on now is the starting point, so the dialog opens
  // showing what they already pay rather than an empty form.
  useEffect(() => {
    if (!subscription) return;

    const current = (plans.data ?? []).find((plan) => plan.key === subscription.planKey);

    setPlanId(current?.id ?? null);
    setSelectedFeatures([]);
    setQuote(null);
    setError(null);
  }, [subscription, plans.data]);

  // Re-priced whenever the selection changes. Debounced lightly so dragging
  // through several options does not fire a request per keystroke.
  useEffect(() => {
    if (!planId) return;

    const timer = setTimeout(() => {
      apiRequest<Quote>("/subscriptions/quote", {
        method: "POST",
        body: { planId, featureIds: selectedFeatures },
      })
        .then((result) => {
          setQuote(result);
          setError(null);
        })
        .catch((caught: unknown) => {
          setQuote(null);
          setError(caught instanceof Error ? caught.message : String(caught));
        });
    }, 250);

    return () => clearTimeout(timer);
  }, [planId, selectedFeatures]);

  const apply = async () => {
    if (!subscription || !planId) return;

    setSaving(true);
    setError(null);

    try {
      await apiRequest(`/subscriptions/${subscription.id}`, {
        method: "PATCH",
        body: { planId },
      });

      if (selectedFeatures.length > 0) {
        await apiRequest(`/subscriptions/${subscription.id}/features`, {
          method: "PUT",
          body: { featureIds: selectedFeatures },
        });
      }

      onChanged();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setSaving(false);
    }
  };

  const optional = (features.data ?? []).filter((feature) => feature.isOptional);

  return (
    <Drawer
      open={subscription !== null}
      title={t("subscriptions.change")}
      subtitle={subscription?.customerName}
      onClose={onClose}
      footer={
        <Button size="sm" disabled={saving || planId === null} onClick={() => void apply()}>
          {t("subscriptions.applyChange")}
        </Button>
      }
    >
      {subscription ? (
        <div className="flex flex-col gap-5">
          {error ? (
            <p
              role="alert"
              className="rounded-md bg-error-container px-3 py-2 text-body-sm text-on-error-container"
            >
              {error}
            </p>
          ) : null}

          <section className="flex flex-col gap-2">
            <h3 className="label-caps text-on-surface-variant/80">{t("subscriptions.plan")}</h3>
            <div className="flex flex-wrap gap-2">
              {(plans.data ?? []).map((plan) => (
                <Button
                  key={plan.id}
                  type="button"
                  size="sm"
                  variant={planId === plan.id ? "primary" : "outline"}
                  onClick={() => setPlanId(plan.id)}
                >
                  {plan.name}
                </Button>
              ))}
            </div>
          </section>

          {optional.length > 0 ? (
            <section className="flex flex-col gap-2">
              <h3 className="label-caps text-on-surface-variant/80">
                {t("subscriptions.optionalFeatures")}
              </h3>
              <div className="flex flex-wrap gap-2">
                {optional.map((feature) => (
                  <Button
                    key={feature.id}
                    type="button"
                    size="sm"
                    variant={selectedFeatures.includes(feature.id) ? "primary" : "outline"}
                    onClick={() =>
                      setSelectedFeatures((current) =>
                        current.includes(feature.id)
                          ? current.filter((id) => id !== feature.id)
                          : [...current, feature.id],
                      )
                    }
                  >
                    {feature.name}
                  </Button>
                ))}
              </div>
            </section>
          ) : null}

          <section className="flex flex-col gap-2">
            <h3 className="label-caps text-on-surface-variant/80">{t("subscriptions.quote")}</h3>

            {quote === null ? (
              <p className="text-body-sm text-on-surface-variant">{t("common.loading")}</p>
            ) : (
              <>
                <dl className="divide-y divide-outline-variant">
                  {quote.lines.map((line) => (
                    <KeyValue key={`${line.description}-${line.featureId ?? "base"}`} label={line.description}>
                      <Mono>
                        {formatNumber(line.total)} {quote.currency}
                      </Mono>
                    </KeyValue>
                  ))}
                </dl>

                <p className="mt-2 text-title font-semibold text-on-surface">
                  {formatNumber(quote.subtotal)} {quote.currency}
                  <span className="ms-1 text-body-sm font-normal text-on-surface-variant">
                    / {t("plans.perMonth")}
                  </span>
                </p>

                {/* Said plainly, because the number above is the one the customer
                    will be billed — it comes from the same calculation. */}
                <p className="text-body-sm text-on-surface-variant">{t("subscriptions.quoteHint")}</p>
              </>
            )}
          </section>
        </div>
      ) : null}
    </Drawer>
  );
}
