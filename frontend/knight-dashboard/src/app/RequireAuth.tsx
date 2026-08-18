import { useEffect, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useAuthStore } from "@/store/auth";
import { LoginPage } from "@/features/auth/pages/LoginPage";

/**
 * Gate for the authenticated shell. This is convenience, not security: the API
 * authorizes every request independently (docs/authorization.md).
 *
 * On first render it tries to restore the session from the refresh cookie, so a
 * page reload continues the session instead of bouncing the user back to login.
 */
export function RequireAuth({ children }: { children: ReactNode }) {
  const { t } = useTranslation();
  const status = useAuthStore((state) => state.status);
  const restore = useAuthStore((state) => state.restore);

  useEffect(() => {
    if (status === "unknown") void restore();
  }, [status, restore]);

  if (status === "unknown") {
    return (
      <div className="grid min-h-dvh place-items-center bg-surface" role="status" aria-live="polite">
        <span className="text-body-sm text-on-surface-variant">{t("common.loading")}</span>
      </div>
    );
  }

  if (status !== "authenticated") return <LoginPage />;

  return <>{children}</>;
}
