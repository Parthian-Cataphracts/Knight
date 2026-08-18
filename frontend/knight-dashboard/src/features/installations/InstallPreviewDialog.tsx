import { useState } from "react";
import { useTranslation } from "react-i18next";
import { AlertTriangle, ArrowDown, CheckCircle2, Clock, RefreshCw } from "lucide-react";
import type { InstallPlan } from "@/lib/api/fixtures-detail";
import { Drawer } from "@/components/data/Drawer";
import { KeyValue, Mono } from "@/components/data/PageShell";
import { StatusChip } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";

/**
 * Shown before any install or upgrade: the resolved dependency plan, the
 * compatibility verdict and the migration consequences. An irreversible
 * migration requires typing the store domain to confirm
 * (docs/frontend-architecture.md section 9).
 */
export function InstallPreviewDialog({
  open,
  plan,
  storeName,
  featureName,
  onClose,
  onConfirm,
}: {
  open: boolean;
  plan: InstallPlan | null;
  storeName: string;
  featureName: string;
  onClose: () => void;
  onConfirm: () => void;
}) {
  const { t } = useTranslation();
  const [confirmation, setConfirmation] = useState("");

  if (!plan) return null;

  const needsTypedConfirmation = plan.migrations.required && !plan.migrations.reversible;
  const canConfirm =
    plan.compatible && (!needsTypedConfirmation || confirmation.trim() === storeName);

  return (
    <Drawer
      open={open}
      title={t("installPreview.title")}
      subtitle={`${featureName} → ${storeName}`}
      onClose={() => {
        setConfirmation("");
        onClose();
      }}
      footer={
        <>
          <Button variant="outline" size="sm" onClick={onClose}>
            {t("createCustomer.cancel")}
          </Button>
          <Button size="sm" disabled={!canConfirm} onClick={onConfirm}>
            {t("installPreview.confirm")}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-5">
        <div
          className={`flex items-start gap-2 rounded-md px-3 py-2.5 text-body-sm ${
            plan.compatible ? "bg-success/10 text-success" : "bg-error/10 text-error"
          }`}
        >
          {plan.compatible ? (
            <CheckCircle2 className="mt-0.5 size-4 shrink-0" aria-hidden />
          ) : (
            <AlertTriangle className="mt-0.5 size-4 shrink-0" aria-hidden />
          )}
          <span>{plan.blockingReason ?? plan.verdict}</span>
        </div>

        <section>
          <h3 className="label-caps mb-3 text-on-surface-variant/80">{t("installPreview.plan")}</h3>
          <ol className="flex flex-col gap-2">
            {plan.steps.map((step, index) => (
              <li key={step.slug} className="flex flex-col gap-2">
                <div className="flex items-center justify-between gap-3 rounded-md bg-surface-low px-3.5 py-3">
                  <span className="flex min-w-0 flex-col">
                    <Mono className="text-on-surface">{step.slug}</Mono>
                    <span className="text-body-sm text-on-surface-variant">
                      {t(`installPreview.role_${step.role}`)}
                    </span>
                  </span>
                  <span className="flex shrink-0 items-center gap-2">
                    <Mono>{step.version}</Mono>
                    <StatusChip tone={step.alreadyInstalled ? "neutral" : "info"}>
                      {step.alreadyInstalled
                        ? t("installPreview.alreadyInstalled")
                        : t("installPreview.willInstall")}
                    </StatusChip>
                  </span>
                </div>
                {index < plan.steps.length - 1 ? (
                  <ArrowDown className="mx-auto size-4 text-on-surface-variant/50" aria-hidden />
                ) : null}
              </li>
            ))}
          </ol>
        </section>

        <dl className="divide-y divide-outline-variant">
          <KeyValue label={t("installPreview.migrations")}>
            {plan.migrations.required ? (
              <StatusChip tone={plan.migrations.reversible ? "info" : "warning"}>
                {plan.migrations.reversible
                  ? t("features.reversible")
                  : t("features.irreversible")}
              </StatusChip>
            ) : (
              t("common.no")
            )}
          </KeyValue>
          <KeyValue label={t("installPreview.duration")}>
            <span className="flex items-center justify-end gap-1.5">
              <Clock className="size-4 text-on-surface-variant" aria-hidden />
              <Mono>{plan.migrations.estimatedSeconds}s</Mono>
            </span>
          </KeyValue>
          <KeyValue label={t("installPreview.restart")}>
            <span className="flex items-center justify-end gap-1.5">
              {plan.requiresRestart ? (
                <RefreshCw className="size-4 text-warning" aria-hidden />
              ) : null}
              {plan.requiresRestart ? t("common.yes") : t("common.no")}
            </span>
          </KeyValue>
        </dl>

        {needsTypedConfirmation && plan.compatible ? (
          <div className="flex flex-col gap-2 rounded-md bg-warning/10 p-3.5">
            <p className="flex items-start gap-2 text-body-sm text-warning">
              <AlertTriangle className="mt-0.5 size-4 shrink-0" aria-hidden />
              {t("installPreview.irreversibleWarning")}
            </p>
            <label className="text-body-sm text-on-surface-variant" htmlFor="confirm-store">
              {t("installPreview.typeToConfirm", { name: storeName })}
            </label>
            <input
              id="confirm-store"
              dir="ltr"
              value={confirmation}
              onChange={(event) => setConfirmation(event.target.value)}
              className="h-10 rounded-md border border-outline-variant bg-surface-lowest px-3 font-mono text-body-sm text-on-surface focus:border-primary focus:outline-none"
            />
          </div>
        ) : null}
      </div>
    </Drawer>
  );
}
