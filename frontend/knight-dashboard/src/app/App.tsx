import { useEffect } from "react";
import { BrowserRouter, Routes, Route, Navigate } from "react-router-dom";
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
import { featureRoutes } from "./routes";

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
              invitation link has no session yet, and cannot get one until they
              have set a password here. */}
          <Route path="/activate" element={<ActivateAccountPage />} />

          <Route
            element={
              <RequireAuth>
                <AppLayout />
              </RequireAuth>
            }
          >
            {featureRoutes()}
            <Route path="*" element={<Navigate to="/" replace />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
