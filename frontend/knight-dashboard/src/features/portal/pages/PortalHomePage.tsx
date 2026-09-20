import { Link } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useQueryClient } from "@tanstack/react-query";
import { Store, Sparkles, ArrowRight, CheckCircle2, Loader2, Download, Bot } from "lucide-react";
import { Card, CardBody, CardHeader } from "@/components/ui/Card";
import { Button } from "@/components/ui/Button";
import { StatusChip } from "@/components/ui/StatusChip";
import { Meter } from "@/components/ui/Meter";
import { LoadingBlock, ErrorBlock } from "@/components/ui/StateBlock";
import { formatDateTime } from "@/lib/utils/format";
import { ApiError } from "@/lib/api/problem";
import { ButtonLink } from "../components";
import { planNameFromDisplay } from "../catalogueLabels";
import { provisioningStatusText, isPublicDomain } from "../provisioning";
import { useCancelSubscription, useExportMyData, useMyStores, useMySubscription, useProvisioning, type MeStore } from "../api";

/**
 * The portal's home. What a merchant sees depends only on where they are in the
 * journey: no subscription yet → choose a plan; a store still coming up → watch
 * it; a store ready → open and manage it.
 */
export function PortalHomePage() {
  const { t } = useTranslation();
  const subscription = useMySubscription();
  const stores = useMyStores();

  if (subscription.isLoading || stores.isLoading) return <LoadingBlock rows={6} />;
  if (subscription.isError) {
    const status = subscription.error instanceof ApiError ? subscription.error.status : undefined;
    const message = subscription.error instanceof Error ? subscription.error.message : String(subscription.error);
    return <ErrorBlock message={message} status={status} onRetry={() => void subscription.refetch()} />;
  }

  const store = stores.data?.[0];

  return (
    <div className="flex flex-col gap-6">
      <div>
        <h1 className="text-headline font-semibold text-on-surface">{t("portal.home.title")}</h1>
        <p className="mt-1 text-body-sm text-on-surface-variant">{t("portal.home.subtitle")}</p>
      </div>

      {!subscription.data ? (
        <Card>
          <CardBody className="flex flex-col items-start gap-4">
            <span className="grid size-12 place-items-center rounded-xl bg-primary/15 text-primary">
              <Sparkles className="size-6" aria-hidden />
            </span>
            <div>
              <h2 className="text-title font-semibold text-on-surface">{t("portal.home.noPlanTitle")}</h2>
              <p className="mt-1 text-body-sm text-on-surface-variant">{t("portal.home.noPlanBody")}</p>
            </div>
            <ButtonLink to="/portal/plans">
              {t("portal.home.choosePlan")}
              <ArrowRight className="size-4 rtl:-scale-x-100" aria-hidden />
            </ButtonLink>
          </CardBody>
        </Card>
      ) : (
        <>
          <SubscriptionCard subscription={subscription.data} />
          {store ? <StoreCard store={store} /> : <PendingStore />}
          <AutoAdminCard />
        </>
      )}

      <PortalGuide />
    </div>
  );
}

function SubscriptionCard({ subscription }: { subscription: NonNullable<ReturnType<typeof useMySubscription>["data"]> }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const cancel = useCancelSubscription();
  const exportData = useExportMyData();

  const onCancel = () => {
    cancel.mutate(undefined, {
      onSuccess: () => queryClient.invalidateQueries({ queryKey: ["portal", "me", "subscription"] }),
    });
  };

  return (
    <Card>
      <CardHeader title={t("portal.subscription.title")} />
      <CardBody className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <p className="text-title font-semibold text-on-surface">{planNameFromDisplay(subscription.planName)}</p>
          <p className="mt-1 text-body-sm text-on-surface-variant">
            {t("portal.subscription.renews", { date: formatDateTime(subscription.currentPeriodEnd) })}
          </p>
        </div>
        <div className="flex items-center gap-2">
          {subscription.cancelAtPeriodEnd ? (
            <StatusChip tone="warning">{t("portal.subscription.ending")}</StatusChip>
          ) : (
            <StatusChip tone="success">{t(`portal.subStatus.${subscription.status}`, subscription.status)}</StatusChip>
          )}
          <Button
            variant="ghost"
            size="sm"
            onClick={() => exportData.mutate()}
            loading={exportData.isPending}
          >
            <Download className="size-4" aria-hidden />
            {t("portal.subscription.export")}
          </Button>
          {!subscription.cancelAtPeriodEnd ? (
            <Button variant="ghost" size="sm" onClick={onCancel} loading={cancel.isPending}>
              {t("portal.subscription.cancel")}
            </Button>
          ) : null}
        </div>
      </CardBody>
    </Card>
  );
}

