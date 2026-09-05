import { NavLink } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { Shield, LogOut } from "lucide-react";
import { NAVIGATION } from "./navigation";
import { permissionForPath } from "@/app/permissions";
import { useAuthStore } from "@/store/auth";
import { useUiStore } from "@/store/ui";
import { cn } from "@/lib/utils/cn";

interface SidebarProps {
  collapsed: boolean;
  onNavigate?: () => void;
}

export function Sidebar({ collapsed, onNavigate }: SidebarProps) {
  const { t } = useTranslation();
  const can = useAuthStore((state) => state.can);
  const user = useAuthStore((state) => state.user);
  const signOut = useAuthStore((state) => state.signOut);
  const setMobileNavOpen = useUiStore((state) => state.setMobileNavOpen);

  return (
    <div className="flex h-full flex-col bg-surface-lowest">
      <div
        className={cn(
          "flex items-center gap-3 border-b border-outline-variant px-4 py-5",
          collapsed && "justify-center px-2",
        )}
      >
        <span className="grid size-9 shrink-0 place-items-center rounded-md bg-primary/15 text-primary">
          <Shield className="size-5" aria-hidden />
        </span>
        {!collapsed && (
          <span className="flex min-w-0 flex-col">
            <span className="truncate text-body font-semibold text-on-surface">
              {t("app.name")}
            </span>
            <span className="label-caps truncate text-on-surface-variant">Aegis Command</span>
          </span>
        )}
      </div>

      <nav className="flex-1 overflow-y-auto px-2 py-4" aria-label={t("common.menu")}>
        {NAVIGATION.map((section) => {
          const items = section.items.filter((item) => {
            const permission = permissionForPath(item.to);
            return permission === undefined || can(permission);
          });
          if (items.length === 0) return null;

          return (
            <div key={section.key} className="mb-5 last:mb-0">
              {!collapsed && (
                <p className="label-caps px-3 pb-2 text-on-surface-variant/70">
                  {t(`nav.${section.key}`)}
                </p>
              )}
              <ul className="flex flex-col gap-0.5">
                {items.map((item) => (
                  <li key={item.key}>
                    <NavLink
                      to={item.to}
                      end={item.to === "/"}
                      onClick={() => {
                        setMobileNavOpen(false);
                        onNavigate?.();
                      }}
                      title={collapsed ? t(`nav.${item.key}`) : undefined}
                      className={({ isActive }) =>
                        cn(
                          "flex items-center gap-3 rounded-md px-3 py-2.5 text-body-sm transition-colors",
                          collapsed && "justify-center px-2",
                          isActive
                            ? "bg-primary/15 font-medium text-primary"
                            : "text-on-surface-variant hover:bg-surface-high hover:text-on-surface",
                        )
                      }
                    >
                      <item.icon className="size-5 shrink-0" aria-hidden />
                      {!collapsed && <span className="truncate">{t(`nav.${item.key}`)}</span>}
                    </NavLink>
                  </li>
                ))}
              </ul>
            </div>
          );
        })}
      </nav>

      <div className="border-t border-outline-variant p-3">
        {!collapsed && user ? (
          <p className="truncate px-1 pb-2 text-body-sm text-on-surface-variant">
            {user.displayName}
          </p>
        ) : null}
        <button
          type="button"
          onClick={signOut}
          className={cn(
            "flex w-full items-center gap-3 rounded-md px-3 py-2.5 text-body-sm text-on-surface-variant",
            "hover:bg-surface-high hover:text-on-surface",
            collapsed && "justify-center px-2",
          )}
        >
          <LogOut className="size-5 shrink-0 rtl:-scale-x-100" aria-hidden />
          {!collapsed && t("common.logout")}
        </button>
      </div>
    </div>
  );
}
