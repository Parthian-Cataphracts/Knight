import { useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { CreditCard, CheckCircle2, ShieldCheck } from "lucide-react";
import { Card, CardBody, CardHeader } from "@/components/ui/Card";
import { Button } from "@/components/ui/Button";
import { useSimulatedPay } from "../api";

/**
 * The test payment gateway.
 *
 * Self-service checkout sends the merchant here (the simulated provider's
 * "hosted page"). Paying settles the checkout by posting the provider's webhook,
 * which activates the subscription; a real provider would replace this whole page
 * with its own. There is no card here because there is no real charge — it is
 * clearly marked as a test so nobody mistakes it for one.
 */
export function PortalPayPage() {
  const { t } = useTranslation();
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const pay = useSimulatedPay();
  const [done, setDone] = useState(false);

  const session = params.get("session") ?? "";

  const settle = (succeed: boolean) => {
    if (!session) return;
    pay.mutate(
      { session, succeed },
      {
        onSuccess: () => {
          setDone(true);
          // Give the webhook a moment to activate the subscription, then return.
          setTimeout(() => navigate("/portal", { replace: true }), 1200);
        },
      },
    );
  };

  return (
    <div className="mx-auto flex max-w-lg flex-col gap-6 py-8">
      <Card>
        <CardHeader title={t("portal.pay.title", "پرداخت آزمایشی")} />
        <CardBody className="flex flex-col gap-5">
          <div className="flex items-start gap-3 rounded-lg bg-primary/10 p-3">
            <ShieldCheck className="mt-0.5 size-5 shrink-0 text-primary" aria-hidden />
            <p className="text-body-sm leading-6 text-on-surface-variant">
              {t(
                "portal.pay.note",
                "این یک درگاه آزمایشی است و هیچ مبلغی واقعاً از شما گرفته نمی‌شود. با «پرداخت موفق»، اشتراک و فیچرهای انتخابی‌تان فعال می‌شوند.",
              )}
            </p>
          </div>

          {!session ? (
            <p role="alert" className="rounded-md bg-error/15 px-3 py-2 text-body-sm text-error">
              {t("portal.pay.noSession", "نشست پرداخت پیدا نشد. از صفحهٔ پلن‌ها دوباره اقدام کنید.")}
            </p>
          ) : done ? (
            <div className="flex items-center gap-2 rounded-md bg-primary/10 px-3 py-3 text-body-sm text-on-surface">
              <CheckCircle2 className="size-5 text-primary" aria-hidden />
              {t("portal.pay.doneRedirect", "پرداخت ثبت شد؛ در حال بازگشت به پورتال…")}
            </div>
          ) : (
            <div className="flex flex-col gap-3">
              <Button onClick={() => settle(true)} loading={pay.isPending}>
                <CreditCard className="size-4" aria-hidden />
                {t("portal.pay.succeed", "پرداخت موفق (آزمایشی)")}
              </Button>
              <Button variant="outline" onClick={() => settle(false)} disabled={pay.isPending}>
                {t("portal.pay.fail", "پرداخت ناموفق / لغو")}
              </Button>
              {pay.isError ? (
                <p role="alert" className="rounded-md bg-error/15 px-3 py-2 text-body-sm text-error">
                  {pay.error instanceof Error ? pay.error.message : t("common.errorTitle")}
                </p>
              ) : null}
            </div>
          )}
        </CardBody>
      </Card>
    </div>
  );
}
