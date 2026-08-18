import { useEffect } from "react";
import { Outlet, useLocation } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { Menu, X, Moon, Sun, Search, Bell, PanelLeftClose, PanelLeftOpen, Globe } from "lucide-react";
import { Sidebar } from "./Sidebar";
import { useUiStore } from "@/store/ui";
import { cn } from "@/lib/utils/cn";

export function AppLayout() {
  const { t, i18n } = useTranslation();
  const location = useLocation();
  const { theme, locale, sidebarCollapsed, mobileNavOpen, toggleTheme, setLocale, toggleSidebar, setMobileNavOpen } =
    useUiStore();

  useEffect(() => {
    setMobileNavOpen(false);
  }, [location.pathname, setMobileNavOpen]);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") setMobileNavOpen(false);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [setMobileNavOpen]);

  return (
    <div className="min-h-dvh bg-surface">
      {/* Desktop: persistent sidebar, collapsible to a 64px rail. */}
      <aside
        className={cn(
          "fixed inset-y-0 start-0 z-30 hidden border-e border-outline-variant lg:block",
          sidebarCollapsed ? "w-16" : "w-[17.5rem]",
        )}
      >
        <Sidebar collapsed={sidebarCollapsed} />
      </aside>

      {/* Mobile: off-canvas drawer. */}
      {mobileNavOpen ? (
        <div className="fixed inset-0 z-40 lg:hidden">
          <button
            type="button"
            aria-label={t("common.close")}
            className="absolute inset-0 bg-black/60"
            onClick={() => setMobileNavOpen(false)}
          />
          <div className="absolute inset-y-0 start-0 w-72 max-w-[85vw] elevated">
            <Sidebar collapsed={false} />
          </div>
        </div>
      ) : null}

      <div className={cn("flex min-h-dvh flex-col", sidebarCollapsed ? "lg:ps-16" : "lg:ps-[17.5rem]")}>
        <header className="sticky top-0 z-20 flex h-16 items-center gap-2 border-b border-outline-variant bg-surface-lowest/95 px-4 backdrop-blur sm:px-6">
          <button
            type="button"
            className="grid size-10 place-items-center rounded-md text-on-surface-variant hover:bg-surface-high lg:hidden"
            aria-label={t("common.menu")}
            onClick={() => setMobileNavOpen(!mobileNavOpen)}
          >
            {mobileNavOpen ? <X className="size-5" /> : <Menu className="size-5" />}
          </button>

          <button
            type="button"
            className="hidden size-10 place-items-center rounded-md text-on-surface-variant hover:bg-surface-high lg:grid"
            aria-label={t("common.menu")}
            onClick={toggleSidebar}
          >
            {sidebarCollapsed ? (
              <PanelLeftOpen className="size-5 rtl:-scale-x-100" />
            ) : (
              <PanelLeftClose className="size-5 rtl:-scale-x-100" />
            )}
          </button>

          <div className="relative hidden flex-1 md:block">
            <Search
              className="pointer-events-none absolute inset-y-0 start-3 my-auto size-4 text-on-surface-variant"
              aria-hidden
            />
            <input
              type="search"
              placeholder={t("common.search")}
              aria-label={t("common.search")}
              className="h-10 w-full max-w-md rounded-md border border-outline-variant bg-surface-low ps-9 pe-3 text-body-sm text-on-surface placeholder:text-on-surface-variant/60 focus:border-primary focus:outline-none"
            />
          </div>

          <div className="ms-auto flex items-center gap-1">
            <button
              type="button"
              className="grid size-10 place-items-center rounded-md text-on-surface-variant hover:bg-surface-high"
              aria-label={t("common.language")}
              onClick={() => {
                const next = locale === "fa" ? "en" : "fa";
                setLocale(next);
                void i18n.changeLanguage(next);
              }}
            >
              <Globe className="size-5" />
            </button>
            <button
              type="button"
              className="grid size-10 place-items-center rounded-md text-on-surface-variant hover:bg-surface-high"
              aria-label={t("common.theme")}
              onClick={toggleTheme}
            >
              {theme === "dark" ? <Sun className="size-5" /> : <Moon className="size-5" />}
            </button>
            <button
              type="button"
              className="grid size-10 place-items-center rounded-md text-on-surface-variant hover:bg-surface-high"
              aria-label={t("dashboard.alerts")}
            >
              <Bell className="size-5" />
            </button>
          </div>
        </header>

        <main className="flex-1 px-4 py-6 pb-[calc(1.5rem+env(safe-area-inset-bottom))] sm:px-6">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
