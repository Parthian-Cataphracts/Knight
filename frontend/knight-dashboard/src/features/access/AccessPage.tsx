import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ShieldCheck, ShieldAlert, UserPlus } from "lucide-react";
import { useAction, useCollection } from "@/lib/api/hooks";
import { apiRequest } from "@/lib/api/client";
import type { AdminUser, Role } from "@/lib/api/domain";
import { PageShell, PageHeader, Toolbar, FilterTabs, Mono } from "@/components/data/PageShell";
import { CollectionCard } from "@/components/data/CollectionCard";
import { DataTable, type Column } from "@/components/data/DataTable";
import { StatusChip } from "@/components/ui/StatusChip";
import { Button } from "@/components/ui/Button";
import { TextField } from "@/components/ui/TextField";
import { Drawer } from "@/components/data/Drawer";
import { KeyValue } from "@/components/data/PageShell";
import { useAuthStore } from "@/store/auth";
import { formatRelative } from "@/lib/utils/format";

type Tab = "users" | "roles";

export function AccessPage() {
  const { t } = useTranslation();
  const [tab, setTab] = useState<Tab>("users");
  const users = useCollection<AdminUser>("/users");
  const roles = useCollection<Role>("/roles");
  const can = useAuthStore((state) => state.can);

  const [inviting, setInviting] = useState(false);
  const [selected, setSelected] = useState<AdminUser | null>(null);

  // A one-time password exists in readable form for exactly one response. It is
  // held here to be shown and never fetched again — there is no endpoint that
  // reads it back.
  const [issuedPassword, setIssuedPassword] = useState<string | null>(null);

  const accountAction = useAction<unknown, { id: string; action: string }>(
    ({ id, action }) => ({ path: `/users/${id}/${action}` }),
    ["/users"],
  );

  const resetPassword = useAction<{ temporaryPassword: string }, string>(
    (id) => ({ path: `/users/${id}/password/reset` }),
    ["/users"],
  );

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
            <Button size="sm" onClick={() => setInviting(true)}>
              <UserPlus className="size-4" aria-hidden />
              {t("access.invite")}
            </Button>
          ) : undefined
        }
      />

      <CreateAccountForm
        open={inviting}
        roles={roles.data ?? []}
        onClose={() => setInviting(false)}
        onCreated={(password) => {
          setInviting(false);
          setIssuedPassword(password);
          void users.refetch();
        }}
      />

      {issuedPassword ? (
        <div className="rounded-md border border-warning/40 bg-warning/10 p-4">
          <p className="text-body-sm text-on-surface">{t("access.passwordShownOnce")}</p>
          <p dir="ltr" className="mt-2 break-all font-mono text-label text-on-surface">
            {issuedPassword}
          </p>
          <Button variant="outline" size="sm" className="mt-3" onClick={() => setIssuedPassword(null)}>
            {t("common.dismiss")}
          </Button>
        </div>
      ) : null}

      {tab === "users" ? (
        <CollectionCard query={users} toolbar={tabs}>
          {(rows) => (
            <DataTable
              columns={userColumns}
              rows={rows}
              rowKey={(row) => row.id}
              onRowClick={setSelected}
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

      <Drawer
        open={selected !== null}
        title={selected?.displayName ?? ""}
        subtitle={selected?.email}
        onClose={() => setSelected(null)}
        footer={
          can("user.manage") && selected ? (
            <>
              {selected.status === "Active" ? (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={accountAction.isPending}
                  onClick={() => accountAction.mutate({ id: selected.id, action: "suspend" })}
                >
                  {t("access.suspend")}
                </Button>
              ) : (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={accountAction.isPending}
                  onClick={() => accountAction.mutate({ id: selected.id, action: "activate" })}
                >
                  {t("access.activate")}
                </Button>
              )}

              {selected.mfaEnabled ? (
                <Button
                  variant="outline"
                  size="sm"
                  disabled={accountAction.isPending}
                  onClick={() => accountAction.mutate({ id: selected.id, action: "mfa/reset" })}
                >
                  {t("access.resetMfa")}
                </Button>
              ) : null}

              <Button
                size="sm"
                disabled={resetPassword.isPending}
                onClick={() =>
                  resetPassword.mutate(selected.id, {
                    onSuccess: (result) => {
                      setIssuedPassword(result.temporaryPassword);
                      setSelected(null);
                    },
                  })
                }
              >
                {t("access.resetPassword")}
              </Button>
            </>
          ) : undefined
        }
      >
        {selected ? (
          <div className="flex flex-col gap-4">
            {accountAction.isError || resetPassword.isError ? (
              <p
                role="alert"
                className="rounded-md bg-error-container px-3 py-2 text-body-sm text-on-error-container"
              >
                {(accountAction.error ?? resetPassword.error)?.message}
              </p>
            ) : null}

            <dl className="divide-y divide-outline-variant">
              <KeyValue label={t("access.scope")}>
                {selected.scope === "Platform" ? t("access.platform") : (selected.customerName ?? "—")}
              </KeyValue>
              <KeyValue label={t("access.rolesColumn")}>{selected.roles.join("، ")}</KeyValue>
              <KeyValue label={t("access.mfa")}>
                {selected.mfaEnabled ? t("common.yes") : t("common.no")}
              </KeyValue>
              <KeyValue label={t("common.status")}>{t(`userStatus.${selected.status}`)}</KeyValue>
            </dl>

            {/* Said plainly: resetting is the only recovery path, and it is
                audited precisely because it is also how an account is taken
                over by somebody holding an administrator's session. */}
            <p className="text-body-sm text-on-surface-variant">{t("access.resetHint")}</p>
          </div>
        ) : null}
      </Drawer>
    </PageShell>
  );
}

/**
 * Creating an account.
 *
 * Roles are chosen here rather than afterwards: an account that exists with no
 * roles can sign in and see nothing, which reads as broken rather than
 * unfinished.
 */
function CreateAccountForm({
  open,
  roles,
  onClose,
  onCreated,
}: {
  open: boolean;
  roles: Role[];
  onClose: () => void;
  onCreated: (temporaryPassword: string) => void;
}) {
  const { t } = useTranslation();
  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [roleIds, setRoleIds] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const submit = async () => {
    setSaving(true);
    setError(null);

    try {
      const created = await apiRequest<{ temporaryPassword: string }>("/users", {
        method: "POST",
        body: { email, displayName, roleIds },
      });

      setEmail("");
      setDisplayName("");
      setRoleIds([]);
      onCreated(created.temporaryPassword);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Drawer
      open={open}
      title={t("access.invite")}
      onClose={onClose}
      footer={
        <Button
          size="sm"
          disabled={saving || email.trim().length === 0 || displayName.trim().length === 0}
          onClick={() => void submit()}
        >
          {t("common.save")}
        </Button>
      }
    >
      <div className="flex flex-col gap-4">
        {error ? (
          <p role="alert" className="rounded-md bg-error-container px-3 py-2 text-body-sm text-on-error-container">
            {error}
          </p>
        ) : null}

        <TextField
          label={t("access.email")}
          value={email}
          type="email"
          dir="ltr"
          onChange={(event) => setEmail(event.target.value)}
        />

        <TextField
          label={t("access.displayName")}
          value={displayName}
          onChange={(event) => setDisplayName(event.target.value)}
        />

        <fieldset className="flex flex-col gap-2">
          <legend className="text-body-sm font-medium text-on-surface-variant">
            {t("access.rolesColumn")}
          </legend>
          <div className="flex flex-wrap gap-2">
            {roles.map((role) => (
              <Button
                key={role.id}
                type="button"
                size="sm"
                variant={roleIds.includes(role.id) ? "primary" : "outline"}
                onClick={() =>
                  setRoleIds((current) =>
                    current.includes(role.id)
                      ? current.filter((id) => id !== role.id)
                      : [...current, role.id],
                  )
                }
              >
                {role.name}
              </Button>
            ))}
          </div>
        </fieldset>

        <p className="text-body-sm text-on-surface-variant">{t("access.inviteHint")}</p>
      </div>
    </Drawer>
  );
}
