import { useTranslation } from "react-i18next";
import { ShieldAlert } from "lucide-react";

/**
 * Shown where a screen would be if the signed-in operator's role does not grant
 * it. Deliberately calm rather than an error: not being allowed here is a normal
 * state of a limited role, not something that broke. It names no internals — the
 * permission key is the API's business, not a merchant-facing detail.
 */
export function NotAuthorized() {
  const { t } = useTranslation();

  return (
    <div className="flex flex-col items-center justify-center gap-3 p-12 text-center" role="status">
      <span className="grid size-12 place-items-center rounded-full bg-surface-high text-on-surface-variant">
        <ShieldAlert className="size-6" aria-hidden />
      </span>
      <p className="text-title-sm font-semibold text-on-surface">{t("access.notAuthorizedTitle")}</p>
      <p className="max-w-md text-body-sm text-on-surface-variant">{t("access.notAuthorizedBody")}</p>
    </div>
  );
}