function StoreCard({ store }: { store: MeStore }) {
  const { t } = useTranslation();
  const provisioning = useProvisioning(store.isReady ? undefined : store.id);
  const progress = provisioning.data;

  return (
    <Card>
      <CardHeader title={t("portal.store.title")} />
      <CardBody className="flex flex-col gap-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex items-center gap-3">
            <span className="grid size-10 place-items-center rounded-lg bg-surface-high text-on-surface-variant">
              <Store className="size-5" aria-hidden />
            </span>
            <div>
              <p className="font-medium text-on-surface">{store.name}</p>
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
          <div className="flex flex-col gap-3">
            {isPublicDomain(store.primaryDomain) ? (
              <div className="flex flex-wrap gap-2">
                <ButtonLink href={`https://admin.${store.primaryDomain}`} target="_blank" rel="noreferrer">
                  {t("portal.store.manage", "مدیریت فروشگاه")}
                  <ArrowRight className="size-4 rtl:-scale-x-100" aria-hidden />
                </ButtonLink>
                <ButtonLink variant="outline" href={`https://${store.primaryDomain}`} target="_blank" rel="noreferrer">
                  {t("portal.store.open")}
                </ButtonLink>
                <ButtonLink variant="outline" to={`/portal/stores/${store.id}`}>
                  {t("portal.store.details")}
                </ButtonLink>
              </div>
            ) : (
              <>
                <p className="rounded-md bg-primary/10 px-3 py-2 text-body-sm leading-6 text-on-surface-variant">
                  {t(
                    "portal.store.previewNote",
                    "این فروشگاه در محیط پیش‌نمایش ساخته شده و روی یک دامنهٔ داخلی است؛ برای فعال‌سازی دامنهٔ عمومی و دسترسی به پنل مدیریت، با تیم ما هماهنگ کنید.",
                  )}
                </p>
                <ButtonLink variant="outline" to={`/portal/stores/${store.id}`} className="w-fit">
                  {t("portal.store.details")}
                </ButtonLink>
              </>
            )}
            <p className="text-body-sm text-on-surface-variant">
              {t(
                "portal.store.manageHint",
                "محصولات، سفارش‌ها و فیچرهای خریداری‌شده را در «پنل مدیریت فروشگاه» (بخش «افزونه‌ها») مدیریت می‌کنید.",
              )}
            </p>
          </div>
        ) : progress ? (
          <div className="flex flex-col gap-2">
            <Meter label={provisioningStatusText(progress.state, progress.friendlyStatus)} value={progress.percentComplete} />
            <Link to={`/portal/stores/${store.id}`} className="text-body-sm text-primary hover:underline">
              {t("portal.store.watch")}
            </Link>
          </div>
        ) : (
          <LoadingBlock rows={2} />
        )}
      </CardBody>
    </Card>
  );
}

/** A plain-language guide answering the common "where do I…?" questions, right
 *  on the portal home so a shop owner is never lost. */
function PortalGuide() {
  const rows: { q: string; a: string }[] = [
    {
      q: "این پورتال چیست؟",
      a: "پنلِ خودِ شما به‌عنوان صاحب کسب‌وکار: اشتراک، خرید فیچر، ادمین خودکار و دسترسی به فروشگاه‌تان.",
    },
    {
      q: "فروشگاه جدید چطور بسازم؟",
      a: "هر حساب یک فروشگاه دارد که هنگام ثبت‌نام ساخته می‌شود. برای فروشگاه دیگر، حساب جدید در صفحهٔ ثبت‌نام (/signup) بسازید.",
    },
    {
      q: "چطور فیچر بخرم/اضافه کنم و پرداخت کنم؟",
      a: "از «پلن‌ها» (/portal/plans) پلن و فیچرهای دلخواه را انتخاب کنید و «ادامه به پرداخت» را بزنید؛ پرداخت همان‌جاست.",
    },
    {
      q: "محصولات/سفارش‌ها/فیچرهای خریداری‌شده را کجا مدیریت کنم؟",
      a: "در «پنل مدیریت فروشگاه» (دکمهٔ بالا). همهٔ فیچرها آن‌جا در بخش «افزونه‌ها» است — این پورتال فقط ادمین‌خودکار را مستقیم نشان می‌دهد.",
    },
  ];
  return (
    <Card>
      <CardHeader title="راهنما — سؤال‌های پرتکرار" />
      <CardBody className="flex flex-col gap-3">
        {rows.map((r) => (
          <div key={r.q}>
            <p className="text-body-sm font-semibold text-on-surface">{r.q}</p>
            <p className="mt-0.5 text-body-sm leading-6 text-on-surface-variant">{r.a}</p>
          </div>
        ))}
      </CardBody>
    </Card>
  );
}

function AutoAdminCard() {
  const { t } = useTranslation();
  return (
    <Card>
      <CardBody className="flex flex-wrap items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          <span className="grid size-10 place-items-center rounded-lg bg-primary/15 text-primary">
            <Bot className="size-5" aria-hidden />
          </span>
          <div>
            <p className="font-medium text-on-surface">{t("portal.autoAdmin.title", "ادمین خودکار")}</p>
            <p className="text-body-sm text-on-surface-variant">
              {t("portal.autoAdmin.cardBody", "تولید و انتشار خودکار محتوا در کانال‌های فروشگاه‌تان، روی خلبان خودکار.")}
            </p>
          </div>
        </div>
        <ButtonLink variant="outline" to="/portal/auto-admin">
          {t("portal.autoAdmin.open", "باز کردن")}
          <ArrowRight className="size-4 rtl:-scale-x-100" aria-hidden />
        </ButtonLink>
      </CardBody>
    </Card>
  );
}

function PendingStore() {
  const { t } = useTranslation();
  return (
    <Card>
      <CardBody className="flex items-center gap-3 text-on-surface-variant">
        <Loader2 className="size-5 animate-spin" aria-hidden />
        <p className="text-body-sm">{t("portal.store.starting")}</p>
      </CardBody>
    </Card>
  );
}
