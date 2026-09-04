import { useEffect } from "react";
import { BrowserRouter, Routes, Route, Navigate, useLocation } from "react-router-dom";
import { QueryClientProvider } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { queryClient } from "@/lib/api/queryClient";
import { setSessionRefresher, setUnauthorizedHandler } from "@/lib/api/client";
import { useAuthStore } from "@/store/auth";
import { useUiStore } from "@/store/ui";
import { DIRECTION } from "@/i18n";
import { AppLayout } from "@/layouts/AppLayout";
import { RequireAuth } from "./RequireAuth";
import { ActivateAccountPage } from "@/features/auth/pages/ActivateAccountPage";
import { PortalLayout } from "@/features/portal/PortalLayout";
import { PortalHomePage } from "@/features/portal/pages/PortalHomePage";
import { PortalPlansPage } from "@/features/portal/pages/PortalPlansPage";
import { PortalStorePage } from "@/features/portal/pages/PortalStorePage";
import { PortalSignUpPage } from "@/features/portal/pages/PortalSignUpPage";
import { PortalVerifyPage } from "@/features/portal/pages/PortalVerifyPage";
import { featureRoutes } from "./routes";

/**
 * Picks the shell by who is signed in. A customer principal (one bound to a
 * customer) gets the self-service portal; platform staff get the operations
 * dashboard — two separate route trees, never mixed (docs/self-service-saas-plan.md
 * §12, F). A principal on the wrong tree is redirected to its own home rather
 * than shown the other's chrome.
 */
function RoleLayout() {
  const isCustomer = Boolean(useAuthStore((state) => state.user?.customerId));
  const { pathname } = useLocation();
  const onPortal = pathname.startsWith("/portal");

  if (isCustomer && !onPortal) return <Navigate to="/portal" replace />;
  if (!isCustomer && onPortal) return <Navigate to="/" replace />;

  return isCustomer ? <PortalLayout /> : <AppLayout />;
}

/** Keeps <html lang/dir/data-theme> in sync with the UI store. */
function DocumentSettings() {
  const { i18n } = useTranslation();
  const theme = useUiStore((state) => state.theme);
  const locale = useUiStore((state) => state.locale);

  useEffect(() => {
    const root = document.documentElement;
    root.lang = locale;
    root.dir = DIRECTION[locale];
    root.dataset["theme"] = theme;
    if (i18n.language !== locale) void i18n.changeLanguage(locale);
  }, [locale, theme, i18n]);

  return null;
}

export function App() {
  const signOut = useAuthStore((state) => state.signOut);
  const restore = useAuthStore((state) => state.restore);

  useEffect(() => {
    setUnauthorizedHandler(signOut);

    // Renewal goes through the same de-duplicated path session restore uses, so
    // several requests expiring at once produce one refresh rather than a burst
    // that the server reads as a replayed token.
    setSessionRefresher(async () => {
      await restore();

      if (useAuthStore.getState().status !== "authenticated") {
        throw new Error("The session could not be renewed.");
      }
    });
  }, [signOut, restore]);

  return (
    <QueryClientProvider client={queryClient}>
      <DocumentSettings />
      <BrowserRouter>
        <Routes>
          {/* Outside the authenticated shell on purpose: whoever follows an
              invitation link, signs up, or verifies an email has no session yet. */}
          <Route path="/activate" element={<ActivateAccountPage />} />
          <Route path="/signup" element={<PortalSignUpPage />} />
          <Route path="/verify-email" element={<PortalVerifyPage />} />

          <Route
            element={
              <RequireAuth>
                <RoleLayout />
              </RequireAuth>
            }
          >
            {/* Customer self-service portal. */}
            <Route path="/portal" element={<PortalHomePage />} />
            <Route path="/portal/plans" element={<PortalPlansPage />} />
            <Route path="/portal/stores/:storeId" element={<PortalStorePage />} />

            {/* Operations dashboard. */}
            {featureRoutes()}
            <Route path="*" element={<Navigate to="/" replace />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
