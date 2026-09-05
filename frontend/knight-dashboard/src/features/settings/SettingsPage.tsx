import { useTranslation } from "react-i18next";
import { Moon, Sun, Globe } from "lucide-react";
import { PageShell, PageHeader, KeyValue, Mono } from "@/components/data/PageShell";
import { Card, CardBody, CardHeader } from "@/components/ui/Card";
import { Button } from "@/components/ui/Button";
import { StatusChip } from "@/components/ui/StatusChip";
import { NotificationChannels } from "./NotificationChannels";
import { useUiStore } from "@/store/ui";
import { useAuthStore } from "@/store/auth";

export function SettingsPage() {
  const { t } = useTranslation();
  const { theme, toggleTheme } = useUiStore();
  const user = useAuthStore((state) => state.user);

  return (
    <PageShell>
      <PageHeader title={t("nav.settings")} subtitle={t("settings.subtitle")} />

      <NotificationChannels />

      <div className="grid grid-cols-1 gap-6 xl:grid-cols-2">
        <Card>
          <CardHeader title={t("settings.appearance")} />
          <CardBody className="flex flex-col gap-4">
            <div className="flex items-center justify-between gap-3">
              <span className="text-body-sm text-on-surface">{t("settings.theme")}</span>
              <Button variant="outline" size="sm" onClick={toggleTheme}>
                {theme === "dark" ? <Sun className="size-4" /> : <Moon className="size-4" />}
                {t(`settings.theme_${theme}`)}
              </Button>
            </div>
            <div className="flex items-center justify-between gap-3">
              <span className="text-body-sm text-on-surface">{t("settings.language")}</span>
              {/* English-only UI (docs/risks.md §3.6): shown, not chosen. */}
              <span className="flex items-center gap-1.5 text-body-sm text-on-surface-variant">
                <Globe className="size-4" aria-hidden />
                {t("settings.languageEnglish")}
              </span>
            </div>
          </CardBody>
        </Card>

        <Card>
          <CardHeader title={t("settings.account")} />
          <CardBody>
            <dl className="divide-y divide-outline-variant">
              <KeyValue label={t("settings.name")}>{user?.displayName ?? "—"}</KeyValue>
              <KeyValue label={t("auth.email")}>
                <Mono>{user?.email ?? "—"}</Mono>
              </KeyValue>
              <KeyValue label={t("settings.roles")}>{user?.roles.join("، ") ?? "—"}</KeyValue>
              <KeyValue label={t("settings.scope")}>
                {user?.customerId ? t("access.customer") : t("access.platform")}
              </KeyValue>
              <KeyValue label={t("settings.permissions")}>
                {user?.permissions.length ?? 0}
              </KeyValue>
            </dl>
          </CardBody>
        </Card>

        <Card className="xl:col-span-2">
          <CardHeader title={t("settings.environment")} />
          <CardBody>
            <dl className="divide-y divide-outline-variant">
              <KeyValue label={t("settings.apiBaseUrl")}>
                <Mono>{import.meta.env.VITE_API_BASE_URL ?? "/api/v1"}</Mono>
              </KeyValue>
              <KeyValue label={t("settings.mockMode")}>
                <StatusChip tone={import.meta.env.VITE_USE_MOCKS === "true" ? "warning" : "success"}>
                  {import.meta.env.VITE_USE_MOCKS === "true"
                    ? t("settings.mockOn")
                    : t("settings.mockOff")}
                </StatusChip>
              </KeyValue>
            </dl>
            {import.meta.env.VITE_USE_MOCKS === "true" ? (
              <p className="mt-4 rounded-md bg-warning/10 px-3 py-2.5 text-body-sm text-warning">
                {t("settings.mockNote")}
              </p>
            ) : null}
          </CardBody>
        </Card>
      </div>
    </PageShell>
  );
}
