import { useEffect } from "react";
import { BrowserRouter, Routes, Route, Navigate } from "react-router-dom";
import { QueryClientProvider } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { queryClient } from "@/lib/api/queryClient";
import { setUnauthorizedHandler } from "@/lib/api/client";
import { useAuthStore } from "@/store/auth";
import { useUiStore } from "@/store/ui";
import { DIRECTION } from "@/i18n";
import { AppLayout } from "@/layouts/AppLayout";
import { RequireAuth } from "./RequireAuth";
import { DashboardPage } from "@/features/dashboard/pages/DashboardPage";
import { PlaceholderPage } from "@/features/shared/PlaceholderPage";

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

const PLACEHOLDER_ROUTES: [path: string, titleKey: string][] = [
  ["/customers", "nav.customers"],
  ["/stores", "nav.stores"],
  ["/features", "nav.features"],
  ["/installations", "nav.installations"],
  ["/plans", "nav.plans"],
  ["/billing", "nav.billing"],
  ["/infrastructure", "nav.infrastructure"],
  ["/monitoring", "nav.monitoring"],
  ["/errors", "nav.errors"],
  ["/incidents", "nav.incidents"],
  ["/logs", "nav.logs"],
  ["/reports", "nav.reports"],
  ["/access", "nav.access"],
  ["/audit", "nav.audit"],
  ["/settings", "nav.settings"],
];

export function App() {
  const signOut = useAuthStore((state) => state.signOut);

  useEffect(() => {
    setUnauthorizedHandler(signOut);
  }, [signOut]);

  return (
    <QueryClientProvider client={queryClient}>
      <DocumentSettings />
      <BrowserRouter>
        <Routes>
          <Route
            element={
              <RequireAuth>
                <AppLayout />
              </RequireAuth>
            }
          >
            <Route path="/" element={<DashboardPage />} />
            {PLACEHOLDER_ROUTES.map(([path, titleKey]) => (
              <Route key={path} path={path} element={<PlaceholderPage titleKey={titleKey} />} />
            ))}
            <Route path="*" element={<Navigate to="/" replace />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
