import { Outlet, Link, useNavigate } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { Store, LogOut } from "lucide-react";
import { apiRequest } from "@/lib/api/client";
import { useAuthStore } from "@/store/auth";

/**
 * The customer portal shell — deliberately its own layout, not the operations
 * dashboard's sidebar (docs/self-service-saas-plan.md §12, F). A merchant sees a
 * calm header with their store, not a fleet-wide navigation.
 */
export function PortalLayout() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const user = useAuthStore((state) => state.user);
  const signOut = useAuthStore((state) => state.signOut);

  const onSignOut = async () => {
    try {
      await apiRequest<void>("/auth/logout", { method: "POST", body: {} });
    } catch {
      // Signing out locally is what matters; a failed server call must not trap
      // the user in a session they asked to leave.
    }
    signOut();
    navigate("/");
  };

  return (
    <div className="min-h-dvh bg-surface">
      <header className="border-b border-outline-variant bg-surface-low">
        <div className="mx-auto flex max-w-5xl items-center justify-between gap-4 px-4 py-3">
          <Link to="/portal" className="flex items-center gap-2 text-on-surface">
            <span className="grid size-9 place-items-center rounded-lg bg-primary/15 text-primary">
              <Store className="size-5" aria-hidden />
            </span>
            <span className="text-title font-semibold">{t("portal.brand")}</span>
          </Link>

          <div className="flex items-center gap-3">
            {user ? (
              <span className="hidden text-body-sm text-on-surface-variant sm:inline">{user.email}</span>
            ) : null}
            <button
              type="button"
              onClick={() => void onSignOut()}
              className="flex items-center gap-1.5 rounded-md px-2 py-1.5 text-body-sm text-on-surface-variant hover:bg-surface-high hover:text-on-surface"
            >
              <LogOut className="size-4 rtl:-scale-x-100" aria-hidden />
              {t("portal.signOut")}
            </button>
          </div>
        </div>
      </header>

      <main className="mx-auto max-w-5xl px-4 py-8">
        <Outlet />
      </main>
    </div>
  );
}
