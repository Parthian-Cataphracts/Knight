import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ShieldCheck, ShieldAlert, UserPlus } from "lucide-react";
import { useCollection } from "@/lib/api/hooks";
import type { AdminUser, Role } from "@/lib/api/domain";
import { PageShell, PageHeader, Toolbar, FilterTabs, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { StatusChip } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { useAuthStore } from "@/store/auth";
import { formatRelative } from "@/lib/utils/format";

type Tab = "users" | "roles";

export function AccessPage() {
  const { t } = useTranslation();
  const [tab, setTab] = useState<Tab>("users");
  const users = useCollection<AdminUser>("/users");
  const roles = useCollection<Role>("/roles");
  const can = useAuthStore((state) => state.can);

  const tabs = (
    <Toolbar>
      <FilterTabs<Tab>
        value={tab}
        onChange={setTab}
        options={[
          { value: "users", label: t("access.users"), count: (users.data ?? []).length },
          { value: "roles", label: t("access.roles"), count: (roles.data ?? []).length },
        ]}
      />
    </Toolbar>
  );

  const userColumns: Column<AdminUser>[] = [
    {
      key: "user",
      header: t("access.user"),
      render: (row) => (
        <span className="flex flex-col">
          <span className="text-on-surface">{row.displayName}</span>
          <Mono>{row.email}</Mono>
        </span>
      ),
    },
    {
      key: "scope",
      header: t("access.scope"),
      render: (row) =>
        row.scope === "Platform" ? t("access.platform") : (row.customerName ?? t("access.customer")),
    },
    { key: "roles", header: t("access.rolesColumn"), render: (row) => row.roles.join("، ") },
    {
      key: "mfa",
      header: t("access.mfa"),
      render: (row) =>
        row.mfaEnabled ? (
          <span className="flex items-center gap-1.5 text-success">
            <ShieldCheck className="size-4" aria-hidden />
            {t("common.yes")}
          </span>
        ) : (
          <span className="flex items-center gap-1.5 text-warning">
            <ShieldAlert className="size-4" aria-hidden />
            {t("common.no")}
          </span>
        ),
    },
    {
      key: "status",
      header: t("common.status"),
      render: (row) => (
        <StatusChip tone={row.status === "Active" ? "success" : "warning"}>
          {t(`userStatus.${row.status}`)}
        </StatusChip>
      ),
    },
    {
      key: "lastSeen",
      header: t("access.lastSeen"),
      secondary: true,
      render: (row) => (row.lastSeenAt ? formatRelative(row.lastSeenAt) : "—"),
    },
  ];

  const roleColumns: Column<Role>[] = [
    { key: "name", header: t("access.role"), mono: true, render: (row) => row.name },
    {
      key: "scope",
      header: t("access.scope"),
      render: (row) => (row.scope === "Platform" ? t("access.platform") : t("access.customer")),
    },
    {
      key: "system",
      header: t("access.system"),
      render: (row) => (
        <StatusChip tone={row.isSystem ? "info" : "neutral"}>
          {row.isSystem ? t("common.yes") : t("common.no")}
        </StatusChip>
      ),
    },
    {
      key: "permissions",
      header: t("access.permissions"),
      numeric: true,
      render: (row) => row.permissionCount,
    },
    { key: "users", header: t("access.userCount"), numeric: true, render: (row) => row.userCount },
  ];

  return (
    <PageShell>
      <PageHeader
        title={t("nav.access")}
        subtitle={t("access.subtitle")}
        actions={
          can("user.manage") ? (
            <Button size="sm">
              <UserPlus className="size-4" aria-hidden />
              {t("access.invite")}
            </Button>
          ) : undefined
        }
      />

      {tab === "users" ? (
        <CollectionCard query={users} toolbar={tabs}>
          {(rows) => (
            <DataTable
              columns={userColumns}
              rows={rows}
              rowKey={(row) => row.id}
              cardTitle={(row) => row.displayName}
              emptyMessage={t("common.noResults")}
            />
          )}
        </CollectionCard>
      ) : (
        <CollectionCard query={roles} toolbar={tabs}>
          {(rows) => (
            <DataTable
              columns={roleColumns}
              rows={rows}
              rowKey={(row) => row.id}
              cardTitle={(row) => <Mono>{row.name}</Mono>}
              emptyMessage={t("common.noResults")}
            />
          )}
        </CollectionCard>
      )}
    </PageShell>
  );
}
