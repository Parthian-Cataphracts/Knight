import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Button } from "./Button";

export function LoadingBlock({ rows = 3 }: { rows?: number }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-3 p-5" role="status" aria-live="polite">
      <span className="sr-only">{t("common.loading")}</span>
      {Array.from({ length: rows }).map((_, index) => (
        <div key={index} className="h-4 animate-pulse rounded bg-surface-highest" />
      ))}
    </div>
  );
}

/**
 * A 404 from the API is not a failure to report as one: several dashboard
 * screens are built against endpoints that later phases deliver (feature
 * delivery, monitoring, incidents), and a red error implies something broke
 * rather than has not been built. Callers pass the status so this stays one
 * decision instead of six.
 */
export function ErrorBlock({
  message,
  status,
  onRetry,
}: {
  message: string;
  status?: number | undefined;
  onRetry?: (() => void) | undefined;
}) {
  const { t } = useTranslation();

  if (status === 404) {
    return (
      <p className="p-5 text-body-sm text-on-surface-variant" role="status">
        {t("common.notAvailableYet")}
      </p>
    );
  }

  return (
    <div className="flex flex-col items-start gap-3 p-5" role="alert">
      <p className="text-body-sm font-medium text-error">{t("common.errorTitle")}</p>
      <p className="text-body-sm text-on-surface-variant">{message}</p>
      {onRetry ? (
        <Button variant="outline" size="sm" onClick={onRetry}>
          {t("common.retry")}
        </Button>
      ) : null}
    </div>
  );
}

export function EmptyBlock({ children }: { children: ReactNode }) {
  return <p className="p-5 text-body-sm text-on-surface-variant">{children}</p>;
}
