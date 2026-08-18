import { useEffect, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { X } from "lucide-react";

/**
 * Detail surface: a side sheet on desktop (inline-end, so it mirrors in RTL)
 * and a full-height bottom sheet on mobile.
 */
export function Drawer({
  open,
  title,
  subtitle,
  onClose,
  children,
  footer,
}: {
  open: boolean;
  title: string;
  subtitle?: string | undefined;
  onClose: () => void;
  children: ReactNode;
  footer?: ReactNode | undefined;
}) {
  useEffect(() => {
    if (!open) return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onClose]);

  const { t } = useTranslation();
  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50" role="dialog" aria-modal="true" aria-label={title}>
      <button
        type="button"
        aria-label={t("common.close")}
        className="absolute inset-0 bg-black/60"
        onClick={onClose}
      />
      <div
        className={[
          "absolute bg-surface-lowest elevated",
          "inset-x-0 bottom-0 max-h-[88dvh] rounded-t-lg",
          "sm:inset-y-0 sm:end-0 sm:start-auto sm:max-h-none sm:w-[30rem] sm:max-w-[92vw] sm:rounded-none",
          "flex flex-col",
        ].join(" ")}
      >
        <header className="flex items-start justify-between gap-3 border-b border-outline-variant px-5 py-4">
          <div className="min-w-0">
            <h2 className="truncate text-title font-semibold text-on-surface">{title}</h2>
            {subtitle ? (
              <p className="mt-0.5 truncate text-body-sm text-on-surface-variant">{subtitle}</p>
            ) : null}
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label={t("common.close")}
            className="grid size-9 shrink-0 place-items-center rounded-md text-on-surface-variant hover:bg-surface-high"
          >
            <X className="size-5" />
          </button>
        </header>

        <div className="flex-1 overflow-y-auto px-5 py-4">{children}</div>

        {footer ? (
          <footer className="flex flex-wrap items-center justify-end gap-2 border-t border-outline-variant px-5 py-4 pb-[calc(1rem+env(safe-area-inset-bottom))]">
            {footer}
          </footer>
        ) : null}
      </div>
    </div>
  );
}
