import { useParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { CheckCircle2, Circle, Loader2, XCircle, ArrowRight, ArrowLeft } from "lucide-react";
import { Card, CardBody, CardHeader } from "@/components/ui/Card";
import { Meter } from "@/components/ui/Meter";
import { LoadingBlock, ErrorBlock } from "@/components/ui/StateBlock";
import { ApiError } from "@/lib/api/problem";
import { ButtonLink } from "../components";
import { useMyStores, useProvisioning, type MeProvisioningStep } from "../api";

/**
 * One store, and its provisioning as it happens. The step list polls until the
 * store is ready or fails, so a merchant watches their store come up rather than
 * refreshing to find out.
 */
export function PortalStorePage() {
  const { t } = useTranslation();
  const { storeId } = useParams<{ storeId: string }>();
  const stores = useMyStores();
  const provisioning = useProvisioning(storeId);

  if (stores.isLoading || provisioning.isLoading) return <LoadingBlock rows={6} />;

  if (provisioning.isError) {
    const status = provisioning.error instanceof ApiError ? provisioning.error.status : undefined;
    const message = provisioning.error instanceof Error ? provisioning.error.message : String(provisioning.error);
    return <ErrorBlock message={message} status={status} onRetry={() => void provisioning.refetch()} />;
  }

  const store = stores.data?.find((s) => s.id === storeId);
  const progress = provisioning.data;

  return (
    <div className="flex flex-col gap-6">
      <ButtonLink variant="outline" to="/portal" className="w-fit">
        <ArrowLeft className="size-4 rtl:-scale-x-100" aria-hidden />
        {t("portal.store.back")}
      </ButtonLink>

      {store ? (
        <div>
          <h1 className="text-headline font-semibold text-on-surface">{store.name}</h1>
          <p dir="ltr" className="mt-1 text-body-sm text-on-surface-variant">{store.primaryDomain}</p>
        </div>
      ) : null}

      {progress ? (
        <Card>
          <CardHeader title={t("portal.provisioning.title")} />
          <CardBody className="flex flex-col gap-5">
            <div className="flex items-center gap-3">
              {progress.state === "ready" ? (
                <CheckCircle2 className="size-6 text-success" aria-hidden />
              ) : progress.state === "failed" ? (
                <XCircle className="size-6 text-error" aria-hidden />
              ) : (
                <Loader2 className="size-6 animate-spin text-primary" aria-hidden />
              )}
              <p className="text-body font-medium text-on-surface">{progress.friendlyStatus}</p>
            </div>

            <Meter label={t("portal.provisioning.progress")} value={progress.percentComplete / 100} />

            <ol className="flex flex-col gap-2">
              {progress.steps.map((step) => (
                <StepRow key={step.name} step={step} />
              ))}
            </ol>

            {progress.state === "ready" && store ? (
              <ButtonLink href={`https://${store.primaryDomain}`} target="_blank" rel="noreferrer" className="w-fit">
                {t("portal.store.open")}
                <ArrowRight className="size-4 rtl:-scale-x-100" aria-hidden />
              </ButtonLink>
            ) : null}
          </CardBody>
        </Card>
      ) : null}
    </div>
  );
}

function StepRow({ step }: { step: MeProvisioningStep }) {
  const { t } = useTranslation();
  const icon =
    step.status === "succeeded" || step.status === "skipped" ? (
      <CheckCircle2 className="size-4 text-success" aria-hidden />
    ) : step.status === "failed" ? (
      <XCircle className="size-4 text-error" aria-hidden />
    ) : step.status === "running" || step.status === "waiting" ? (
      <Loader2 className="size-4 animate-spin text-primary" aria-hidden />
    ) : (
      <Circle className="size-4 text-on-surface-variant" aria-hidden />
    );

  return (
    <li className="flex items-center gap-2.5 text-body-sm text-on-surface-variant">
      {icon}
      {t(`portal.step.${step.name}`, step.name)}
    </li>
  );
}
